"""EXPLORATORY: where is the leakage-clean K=6 model most accurate?
Bucket the OOS predictions by candidate regime variables (all known at decision time)
and by model confidence. Looking for a pocket with >=60% directional accuracy at a
usable frequency. (Anything promising gets train-validated next, to avoid cherry-picking.)"""
import numpy as np, pandas as pd

oos = pd.read_pickle("oos_v4_K6_v3.pkl")            # open_time, close, y, fwdK, p
feat = pd.read_pickle("btc_v3_features.pkl")
d = oos.merge(feat, on="open_time", how="left", suffixes=("", "_f"))
y = d["y"].values; p = d["p"].values
conf = np.abs(p - 0.5)
correct = (p > 0.5).astype(int) == y
n = len(d)
print(f"bars={n}  base acc={correct.mean():.4f}")

def acc_top(mask_metric, frac, also_conf=None, conf_frac=0.5):
    thr = np.quantile(mask_metric, 1 - frac)
    sel = mask_metric >= thr
    if also_conf is not None:
        sel = sel & (also_conf >= np.quantile(also_conf[sel], 1 - conf_frac)) if sel.sum() else sel
    return correct[sel].mean(), int(sel.sum())

conds = {
    "confidence|p-.5|": conf,
    "vol_24": d["vol_24"].abs().values,
    "gk_vol_12": d["gk_vol_12"].values,
    "|xex_basis_z96|": d["xex_basis_z96"].abs().values,
    "|ofi_ema12|": d["ofi_ema12"].abs().values,
    "|1h_trend|": d["1h_trend"].abs().values,
    "|4h_trend|": d["4h_trend"].abs().values,
    "|z_sma_48|": d["z_sma_48"].abs().values,
    "trade_intensity": d["trade_intensity"].values,
    "alt_dispersion": d["alt_dispersion"].values,
    "|rel_strength_btc|": d["rel_strength_btc"].abs().values,
}
print("\n=== accuracy in the TOP magnitude bucket of each regime variable ===")
print(f"  {'regime var':>20} | top20%        top10%        top5%")
for name, metric in conds.items():
    metric = np.nan_to_num(metric, nan=np.nanmedian(metric))
    a20, n20 = acc_top(metric, 0.20); a10, n10 = acc_top(metric, 0.10); a5, n5 = acc_top(metric, 0.05)
    print(f"  {name:>20} | {a20:.4f}(n{n20})  {a10:.4f}(n{n10})  {a5:.4f}(n{n5})")

print("\n=== CONFIDENT calls (top conf within high-regime bins) -> hunting 60% ===")
print(f"  {'regime var':>20} | top30%regime & top-half-conf   top10%regime & top-half-conf")
for name, metric in conds.items():
    if name.startswith("confidence"): continue
    metric = np.nan_to_num(metric, nan=np.nanmedian(metric))
    a1, n1 = acc_top(metric, 0.30, also_conf=conf, conf_frac=0.5)
    a2, n2 = acc_top(metric, 0.10, also_conf=conf, conf_frac=0.5)
    print(f"  {name:>20} |   {a1:.4f} (n={n1})              {a2:.4f} (n={n2})")

# two-way: high confidence AND high cross-exchange dislocation AND high vol
m_cross = np.nan_to_num(d["xex_basis_z96"].abs().values)
m_vol = np.nan_to_num(d["vol_24"].abs().values)
sel = (conf >= np.quantile(conf, 0.80)) & (m_cross >= np.quantile(m_cross, 0.6)) & (m_vol >= np.quantile(m_vol, 0.5))
print(f"\nCOMBO (top20%conf & high-dislocation & high-vol): acc={correct[sel].mean():.4f} n={int(sel.sum())} ({100*sel.mean():.2f}% of bars)")
