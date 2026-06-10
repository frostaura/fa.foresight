"""Validate the holdout 58-62% confident-subset is REAL, not a fluke: permutation null.
Shuffle labels, repeat the exact single-holdout + top-2.5% gate, and confirm the real
accuracy sits far above the shuffled distribution. K=6 and K=9 on the v5 stack."""
import warnings; warnings.filterwarnings("ignore")
import numpy as np, pandas as pd
from sklearn.isotonic import IsotonicRegression
import lightgbm as lgb

feat = pd.read_pickle("btc_v5_features.pkl")
EXCL = {"y", "fwd_ret", "open_time", "close"}
cols = [c for c in feat.columns if c not in EXCL]
c = feat["close"].values
rng = np.random.default_rng(3)

def gbm():
    return lgb.LGBMClassifier(n_estimators=150, learning_rate=0.03, num_leaves=47, max_depth=6,
        min_child_samples=150, subsample=0.8, subsample_freq=1, colsample_bytree=0.6,
        reg_lambda=8.0, n_jobs=-1, verbose=-1)

def holdout_top_acc(K, shuffle=False, cov=0.025):
    fwdK = np.full(len(c), np.nan); fwdK[:-K] = c[K:] / c[:-K] - 1
    yK = (fwdK > 0).astype(float)
    valid = feat[cols].notna().all(axis=1).values & ~np.isnan(fwdK)
    idx = np.where(valid)[0]
    X = feat[cols].values[idx][::2]; y = yK[idx].astype(int)[::2]
    if shuffle: y = y.copy(); rng.shuffle(y)
    n = len(X); emb = K + 60; cut = int(n * 0.75)
    m = gbm(); m.fit(X[:int(cut*0.85)], y[:int(cut*0.85)])
    cal = slice(int(cut*0.85)+emb, cut-emb)
    iso = IsotonicRegression(out_of_bounds="clip"); iso.fit(m.predict_proba(X[cal])[:,1], y[cal])
    confcal = np.abs(iso.transform(m.predict_proba(X[cal])[:,1]) - .5)
    p = iso.transform(m.predict_proba(X[cut:])[:,1]); yt = y[cut:]
    sel = np.abs(p-.5) >= np.quantile(confcal, 1-cov)
    return ((p[sel]>.5).astype(int)==yt[sel]).mean(), int(sel.sum())

for K in [6]:
    real, n = holdout_top_acc(K)
    null = np.array([holdout_top_acc(K, shuffle=True)[0] for _ in range(5)])
    z = (real - null.mean())/(null.std()+1e-9)
    print(f"K={K}: REAL top2.5% acc={real:.4f} (n={n}) | shuffled null mean={null.mean():.4f} sd={null.std():.4f} max={null.max():.4f} | z={z:.1f}")
    print(f"      -> {'REAL signal' if z>3 else 'fragile'} (95% CI on real ~ +/-{1.96*np.sqrt(real*(1-real)/n):.3f})")
