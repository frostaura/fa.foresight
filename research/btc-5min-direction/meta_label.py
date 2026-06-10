"""Proper META-LABELING (Lopez de Prado): a secondary model predicts P(primary correct)
from the primary's probability + regime features, then we ACT only when the secondary is
confident. Second-stage walk-forward on the primary's OOS predictions (honest: the
secondary is always tested on data after its training window)."""
import warnings; warnings.filterwarnings("ignore")
import numpy as np, pandas as pd
from sklearn.model_selection import TimeSeriesSplit
from sklearn.metrics import roc_auc_score
import lightgbm as lgb

oos = pd.read_pickle("oos_v4_K6_v4.pkl")[["open_time", "y", "p"]].rename(columns={"y": "y6", "p": "p6"})
feat = pd.read_pickle("btc_v4_features.pkl")
d = oos.merge(feat, on="open_time", how="left").reset_index(drop=True)
p = d["p6"].values; y = d["y6"].values
correct = ((p > 0.5).astype(int) == y).astype(int)

regime = ["vol_24", "gk_vol_12", "z_sma_48", "z_sma_24", "1h_trend", "4h_trend", "rsi_14",
          "ofi_ema12", "signed_vol_z24", "trade_intensity", "alt_dispersion", "rel_strength_btc",
          "xex_basis_z96", "sp_basis_z288", "funding_z", "perp_ofi_ema12", "hour_sin", "hour_cos"]
regime = [c for c in regime if c in d.columns]
M = np.column_stack([p, np.abs(p - 0.5)] + [d[c].values for c in regime])
M = np.nan_to_num(M, nan=0.0)
print(f"meta-features: {M.shape[1]}  bars={len(M)}  primary base acc={correct.mean():.4f}")

tscv = TimeSeriesSplit(n_splits=6, gap=66)
meta_p = np.full(len(M), np.nan); mask = np.zeros(len(M), bool)
for tr, te in tscv.split(M):
    s = lgb.LGBMClassifier(n_estimators=300, learning_rate=0.03, num_leaves=31, max_depth=5,
                           min_child_samples=200, subsample=0.8, subsample_freq=1,
                           colsample_bytree=0.7, reg_lambda=5.0, n_jobs=-1, verbose=-1)
    s.fit(M[tr], correct[tr])
    meta_p[te] = s.predict_proba(M[te])[:, 1]; mask[te] = True

mp = meta_p[mask]; yc = correct[mask]; pp = p[mask]; yy = y[mask]
print(f"meta-model AUC for predicting 'primary correct': {roc_auc_score(yc, mp):.4f}")
print("\n=== act only when meta-model says primary is reliable ===")
print(f"  {'coverage':>9} {'n':>7} {'direction_acc':>13}")
for q in [1.0, 0.5, 0.3, 0.2, 0.1, 0.05, 0.025, 0.01]:
    thr = np.quantile(mp, 1 - q); sel = mp >= thr
    a = ((pp[sel] > 0.5).astype(int) == yy[sel]).mean()
    print(f"  {100*q:8.1f}% {int(sel.sum()):7d} {a:13.4f}")
# compare with plain confidence gate at matched coverage
print("\n(for reference, plain confidence gate)")
conf = np.abs(pp - 0.5)
for q in [0.1, 0.05, 0.025, 0.01]:
    sel = conf >= np.quantile(conf, 1 - q)
    print(f"  top {100*q:.1f}% conf: acc={((pp[sel]>0.5).astype(int)==yy[sel]).mean():.4f}")
