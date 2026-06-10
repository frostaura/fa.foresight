#!/usr/bin/env python3
"""H14 OOD-veto internal sweep, stage 1: cache calibrated predictions.

Faithful replica of chaos_harness_v1 geometry but on PRE-WINDOW data only
(open_time < 2025-08-01). 4 pseudo-windows of 7286 bars (~25.3d) ending at
the pre-window boundary. Baseline K=3 LGBM, isotonic calib on last 10% of
train (purged EMB). Caches p_cal, p_te, y, and df row indices per window.
"""
import os, pickle
import numpy as np
import pandas as pd
import lightgbm

LAB = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PRE_END = 1754006400000
K = 3
EMB = K + 60
WBARS = 7286
NWIN = 6
CALIB_FRAC = 0.10

df = pd.read_pickle(os.path.join(LAB, "features_v1.pkl"))
drop = {"open_time", "close"}
cols = [c for c in df.columns if c not in drop]
c = df["close"].values.astype(float)
ot = df["open_time"].values.astype(np.int64)
fwd = np.full(len(c), np.nan); fwd[:-K] = c[K:] / c[:-K] - 1
y = (fwd > 0).astype(float)
valid = df[cols].notna().all(axis=1).values & ~np.isnan(fwd) & (ot < PRE_END)
X = df[cols].values.astype(np.float64)

pre_idx = np.where(valid)[0]
print("valid pre rows:", len(pre_idx))

# window boundaries in ms: last NWIN*WBARS bars of the pre period (grid is 5m)
bound_ms = [PRE_END - (NWIN - w) * WBARS * 300000 for w in range(NWIN + 1)]
for b in bound_ms:
    print(pd.to_datetime(b, unit="ms"))

cache = []
for w in range(NWIN):
    ws, we = bound_ms[w], bound_ms[w + 1]
    te = np.where(valid & (ot >= ws) & (ot < we))[0]
    tr = np.where(valid & (ot < ws - EMB * 300000))[0]
    cut = int(len(tr) * (1 - CALIB_FRAC))
    fit_idx, cal_idx = tr[:cut], tr[cut:][EMB:]
    model = lightgbm.LGBMClassifier(
        n_estimators=350, learning_rate=0.03, num_leaves=47, max_depth=6,
        min_child_samples=150, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.6, reg_lambda=8.0, random_state=42, n_jobs=3,
        verbose=-1)
    model.fit(X[fit_idx], y[fit_idx].astype(int))
    from sklearn.isotonic import IsotonicRegression
    p_cal_raw = model.predict_proba(X[cal_idx])[:, 1]
    iso = IsotonicRegression(out_of_bounds="clip")
    iso.fit(p_cal_raw, y[cal_idx].astype(int))
    p_cal = iso.transform(p_cal_raw)
    p_te = iso.transform(model.predict_proba(X[te])[:, 1])
    cache.append(dict(w=w, cal_idx=cal_idx, te=te, p_cal=p_cal, p_te=p_te,
                      y_cal=y[cal_idx].astype(int), y_te=y[te].astype(int)))
    base_hit = ((p_te > 0.5).astype(int) == y[te].astype(int)).mean()
    print(f"win {w} n_tr={len(tr)} n_te={len(te)} raw_hit_all={base_hit:.4f}")

with open(os.path.join(LAB, "sweep", "h14_cache.pkl"), "wb") as f:
    pickle.dump(cache, f)
print("cached.")
