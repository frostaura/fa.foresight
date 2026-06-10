"""Leakage battery for the v3 / horizon pipeline (K=6, 30-min direction).
Proves the edge is real signal, not look-ahead. Same logic as audit.py, on v3 features."""
import warnings; warnings.filterwarnings("ignore")
import numpy as np, pandas as pd
from sklearn.model_selection import TimeSeriesSplit
from sklearn.preprocessing import StandardScaler
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import roc_auc_score

K = 6
rng = np.random.default_rng(11)
feat = pd.read_pickle("btc_v3_features.pkl")
EXCL = {"y", "fwd_ret", "open_time", "close"}
cols = [c for c in feat.columns if c not in EXCL]
c = feat["close"].values
fwdK = np.full(len(c), np.nan); fwdK[:-K] = c[K:] / c[:-K] - 1
yK = (fwdK > 0).astype(float)
valid = feat[cols].notna().all(axis=1).values & ~np.isnan(fwdK)
idx = np.where(valid)[0]
X = feat[cols].values[idx][::3]; y = yK[idx].astype(int)[::3]; fw = fwdK[idx][::3]
tscv = TimeSeriesSplit(n_splits=5, gap=K + 60)

def walk(Xm, ym):
    preds, truth = [], []
    for tr, te in tscv.split(Xm):
        sc = StandardScaler().fit(Xm[tr])
        m = LogisticRegression(max_iter=120, C=0.5).fit(sc.transform(Xm[tr]), ym[tr])
        preds.append(m.predict_proba(sc.transform(Xm[te]))[:, 1]); truth.append(ym[te])
    p = np.concatenate(preds); t = np.concatenate(truth)
    return ((p > 0.5).astype(int) == t).mean(), roc_auc_score(t, p)

ra, rauc = walk(X, y)
print(f"REAL (logit, 30-min dir): acc={ra:.4f} auc={rauc:.4f}")

print("\nA. LABEL-PERMUTATION NULL (8 shuffles):")
null = []
for _ in range(8):
    ys = y.copy(); rng.shuffle(ys); null.append(walk(X, ys)[0])
null = np.array(null); mu, sd = null.mean(), null.std()
print(f"   shuffled acc: mean={mu:.4f} sd={sd:.4f} max={null.max():.4f}")
print(f"   REAL is z={(ra-mu)/sd:.1f} SD above null -> {'GENUINE signal' if (ra-mu)/sd>4 else 'WEAK'}")

print("\nB. POSITIVE CONTROL (inject 30-min fwd return):")
la, lauc = walk(np.column_stack([X, fw]), y)
print(f"   acc={la:.4f} auc={lauc:.4f} -> harness detects leakage when present" if lauc > 0.9 else f"   auc={lauc:.4f} unexpected")

print("\nC. NOISE CONTROL (Gaussian features):")
na, nauc = walk(rng.standard_normal(X.shape), y)
print(f"   acc={na:.4f} auc={nauc:.4f} (must be ~0.50)")
