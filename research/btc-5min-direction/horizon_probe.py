"""Fast probe: which forward horizon K (in 5-min bars) is most predictable?
Target y_K = 1 if close[t+K] > close[t]. Decision cadence stays 5-min; we just
predict a slightly longer window. Single expanding split (70/30) + embargo, fast GBM.
Reports test AUC and accuracy at high-confidence coverage -> shows where 60% lives."""
import warnings; warnings.filterwarnings("ignore")
import numpy as np, pandas as pd
from sklearn.metrics import roc_auc_score
import lightgbm as lgb

feat = pd.read_pickle("btc_v2_features.pkl")
EXCL = {"y", "fwd_ret", "open_time", "close"}
cols = [c for c in feat.columns if c not in EXCL]
c = feat["close"].values
base_valid = feat[cols].notna().all(axis=1).values

def probe(K):
    fwdK = np.full(len(c), np.nan); fwdK[:-K] = c[K:] / c[:-K] - 1
    y = (fwdK > 0).astype(float)
    valid = base_valid & ~np.isnan(fwdK)
    idx = np.where(valid)[0]
    X = feat[cols].values[idx]; yy = y[idx]
    n = len(idx); cut = int(n * 0.7); emb = K + 60
    tr = slice(0, cut - emb); te = slice(cut, n)
    m = lgb.LGBMClassifier(n_estimators=300, learning_rate=0.03, num_leaves=47,
        max_depth=6, min_child_samples=200, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.6, reg_lambda=8.0, reg_alpha=1.0, n_jobs=-1, verbose=-1)
    m.fit(X[tr], yy[tr])
    p = m.predict_proba(X[te])[:, 1]; yte = yy[te]
    auc = roc_auc_score(yte, p)
    conf = np.abs(p - 0.5)
    def acc_at(qq):
        s = conf >= np.quantile(conf, qq)
        return ((p[s] > 0.5).astype(int) == yte[s]).mean(), s.sum()
    a100 = ((p > 0.5).astype(int) == yte).mean()
    a10, n10 = acc_at(0.90); a5, n5 = acc_at(0.95); a2, n2 = acc_at(0.975)
    print(f"K={K:2d} ({K*5:3d}min)  auc={auc:.4f}  acc_all={a100:.4f}  "
          f"acc_top10%={a10:.4f}(n={n10})  acc_top5%={a5:.4f}  acc_top2.5%={a2:.4f}(n={n2})")

print("forward-horizon predictability probe (test = last 30%, embargoed):")
for K in [1, 2, 3, 6, 9, 12, 18, 24, 36]:
    probe(K)
