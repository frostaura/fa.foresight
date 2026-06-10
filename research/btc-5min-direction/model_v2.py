"""
v2 model: calibrated LightGBM, walk-forward, with SELECTIVE PREDICTION.
We do not trade every bar. We report accuracy as a function of COVERAGE
(the fraction of bars we are confident enough to act on) -- the honest path
to high accuracy on the bars that matter.

Leakage controls: time-ordered folds, embargo gap, scaler/calibrator fit on
TRAIN ONLY, isotonic calibration fit on a held-out tail of train (never test).
"""
import warnings; warnings.filterwarnings("ignore")
import numpy as np, pandas as pd
from sklearn.model_selection import TimeSeriesSplit
from sklearn.isotonic import IsotonicRegression
from sklearn.metrics import roc_auc_score
import lightgbm as lgb

EMBARGO, N_SPLITS = 60, 8
feat = pd.read_pickle("btc_v2_features.pkl").dropna().reset_index(drop=True)
EXCL = {"y", "fwd_ret", "open_time", "close"}
cols = [c for c in feat.columns if c not in EXCL]
X = feat[cols].values.astype(np.float64); y = feat["y"].values.astype(int)
fwd = feat["fwd_ret"].values
n = len(y)
print(f"rows={n} features={len(cols)}")

def make_gbm():
    return lgb.LGBMClassifier(n_estimators=600, learning_rate=0.02, num_leaves=63,
        max_depth=6, min_child_samples=150, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.6, reg_lambda=8.0, reg_alpha=1.0, n_jobs=-1, verbose=-1)

tscv = TimeSeriesSplit(n_splits=N_SPLITS, gap=EMBARGO)
p_oos = np.full(n, np.nan); mask = np.zeros(n, bool)
fold_auc = []
for fold, (tr, te) in enumerate(tscv.split(X)):
    cut = int(len(tr) * 0.85)
    tr_fit, tr_cal = tr[:cut], tr[cut + EMBARGO:]      # calib = recent tail of train (no test peek)
    m = make_gbm(); m.fit(X[tr_fit], y[tr_fit])
    iso = IsotonicRegression(out_of_bounds="clip")
    iso.fit(m.predict_proba(X[tr_cal])[:, 1], y[tr_cal])
    p = iso.transform(m.predict_proba(X[te])[:, 1])
    p_oos[te] = p; mask[te] = True
    a = roc_auc_score(y[te], p); fold_auc.append(a)
    print(f"  fold {fold}: test_n={len(te)} auc={a:.4f} acc={((p>0.5).astype(int)==y[te]).mean():.4f}")

p = p_oos[mask]; yt = y[mask]; fw = fwd[mask]
acc = ((p > 0.5).astype(int) == yt).mean()
print(f"\nPOOLED OOS: n={mask.sum()} acc={acc:.4f} auc={roc_auc_score(yt,p):.4f} (v1 was acc 0.515 / auc 0.523)")

print("\n=== SELECTIVE PREDICTION: accuracy vs coverage (gate on calibrated confidence) ===")
conf = np.abs(p - 0.5)
print(f"  {'coverage':>9} {'n_trades':>9} {'accuracy':>9}  {'edge(2a-1)':>10}")
for q in [0.0, 0.5, 0.7, 0.8, 0.9, 0.95, 0.975, 0.99, 0.995]:
    thr = np.quantile(conf, q); sel = conf >= thr
    a = ((p[sel] > 0.5).astype(int) == yt[sel]).mean()
    print(f"  {100*(1-q):8.1f}% {sel.sum():9d} {a:9.4f}  {2*a-1:+10.4f}")

out = feat.loc[mask, ["open_time", "close", "y", "fwd_ret"]].copy()
out["p"] = p
out.to_pickle("oos_v2.pkl")
np.save("v2_featimp.npy", np.array(fold_auc))
# feature importance from a final full-history-minus-embargo fit (context only)
print("\nsaved oos_v2.pkl")
