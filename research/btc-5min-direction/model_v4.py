"""
v4: K-bar forward direction target + v2 features + calibrated walk-forward +
HONEST selective prediction (confidence threshold chosen on each fold's calibration
slice, NEVER on test). Decision cadence stays 5-min; target = direction over next K bars.
Usage: python model_v4.py [K]
"""
import warnings; warnings.filterwarnings("ignore")
import sys, numpy as np, pandas as pd
from sklearn.model_selection import TimeSeriesSplit
from sklearn.isotonic import IsotonicRegression
from sklearn.metrics import roc_auc_score
import lightgbm as lgb

K = int(sys.argv[1]) if len(sys.argv) > 1 else 6
FEATFILE = sys.argv[2] if len(sys.argv) > 2 else "btc_v2_features.pkl"
TAG = "v4" if "v4" in FEATFILE else ("v3" if "v3" in FEATFILE else "v2")
EMBARGO, N_SPLITS = K + 60, 8
feat = pd.read_pickle(FEATFILE)
EXCL = {"y", "fwd_ret", "open_time", "close"}
cols = [c for c in feat.columns if c not in EXCL]
c = feat["close"].values
fwdK = np.full(len(c), np.nan); fwdK[:-K] = c[K:] / c[:-K] - 1
yK = (fwdK > 0).astype(float)
valid = feat[cols].notna().all(axis=1).values & ~np.isnan(fwdK)
idx = np.where(valid)[0]
X = feat[cols].values[idx]; y = yK[idx]; fw = fwdK[idx]
n = len(idx)
print(f"K={K} ({K*5}min) rows={n} features={len(cols)} up_rate={y.mean():.4f} embargo={EMBARGO}")

def make_gbm():
    return lgb.LGBMClassifier(n_estimators=600, learning_rate=0.02, num_leaves=63,
        max_depth=6, min_child_samples=150, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.6, reg_lambda=8.0, reg_alpha=1.0, n_jobs=-1, verbose=-1)

COVS = [1.0, 0.5, 0.3, 0.2, 0.1, 0.05, 0.025, 0.01]
agg = {cov: [0, 0] for cov in COVS}     # [correct, count] pooled across folds, honest gate
p_oos = np.full(n, np.nan); mask = np.zeros(n, bool)
tscv = TimeSeriesSplit(n_splits=N_SPLITS, gap=EMBARGO)
for fold, (tr, te) in enumerate(tscv.split(X)):
    cut = int(len(tr) * 0.85)
    tr_fit, tr_cal = tr[:cut], tr[cut + EMBARGO:]
    m = make_gbm(); m.fit(X[tr_fit], y[tr_fit])
    iso = IsotonicRegression(out_of_bounds="clip"); iso.fit(m.predict_proba(X[tr_cal])[:, 1], y[tr_cal])
    p_cal = iso.transform(m.predict_proba(X[tr_cal])[:, 1])
    conf_cal = np.abs(p_cal - 0.5)
    p_te = iso.transform(m.predict_proba(X[te])[:, 1]); conf_te = np.abs(p_te - 0.5)
    yte = y[te]
    p_oos[te] = p_te; mask[te] = True
    for cov in COVS:
        thr = np.quantile(conf_cal, 1 - cov)          # threshold from TRAIN calib slice only
        sel = conf_te >= thr
        if sel.sum():
            agg[cov][0] += ((p_te[sel] > 0.5).astype(int) == yte[sel]).sum()
            agg[cov][1] += sel.sum()

p = p_oos[mask]; yt = y[mask]
print(f"POOLED OOS: n={mask.sum()} acc_all={((p>0.5).astype(int)==yt).mean():.4f} auc={roc_auc_score(yt,p):.4f}")
print("\n=== HONEST selective prediction (gate set on train calib, applied to test) ===")
print(f"  {'target_cov':>10} {'n_trades':>9} {'accuracy':>9}  edge")
for cov in COVS:
    cor, cnt = agg[cov]
    a = cor / cnt if cnt else float('nan')
    print(f"  {100*cov:9.1f}% {cnt:9d} {a:9.4f}  {2*a-1:+.4f}")
out = feat.loc[idx[mask], ["open_time", "close"]].copy()
out["y"] = yt; out["fwdK"] = fw[mask]; out["p"] = p
out.to_pickle(f"oos_v4_K{K}_{TAG}.pkl")
print(f"\nsaved oos_v4_K{K}_{TAG}.pkl")
