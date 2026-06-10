"""v4 = v3 features + DERIVATIVES signal (perp basis, funding, perp order-flow).
Orthogonal to spot price -> best shot at lifting base AUC. All leakage-safe."""
import numpy as np, pandas as pd

v3 = pd.read_pickle("btc_v3_features.pkl")                       # has open_time, close=spot
spot = pd.read_csv("btc_5m.csv")[["open_time", "close", "volume"]].rename(columns={"close": "s_close", "volume": "s_vol"})
perp = pd.read_csv("perp_5m.csv").rename(columns={"close": "p_close", "volume": "p_vol",
                                                  "taker_base": "p_tb", "quote_vol": "p_qv"})
fund = pd.read_csv("funding.csv")

m = spot.merge(perp[["open_time", "p_close", "p_vol", "p_tb"]], on="open_time", how="left")
m[["p_close", "p_vol", "p_tb"]] = m[["p_close", "p_vol", "p_tb"]].ffill()
s_r = m["s_close"].pct_change(fill_method=None)
p_r = m["p_close"].pct_change(fill_method=None)

x = pd.DataFrame({"open_time": m["open_time"]})
# Spot-perp basis (perp premium): positive => leveraged longs paying up
x["sp_basis"] = (m["p_close"] - m["s_close"]) / m["s_close"]
x["sp_basis_z288"] = (x["sp_basis"] - x["sp_basis"].rolling(288).mean()) / (x["sp_basis"].rolling(288).std() + 1e-12)
x["sp_basis_chg"] = x["sp_basis"].diff()
# Perp leads spot? return divergence
x["perp_lead"] = (p_r - s_r).values
x["perp_lead_ema6"] = pd.Series(x["perp_lead"]).ewm(span=6).mean().values
# Perp order-flow imbalance & relative activity
x["perp_ofi"] = ((2 * m["p_tb"] - m["p_vol"]) / (m["p_vol"] + 1e-12)).values
x["perp_ofi_ema12"] = pd.Series(x["perp_ofi"]).ewm(span=12).mean().values
x["perp_spot_vol_ratio"] = (m["p_vol"] / (m["s_vol"] + 1e-9)).values

# Funding: align most-recent settled funding (fundingTime <= bar) via backward merge_asof
fund = fund.sort_values("fundingTime")
ff = pd.merge_asof(m[["open_time"]].sort_values("open_time"), fund[["fundingTime", "fundingRate"]],
                   left_on="open_time", right_on="fundingTime", direction="backward")
x["funding"] = ff["fundingRate"].values
x["funding_z"] = ((x["funding"] - x["funding"].rolling(288 * 3).mean()) / (x["funding"].rolling(288 * 3).std() + 1e-12))
x["funding_chg"] = x["funding"].diff()

out = v3.merge(x, on="open_time", how="left")
new_cols = [c for c in x.columns if c != "open_time"]
out[new_cols] = out[new_cols].replace([np.inf, -np.inf], np.nan)
out.to_pickle("btc_v4_features.pkl")
clean = out.dropna().reset_index(drop=True)
print(f"v4 rows usable={len(clean)} total_feats={len([c for c in out.columns if c not in ('y','fwd_ret','open_time','close')])} new_deriv={len(new_cols)}")
print("new:", new_cols)
print("sp_basis bps: mean=%.2f std=%.2f | funding mean=%.6f" % (out['sp_basis'].mean()*1e4, out['sp_basis'].std()*1e4, out['funding'].mean()))
