"""THE TICK TEST: on the same 30-day window, does adding sub-bar microstructure beat
bar-only for 30-min BTC direction? If tick data is the missing signal, AUC jumps."""
import warnings; warnings.filterwarnings("ignore")
import numpy as np, pandas as pd
from sklearn.model_selection import TimeSeriesSplit
from sklearn.metrics import roc_auc_score
import lightgbm as lgb

K = 6
bar = pd.read_pickle("btc_v4_features.pkl")
tick = pd.read_csv("tick_5m_features.csv")
d = bar.merge(tick, on="open_time", how="inner").reset_index(drop=True)
c = d["close"].values
fwdK = np.full(len(c), np.nan); fwdK[:-K] = c[K:] / c[:-K] - 1
d["yK"] = (fwdK > 0).astype(float)
d = d.iloc[:-K].copy()
bar_cols = [x for x in bar.columns if x not in ("y", "fwd_ret", "open_time", "close")]
tick_cols = [x for x in tick.columns if x != "open_time"]
d = d.dropna(subset=bar_cols + tick_cols + ["yK"]).reset_index(drop=True)
y = d["yK"].values.astype(int)
print(f"window bars={len(d)} (~30 days)  up_rate={y.mean():.4f}  bar_feats={len(bar_cols)} tick_feats={len(tick_cols)}")

# quick univariate signal check: corr of each tick feature with forward direction
print("\nunivariate |corr| of tick features with next-30min direction:")
for tcol in tick_cols:
    cc = np.corrcoef(np.nan_to_num(d[tcol].values), y)[0, 1]
    print(f"   {tcol:16s} {cc:+.4f}")

def auc_cv(cols):
    X = d[cols].values.astype(float)
    tscv = TimeSeriesSplit(n_splits=5, gap=K + 30)
    P = np.full(len(X), np.nan); m = np.zeros(len(X), bool)
    for tr, te in tscv.split(X):
        g = lgb.LGBMClassifier(n_estimators=250, learning_rate=0.03, num_leaves=31, max_depth=5,
                               min_child_samples=120, subsample=0.8, subsample_freq=1,
                               colsample_bytree=0.7, reg_lambda=6.0, n_jobs=-1, verbose=-1)
        g.fit(X[tr], y[tr]); P[te] = g.predict_proba(X[te])[:, 1]; m[te] = True
    p = P[m]; yt = y[m]; conf = np.abs(p - .5)
    t5 = conf >= np.quantile(conf, .95)
    return (roc_auc_score(yt, p), ((p > .5).astype(int) == yt).mean(),
            ((p[t5] > .5).astype(int) == yt[t5]).mean())

for name, cols in [("BAR only", bar_cols), ("TICK only", tick_cols), ("BAR + TICK", bar_cols + tick_cols)]:
    auc, acc, a5 = auc_cv(cols)
    print(f"\n{name:11s}: auc={auc:.4f}  acc={acc:.4f}  top5%conf_acc={a5:.4f}")
