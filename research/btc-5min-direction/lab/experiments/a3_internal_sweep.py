"""Internal PRE-WINDOW sweep for a3 magnitude-conditioned training lever.

NOT a harness run. Mirrors chaos_harness_v1 geometry (fit/calib split, purge,
isotonic, calib-quantile threshold) but uses ONLY rows before 2025-08-01
(open_time < 1754006400000), split into 5 pseudo-windows of 24 days each.

Sweeps: baseline, hard mask theta in {0.25,0.5,1.0} on |fwd| >= theta*rv_cc_48*sqrt(K),
soft weight |fwd|/(rv*sqrtK) clipped at 5, and mask+weight combos.
"""
import warnings; warnings.filterwarnings("ignore")
import os, sys, json, time
import numpy as np
import pandas as pd
import lightgbm
from sklearn.isotonic import IsotonicRegression

LAB = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
K = 3
EMB = K + 60
CALIB_FRAC = 0.10
PRE_END = 1754006400000
WIN_MS = 24 * 86400 * 1000
N_WIN = 5
BOUNDS = [PRE_END - (N_WIN - i) * WIN_MS for i in range(N_WIN + 1)]
COVS = [0.05, 0.025, 0.01]
SEED = 42

def make_model():
    return lightgbm.LGBMClassifier(
        n_estimators=350, learning_rate=0.03, num_leaves=47, max_depth=6,
        min_child_samples=150, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.6, reg_lambda=8.0, random_state=42,
        n_jobs=3, verbose=-1)

feat = pd.read_pickle(os.path.join(LAB, "features_v1.pkl"))
feat = feat[feat["open_time"] < PRE_END].reset_index(drop=True)
drop_always = {"open_time", "close", "y", "fwd_ret"}
cols = [c for c in feat.columns if c not in drop_always]
c = feat["close"].values.astype(float)
ot = feat["open_time"].values.astype(np.int64)
fwdK = np.full(len(c), np.nan); fwdK[:-K] = c[K:] / c[:-K] - 1
yK = (fwdK > 0).astype(float)
valid = feat[cols].notna().all(axis=1).values & ~np.isnan(fwdK)
X = feat[cols].values.astype(np.float64)
rv = feat["rv_cc_48"].values.astype(float)
SQ = np.sqrt(K)

CONFIGS = [
    ("base",       None, False),
    ("hard025",    0.25, False),
    ("hard05",     0.50, False),
    ("hard10",     1.00, False),
    ("soft",       None, True),
    ("hard025_w",  0.25, True),
    ("hard05_w",   0.50, True),
]

results = {}
for cname, theta, use_w in CONFIGS:
    np.random.seed(SEED)
    trades = {cov: [] for cov in COVS}
    perwin = []
    for w in range(N_WIN):
        ws, we = BOUNDS[w], BOUNDS[w + 1]
        te = np.where(valid & (ot >= ws) & (ot < we))[0]
        tr = np.where(valid & (ot < ws - EMB * 300000))[0]
        cut = int(len(tr) * (1 - CALIB_FRAC))
        fit_idx, cal_idx = tr[:cut], tr[cut:][EMB:]
        ftr = fit_idx
        yfit, fwfit = yK[ftr].astype(int), fwdK[ftr]
        rvfit = rv[ftr]
        if theta is not None:
            m = np.abs(fwfit) >= theta * rvfit * SQ
            ftr, yfit, fwfit, rvfit = ftr[m], yfit[m], fwfit[m], rvfit[m]
        kw = {}
        if use_w:
            kw["sample_weight"] = np.clip(np.abs(fwfit) / (rvfit * SQ + 1e-12), 0, 5.0)
        model = make_model()
        model.fit(X[ftr], yfit, **kw)
        p_cal_raw = model.predict_proba(X[cal_idx])[:, 1]
        iso = IsotonicRegression(out_of_bounds="clip")
        iso.fit(p_cal_raw, yK[cal_idx].astype(int))
        p_cal = iso.transform(p_cal_raw)
        p_te = iso.transform(model.predict_proba(X[te])[:, 1])
        s_cal = np.abs(p_cal - 0.5); s_te = np.abs(p_te - 0.5)
        yte = yK[te].astype(int)
        row = dict(win=w, n_fit=len(ftr))
        for cov in COVS:
            thr = np.quantile(s_cal, 1 - cov)
            sel = s_te >= thr
            n = int(sel.sum())
            if n:
                cor = ((p_te[sel] > 0.5).astype(int) == yte[sel]).astype(int)
                row[f"hit_{cov}"] = float(cor.mean()); row[f"n_{cov}"] = n
                trades[cov].extend(cor.tolist())
            else:
                row[f"hit_{cov}"] = float("nan"); row[f"n_{cov}"] = 0
        perwin.append(row)
        print(f"[{cname}] win{w} nfit={len(ftr):6d} " +
              " ".join(f"c{cov}:{row[f'hit_{cov}']:.3f}/{row[f'n_{cov}']}" for cov in COVS),
              flush=True)
    pooled = {cov: (float(np.mean(trades[cov])), len(trades[cov])) for cov in COVS}
    wins_above = {cov: sum(1 for r in perwin
                           if r[f"n_{cov}"] >= 10 and r[f"hit_{cov}"] > 0.5) for cov in COVS}
    results[cname] = dict(pooled={str(k): v for k, v in pooled.items()},
                          wins_above={str(k): v for k, v in wins_above.items()},
                          perwin=perwin)
    print(f"== {cname} POOLED: " +
          " ".join(f"c{cov}: {pooled[cov][0]:.4f}/n={pooled[cov][1]} w>{wins_above[cov]}/5"
                   for cov in COVS), flush=True)

with open(os.path.join(LAB, "results", "a3_internal_sweep.json"), "w") as f:
    json.dump(results, f, indent=1)
print("done")
