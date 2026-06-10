"""Internal PRE-WINDOW sweep for a3 ensemble-agreement gating. NOT a harness run.

Mimics chaos_harness_v1 geometry on 4 pseudo-windows ending 2025-08-01
(all data < 1754006400000). Fits LGBM + XGB + logistic once per window,
stores per-model calib/test probabilities, then evaluates gating designs
offline from the cached predictions.
"""
import os, sys, time
import numpy as np
import pandas as pd
import lightgbm as lgb
import xgboost as xgb
from sklearn.linear_model import LogisticRegression
from sklearn.preprocessing import StandardScaler
from sklearn.pipeline import Pipeline
from sklearn.isotonic import IsotonicRegression

LAB = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
K = 3
EMB = K + 60
PRE_END = 1754006400000
WLEN = 2188800000  # same window length as harness
N_WIN = 4
COVS = [0.05, 0.025, 0.01]
CALIB_FRAC = 0.10

def make_lgbm():
    return lgb.LGBMClassifier(n_estimators=350, learning_rate=0.03, num_leaves=47,
        max_depth=6, min_child_samples=150, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.6, reg_lambda=8.0, random_state=42, n_jobs=3, verbose=-1)

def make_xgb():
    return xgb.XGBClassifier(n_estimators=350, learning_rate=0.03, max_depth=6,
        min_child_weight=150, subsample=0.8, colsample_bytree=0.6, reg_lambda=8.0,
        tree_method="hist", random_state=42, n_jobs=3, eval_metric="logloss")

def make_logit():
    return Pipeline([("sc", StandardScaler()),
                     ("lr", LogisticRegression(C=0.5, max_iter=2000, n_jobs=3))])

def main():
    df = pd.read_pickle(os.path.join(LAB, "features_v1.pkl"))
    df = df[df["open_time"] < PRE_END].reset_index(drop=True)
    feats = [c for c in df.columns if c not in ("open_time", "close")]
    c = df["close"].values.astype(float)
    ot = df["open_time"].values.astype(np.int64)
    fwd = np.full(len(c), np.nan); fwd[:-K] = c[K:] / c[:-K] - 1
    y = (fwd > 0).astype(float)
    valid = df[feats].notna().all(axis=1).values & ~np.isnan(fwd)
    X = df[feats].values.astype(np.float64)

    bounds = [PRE_END - (N_WIN - i) * WLEN for i in range(N_WIN + 1)]
    cache = []
    for w in range(N_WIN):
        ws, we = bounds[w], bounds[w + 1]
        te = np.where(valid & (ot >= ws) & (ot < we))[0]
        tr = np.where(valid & (ot < ws - EMB * 300000))[0]
        cut = int(len(tr) * (1 - CALIB_FRAC))
        fit_idx, cal_idx = tr[:cut], tr[cut:][EMB:]
        yfit = y[fit_idx].astype(int)
        t0 = time.time()
        P_cal, P_te = {}, {}
        for nm, mk in [("lgb", make_lgbm), ("xgb", make_xgb), ("log", make_logit)]:
            m = mk(); m.fit(X[fit_idx], yfit)
            P_cal[nm] = m.predict_proba(X[cal_idx])[:, 1]
            P_te[nm] = m.predict_proba(X[te])[:, 1]
            print(f"win {w} {nm} done {time.time()-t0:.0f}s", flush=True)
        move = (c[te[-1]] / c[te[0]] - 1) * 100
        cache.append(dict(w=w, ycal=y[cal_idx].astype(int), yte=y[te].astype(int),
                          P_cal=P_cal, P_te=P_te, move=move))
        np.save(os.path.join(LAB, "experiments", f"a3_cache_w{w}.npy"),
                np.array([cache[-1]], dtype=object), allow_pickle=True)
    print("predictions cached")

    def shape(P, design):
        pl, px, pg = P["lgb"], P["xgb"], P["log"]
        pm = (pl + px + pg) / 3.0
        sl, sx, sg = pl > 0.5, px > 0.5, pg > 0.5
        all3 = (sl == sx) & (sx == sg)
        trees = sl == sx
        if design == "mean":      return pm
        if design == "A_hard3":   return 0.5 + (pm - 0.5) * all3
        if design == "B_soft":    return 0.5 + (pm - 0.5) * np.where(all3, 1.0, 1/3.)
        if design == "C_trees":   return 0.5 + (pm - 0.5) * trees
        if design == "D_treesmean":
            pt = (pl + px) / 2.0
            return 0.5 + (pt - 0.5) * trees
        if design == "lgb_only":  return pl
        raise ValueError(design)

    designs = ["lgb_only", "mean", "A_hard3", "B_soft", "C_trees", "D_treesmean"]
    res = {d: {cov: [] for cov in COVS} for d in designs}
    for cw in cache:
        for d in designs:
            p_cal_raw = shape(cw["P_cal"], d)
            p_te_raw = shape(cw["P_te"], d)
            iso = IsotonicRegression(out_of_bounds="clip")
            iso.fit(p_cal_raw, cw["ycal"])
            p_cal = iso.transform(p_cal_raw); p_te = iso.transform(p_te_raw)
            s_cal = np.abs(p_cal - 0.5); s_te = np.abs(p_te - 0.5)
            for cov in COVS:
                thr = np.quantile(s_cal, 1 - cov)
                sel = s_te >= thr
                n = int(sel.sum())
                hit = float((((p_te[sel] > 0.5).astype(int)) == cw["yte"][sel]).mean()) if n else float("nan")
                res[d][cov].append((n, hit))
    print(f"\n{'design':12s} " + " ".join(f"{'cov'+str(cov):>18s}" for cov in COVS))
    for d in designs:
        line = f"{d:12s} "
        for cov in COVS:
            rows = res[d][cov]
            ntot = sum(n for n, _ in rows)
            hits = sum(n * h for n, h in rows if n) / max(ntot, 1)
            wa = sum(1 for n, h in rows if n >= 10 and h > 0.5)
            line += f"  {hits:.4f}/{ntot:5d}/{wa}w "
        print(line)
    print("\nper-window detail @cov0.025:")
    for d in designs:
        print(d, [(n, None if n == 0 else round(h, 3)) for n, h in res[d][0.025]])

if __name__ == "__main__":
    main()
