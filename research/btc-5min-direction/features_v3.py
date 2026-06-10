"""v3 = v2 features + CROSS-EXCHANGE (Binance vs Coinbase) signal.
All leakage-safe: spread/returns at bar t use only data timestamped <= t."""
import numpy as np, pandas as pd

v2 = pd.read_pickle("btc_v2_features.pkl")
bn = pd.read_csv("btc_5m.csv")[["open_time", "close", "volume"]].rename(columns={"close": "bn_close", "volume": "bn_vol"})
cb = pd.read_csv("coinbase_btc_5m.csv")[["open_time", "close", "volume"]].rename(columns={"close": "cb_close", "volume": "cb_vol"})

m = bn.merge(cb, on="open_time", how="left")
m["cb_close"] = m["cb_close"].ffill(); m["cb_vol"] = m["cb_vol"].fillna(0.0)
bn_r = m["bn_close"].pct_change(fill_method=None)
cb_r = m["cb_close"].pct_change(fill_method=None)

x = pd.DataFrame({"open_time": m["open_time"]})
# Cross-exchange basis (Binance premium over Coinbase), known at t
x["xex_basis"] = m["bn_close"] / m["cb_close"] - 1.0
x["xex_basis_z96"] = (x["xex_basis"] - x["xex_basis"].rolling(96).mean()) / (x["xex_basis"].rolling(96).std() + 1e-12)
x["xex_basis_chg"] = x["xex_basis"].diff()
# Lead-lag: which venue moved more this bar (and its persistence)
x["xex_lead"] = (bn_r - cb_r).values
x["xex_lead_ema6"] = pd.Series(x["xex_lead"]).ewm(span=6).mean().values
x["cb_ret1"] = cb_r.values
x["cb_ret3"] = m["cb_close"].pct_change(3, fill_method=None).values
# Coinbase (USD, often institutional) volume share -> flow regime
x["cb_vol_share"] = (m["cb_vol"] / (m["cb_vol"] + m["bn_vol"] + 1e-9)).values
# basis reversion pressure (negative z = Coinbase rich -> Binance may catch up)
x["xex_revert"] = (-x["xex_basis_z96"]).values

out = v2.merge(x, on="open_time", how="left")
new_cols = [c for c in x.columns if c != "open_time"]
out[new_cols] = out[new_cols].replace([np.inf, -np.inf], np.nan)
out.to_pickle("btc_v3_features.pkl")

clean = out.dropna().reset_index(drop=True)
print(f"v3 rows usable={len(clean)} total_features={len([c for c in out.columns if c not in ('y','fwd_ret','open_time','close')])} new_xexch={len(new_cols)}")
print("new:", new_cols)
print("xex_basis stats (bps):", round(out['xex_basis'].mean()*1e4,2), "mean,", round(out['xex_basis'].std()*1e4,2), "std")
