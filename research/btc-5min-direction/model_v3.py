"""
v3: triple-barrier target + v2 features + calibrated walk-forward + selective prediction.
Target = which volatility-scaled barrier (up/down) is touched first within H bars.
Embargo (60) > H so no train label-window overlaps the test set.
Usage: python model_v3.py [H] [k10]   e.g. python model_v3.py 12 10
"""
import warnings; warnings.filterwarnings("ignore")
import sys, numpy as np, pandas as pd
from sklearn.model_selection import TimeSeriesSplit
from sklearn.isotonic import IsotonicRegression
from sklearn.metrics import roc_auc_score
import lightgbm as lgb

H = int(sys.argv[1]) if len(sys.argv) > 1 else 12
k10 = int(sys.argv[2]) if len(sys.argv) > 2 else 10
EMBARGO, N_SPLITS = max(60, H + 20), 8

feat = pd.read_pickle("btc_v2_features.pkl")
lab = pd.read_pickle(f"tb_{H}_{k10}.pkl")
d = feat.merge(lab, on="open_time", how="inner")
d = d[d["y_tb"] >= 0].dropna(subset=[c for c in feat.columns if c not in ("y","fwd_ret","open_time","close")]).reset_index(drop=True)
EXCL = {"y", "fwd_ret", "open_time", "close", "y_tb", "exit_ret", "touched"}
cols = [c for c in d.columns if c not in EXCL]
X = d[cols].values.astype(np.float64); y = d["y_tb"].values.astype(int)
exit_ret = d["exit_ret"].values
n = len(y)
print(f"H={H} k={k10/10} rows={n} features={len(cols)} up_rate={y.mean():.4f} embargo={EMBARGO}")

def make_gbm():
    return lgb.LGBMClassifier(n_estimators=600, learning_rate=0.02, num_leaves=63,
        max_depth=6, min_child_samples=150, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.6, reg_lambda=8.0, reg_alpha=1.0, n_jobs=-1, verbose=-1)

tscv = TimeSeriesSplit(n_splits=N_SPLITS, gap=EMBARGO)
p_oos = np.full(n, np.nan); mask = np.zeros(n, bool)
for fold, (tr, te) in enumerate(tscv.split(X)):
    cut = int(len(tr) * 0.85)
    tr_fit, tr_cal = tr[:cut], tr[cut + EMBARGO:]
    m = make_gbm(); m.fit(X[tr_fit], y[tr_fit])
    iso = IsotonicRegression(out_of_bounds="clip")
    iso.fit(m.predict_proba(X[tr_cal])[:, 1], y[tr_cal])
    p_oos[te] = iso.transform(m.predict_proba(X[te])[:, 1]); mask[te] = True

p = p_oos[mask]; yt = y[mask]; er = exit_ret[mask]
acc = ((p > 0.5).astype(int) == yt).mean()
print(f"POOLED OOS: n={mask.sum()} acc={acc:.4f} auc={roc_auc_score(yt,p):.4f}")
print("\n  coverage  n_trades  accuracy   edge")
conf = np.abs(p - 0.5)
rows = []
for q in [0.0, 0.5, 0.7, 0.8, 0.9, 0.95, 0.975, 0.99, 0.995]:
    thr = np.quantile(conf, q); sel = conf >= thr
    a = ((p[sel] > 0.5).astype(int) == yt[sel]).mean()
    rows.append((100 * (1 - q), sel.sum(), a))
    print(f"  {100*(1-q):7.1f}% {sel.sum():9d} {a:9.4f}  {2*a-1:+.4f}")
out = d.loc[mask, ["open_time", "close", "y_tb", "exit_ret"]].copy(); out["p"] = p
out.to_pickle(f"oos_v3_{H}_{k10}.pkl")
print(f"\nsaved oos_v3_{H}_{k10}.pkl")
