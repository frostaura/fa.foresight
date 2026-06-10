"""Does TEMPORAL SEQUENCE structure beat the tree model? Feed an MLP a window of the
last L bars of the most informative raw series (a fixed-window stand-in for a GRU).
Compare pooled OOS AUC vs the GBM baseline (~0.53). Walk-forward, leakage-safe."""
import warnings; warnings.filterwarnings("ignore")
import numpy as np, pandas as pd
from sklearn.model_selection import TimeSeriesSplit
from sklearn.preprocessing import StandardScaler
from sklearn.neural_network import MLPClassifier
from sklearn.metrics import roc_auc_score

K, L = 6, 10
feat = pd.read_pickle("btc_v4_features.pkl")
series = ["ret_1", "ofi", "signed_vol_z24", "perp_lead", "xex_lead", "perp_ofi",
          "ret_eth_1", "vol_24", "sp_basis_chg", "rsi_14"]
series = [s for s in series if s in feat.columns]
c = feat["close"].values
fwdK = np.full(len(c), np.nan); fwdK[:-K] = c[K:] / c[:-K] - 1
yK = (fwdK > 0).astype(float)

# build windowed matrix: for each bar, last L values of each series (causal)
mats = []
for s in series:
    v = feat[s].values.astype(np.float64)
    cols = [np.roll(v, j) for j in range(L)]           # lag 0..L-1
    mats.append(np.column_stack(cols))
W = np.column_stack(mats)                               # n x (len(series)*L)
valid = ~np.isnan(W).any(axis=1) & ~np.isnan(fwdK)
valid[:L] = False
idx = np.where(valid)[0]
X = W[idx][::2]; y = yK[idx][::2].astype(int)          # stride-2 for speed
print(f"seq-MLP: rows={len(X)} window_features={X.shape[1]} (={len(series)} series x {L} lags)")

tscv = TimeSeriesSplit(n_splits=5, gap=K + 60)
preds, truth = [], []
for tr, te in tscv.split(X):
    sc = StandardScaler().fit(X[tr])
    m = MLPClassifier(hidden_layer_sizes=(48, 24), alpha=1e-3, max_iter=60,
                      early_stopping=True, n_iter_no_change=6, random_state=0)
    m.fit(sc.transform(X[tr]), y[tr])
    preds.append(m.predict_proba(sc.transform(X[te]))[:, 1]); truth.append(y[te])
p = np.concatenate(preds); t = np.concatenate(truth)
acc = ((p > 0.5).astype(int) == t).mean(); auc = roc_auc_score(t, p)
conf = np.abs(p - 0.5)
top = conf >= np.quantile(conf, 0.95)
print(f"seq-MLP POOLED: acc={acc:.4f} auc={auc:.4f}  top5%conf acc={((p[top]>0.5).astype(int)==t[top]).mean():.4f}")
print(f"(GBM baseline on same target: auc~0.524-0.528) -> sequence model {'BEATS' if auc>0.532 else 'does NOT beat'} the tree")
