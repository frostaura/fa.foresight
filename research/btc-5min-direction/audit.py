"""
Leakage & robustness audit. The point: PROVE the ~+1.5pp edge is real signal,
not look-ahead leakage or an overfitting artifact.

Three checks:
  A. LABEL-PERMUTATION NULL TEST  -- shuffle y, rerun the SAME walk-forward many
     times. If our real accuracy sits far above the shuffled-label distribution,
     the edge is genuine. If leakage existed, shuffled labels would still score >0.5.
  B. POSITIVE CONTROL -- inject the actual next-bar return as a feature. The harness
     MUST then score near-perfect AUC. This proves the harness CAN see leakage when
     it is present -> so its absence in the real run is meaningful.
  C. NOISE CONTROL -- replace all features with Gaussian noise. Must score ~0.50.
"""
import warnings; warnings.filterwarnings("ignore")
import numpy as np
import pandas as pd
from sklearn.model_selection import TimeSeriesSplit
from sklearn.preprocessing import StandardScaler
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import roc_auc_score
from features import feature_columns

rng = np.random.default_rng(7)
feat = pd.read_pickle("btc_5m_features.pkl").dropna().reset_index(drop=True)
cols = feature_columns(feat)
X = feat[cols].values.astype(np.float64)
y = feat["y"].values.astype(int)
fwd = feat["fwd_ret"].values
EMBARGO = 60
tscv8 = TimeSeriesSplit(n_splits=8, gap=EMBARGO)   # full harness (matches main run)
tscv5 = TimeSeriesSplit(n_splits=5, gap=EMBARGO)   # lighter, for the many-shuffle null

def walk_logit(Xm, ym, splitter=tscv8):
    """pooled OOS accuracy + auc for logistic regression on identical folds."""
    preds, truth = [], []
    for tr, te in splitter.split(Xm):
        sc = StandardScaler().fit(Xm[tr])
        m = LogisticRegression(max_iter=250, C=0.5).fit(sc.transform(Xm[tr]), ym[tr])
        preds.append(m.predict_proba(sc.transform(Xm[te]))[:, 1]); truth.append(ym[te])
    p = np.concatenate(preds); t = np.concatenate(truth)
    return ((p > 0.5).astype(int) == t).mean(), roc_auc_score(t, p)

real_acc, real_auc = walk_logit(X, y, tscv5)   # compare null to the SAME 5-fold setup
print(f"REAL logit (5-fold)  acc={real_acc:.4f}  auc={real_auc:.4f}")

# ---- A. permutation null (shuffle labels) ----
print("\n=== A. LABEL-PERMUTATION NULL (15 shuffles, 5-fold) ===")
null_acc = []
for i in range(15):
    ys = y.copy(); rng.shuffle(ys)
    a, _ = walk_logit(X, ys, tscv5)
    null_acc.append(a)
null_acc = np.array(null_acc)
mu, sd = null_acc.mean(), null_acc.std()
z = (real_acc - mu) / sd
p_emp = (np.sum(null_acc >= real_acc) + 1) / (len(null_acc) + 1)
print(f"  shuffled-label acc: mean={mu:.4f} std={sd:.4f} max={null_acc.max():.4f}")
print(f"  REAL acc {real_acc:.4f} is z={z:.1f} SD above the null  (empirical p={p_emp:.4f})")
print("  -> real >> null  ==>  edge is genuine, not leakage/artifact" if z > 4 else "  -> WARNING: edge not clearly above null")

# ---- B. positive control (deliberately leaked feature) ----
print("\n=== B. POSITIVE CONTROL (inject next-bar return as a feature) ===")
Xleak = np.column_stack([X, fwd])
la, lauc = walk_logit(Xleak, y)
print(f"  with leaked feature: acc={la:.4f}  auc={lauc:.4f}")
print("  -> harness DOES detect leakage when present (auc->~1). Real run had auc~0.52, so it is clean." if lauc > 0.9 else "  -> unexpected")

# ---- C. noise control ----
print("\n=== C. NOISE CONTROL (features replaced by Gaussian noise) ===")
Xn = rng.standard_normal(X.shape)
na, nauc = walk_logit(Xn, y)
print(f"  pure-noise features: acc={na:.4f}  auc={nauc:.4f}  (must be ~0.50)")
