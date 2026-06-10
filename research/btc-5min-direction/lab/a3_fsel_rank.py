#!/usr/bin/env python3
"""a3 feature-selection ranking — PRE-WINDOW data only (ot < 2025-08-01).

Ranks 163 features by LGBM gain importance averaged over 3 expanding splits at K=3
(baseline LGBM config), then runs a permutation check (AUC drop on a purged
validation tail) on the top-60 gain candidates. Writes a3_fsel_rank.json.
Selection tool only — produces no chaos numbers.
"""
import warnings; warnings.filterwarnings("ignore")
import os, json, time
import numpy as np
import pandas as pd
import lightgbm as lgb
from sklearn.metrics import roc_auc_score

LAB = os.path.dirname(os.path.abspath(__file__))
K = 3
EMB = K + 60
WALL = 1754006400000  # 2025-08-01

def model():
    return lgb.LGBMClassifier(n_estimators=350, learning_rate=0.03, num_leaves=47,
                              max_depth=6, min_child_samples=150, subsample=0.8,
                              subsample_freq=1, colsample_bytree=0.6, reg_lambda=8.0,
                              random_state=42, n_jobs=3, verbose=-1)

def main():
    t0 = time.time()
    df = pd.read_pickle(os.path.join(LAB, "features_v1.pkl"))
    cols = [c for c in df.columns if c not in ("open_time", "close")]
    c = df["close"].values.astype(float)
    ot = df["open_time"].values.astype(np.int64)
    fwd = np.full(len(c), np.nan); fwd[:-K] = c[K:] / c[:-K] - 1
    y = (fwd > 0).astype(float)
    valid = df[cols].notna().all(axis=1).values & ~np.isnan(fwd)
    pre = np.where(valid & (ot < WALL))[0]
    X = df[cols].values.astype(np.float64)
    del df
    n = len(pre)
    print(f"pre-window valid rows: {n}", flush=True)

    # --- gain importance over 3 expanding splits ---
    gains = []
    for frac in (0.55, 0.70, 0.85):
        idx = pre[: int(n * frac)]
        m = model(); m.fit(X[idx], y[idx].astype(int))
        g = m.booster_.feature_importance(importance_type="gain").astype(float)
        g = g / g.sum()
        gains.append(g)
        print(f"split frac={frac} fit n={len(idx)} done {time.time()-t0:.0f}s", flush=True)
    gain_avg = np.mean(gains, axis=0)
    order = np.argsort(-gain_avg)
    rank_gain = [(cols[i], float(gain_avg[i])) for i in order]

    # --- permutation check on top-60 gain candidates ---
    # fit on first 85% of pre, validate on last 15% purged by EMB
    cut = int(n * 0.85)
    fit_idx = pre[:cut]
    val_idx = pre[cut + EMB:]
    mperm = model(); mperm.fit(X[fit_idx], y[fit_idx].astype(int))
    Xv = X[val_idx].copy(); yv = y[val_idx].astype(int)
    base_auc = roc_auc_score(yv, mperm.predict_proba(Xv)[:, 1])
    print(f"perm base AUC={base_auc:.5f} on n_val={len(val_idx)}", flush=True)
    rng = np.random.default_rng(42)
    top60 = [cols[i] for i in order[:60]]
    perm = {}
    for f in top60:
        j = cols.index(f)
        saved = Xv[:, j].copy()
        drops = []
        for _ in range(3):
            Xv[:, j] = rng.permutation(saved)
            drops.append(base_auc - roc_auc_score(yv, mperm.predict_proba(Xv)[:, 1]))
        Xv[:, j] = saved
        perm[f] = float(np.mean(drops))
    print(f"permutation done {time.time()-t0:.0f}s", flush=True)

    out = dict(n_pre=int(n), base_auc=float(base_auc),
               rank_gain=rank_gain, perm_auc_drop=perm)
    with open(os.path.join(LAB, "a3_fsel_rank.json"), "w") as f:
        json.dump(out, f, indent=1)
    print("\nTOP 30 by avg gain (perm AUC drop in 1e-4):")
    for fname, g in rank_gain[:30]:
        pd_ = perm.get(fname)
        print(f"  {fname:28s} gain={g:.4f} permDrop={pd_*1e4:+.2f}" if pd_ is not None
              else f"  {fname:28s} gain={g:.4f}")
    print(f"elapsed {time.time()-t0:.0f}s")

if __name__ == "__main__":
    main()
