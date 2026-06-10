"""Single recent-holdout evaluation on the full v5 stack (peer-comparable protocol).
Train on first 75%, calibrate on a tail slice of train, test on the most recent 25%.
Confidence threshold chosen on the calibration slice (no test peeking). Reports the
confident-subset accuracy for K=6 and the K=3/6/9 agreement -- the apples-to-apples
number vs a 59% single-holdout benchmark. Leakage discipline identical to the WF runs."""
import warnings; warnings.filterwarnings("ignore")
import numpy as np, pandas as pd
from sklearn.isotonic import IsotonicRegression
from sklearn.metrics import roc_auc_score
import lightgbm as lgb

feat = pd.read_pickle("btc_v5_features.pkl")
EXCL = {"y", "fwd_ret", "open_time", "close"}
cols = [c for c in feat.columns if c not in EXCL]
c = feat["close"].values
def make_gbm():
    return lgb.LGBMClassifier(n_estimators=600, learning_rate=0.02, num_leaves=63, max_depth=6,
        min_child_samples=150, subsample=0.8, subsample_freq=1, colsample_bytree=0.6,
        reg_lambda=8.0, reg_alpha=1.0, n_jobs=-1, verbose=-1)

def fit_predict_K(K):
    fwdK = np.full(len(c), np.nan); fwdK[:-K] = c[K:] / c[:-K] - 1
    yK = (fwdK > 0).astype(float)
    valid = feat[cols].notna().all(axis=1).values & ~np.isnan(fwdK)
    idx = np.where(valid)[0]
    X = feat[cols].values[idx]; y = yK[idx].astype(int)
    n = len(idx); emb = K + 60
    cut = int(n * 0.75)
    tr_fit = slice(0, int(cut * 0.85)); tr_cal = slice(int(cut * 0.85) + emb, cut - emb)
    te = slice(cut, n)
    m = make_gbm(); m.fit(X[tr_fit], y[tr_fit])
    iso = IsotonicRegression(out_of_bounds="clip"); iso.fit(m.predict_proba(X[tr_cal])[:, 1], y[tr_cal])
    p_cal = iso.transform(m.predict_proba(X[tr_cal])[:, 1])
    p_te = iso.transform(m.predict_proba(X[te])[:, 1])
    return p_te, y[te], np.abs(p_cal - 0.5)

P = {}; thr_cal = {}
yref = None
for K in [3, 6, 9]:
    p, yk, confcal = fit_predict_K(K); P[K] = (p, yk, confcal)
    if K == 6: yref = yk

print("=== SINGLE RECENT-HOLDOUT (last 25% ~ 3.7 months), v5 stack ===")
for K in [3, 6, 9]:
    p, yk, confcal = P[K]
    auc = roc_auc_score(yk, p); conf = np.abs(p - 0.5)
    print(f"\nK={K} ({K*5}min) holdout AUC={auc:.4f} acc_all={((p>.5).astype(int)==yk).mean():.4f}")
    for q in [0.10, 0.05, 0.025, 0.01]:
        t = np.quantile(confcal, 1 - q); sel = conf >= t   # threshold from TRAIN calib
        if sel.sum() > 20:
            print(f"   gate top~{100*q:4.1f}% (n={int(sel.sum()):5d}): acc={((p[sel]>.5).astype(int)==yk[sel]).mean():.4f}")

# K=3/6/9 agreement on the common holdout length
L = min(len(P[3][0]), len(P[6][0]), len(P[9][0]))
p3, p6, p9 = P[3][0][-L:], P[6][0][-L:], P[9][0][-L:]
yk = P[6][1][-L:]
agree = (np.sign(p3 - .5) == np.sign(p6 - .5)) & (np.sign(p6 - .5) == np.sign(p9 - .5))
side = (p6 > .5).astype(int); conf = np.minimum.reduce([np.abs(p3 - .5), np.abs(p6 - .5), np.abs(p9 - .5)])
print("\n=== K=3/6/9 AGREEMENT on holdout ===")
print(f"agreement coverage={agree.mean():.3f} acc={(side[agree]==yk[agree]).mean():.4f}")
for q in [0.20, 0.10, 0.05, 0.025]:
    m = agree & (conf >= np.quantile(conf[agree], 1 - q))
    if m.sum() > 20:
        print(f"   agree & top~{100*q:4.1f}% conf (n={int(m.sum()):5d}): acc={(side[m]==yk[m]).mean():.4f}")
