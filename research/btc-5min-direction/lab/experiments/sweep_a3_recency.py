"""Internal PRE-WINDOW sweep for recency weighting (a3). NOT a harness run.

Replicates chaos_harness_v1 geometry on 6 pseudo-windows of 25.3 days each,
all strictly before 2025-08-01 (pre-window data only). Sweeps exponential
recency half-life in {30, 60, 120, 9999} days.

w = 0.5 ** (age_days / half_life), age relative to newest fit row.
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
COVS = [0.10, 0.05, 0.025, 0.01]
PRE_END = 1754006400000  # 2025-08-01
WIN_MS = int(25.3 * 86400 * 1000)
N_WIN = 6
HALF_LIVES = [30, 60, 120, 9999]

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

bounds = [PRE_END - (N_WIN - w) * WIN_MS for w in range(N_WIN + 1)]

results = {hl: {cov: [] for cov in COVS} for hl in HALF_LIVES}
perwin = {hl: [] for hl in HALF_LIVES}

for w in range(N_WIN):
    ws, we = bounds[w], bounds[w + 1]
    te = np.where(valid & (ot >= ws) & (ot < we))[0]
    tr = np.where(valid & (ot < ws - EMB * 300000))[0]
    if len(te) == 0 or len(tr) < 5000:
        print(f"win {w}: skipped (tr={len(tr)})"); continue
    cut = int(len(tr) * (1 - CALIB_FRAC))
    fit_idx, cal_idx = tr[:cut], tr[cut:][EMB:]
    yfit = yK[fit_idx].astype(int); ycal = yK[cal_idx].astype(int)
    yte = yK[te].astype(int)
    newest = ot[fit_idx].max()
    age_days = (newest - ot[fit_idx]) / 86400000.0
    for hl in HALF_LIVES:
        t0 = time.time()
        sw = 0.5 ** (age_days / hl)
        model = make_model()
        model.fit(X[fit_idx], yfit, sample_weight=sw)
        p_cal_raw = model.predict_proba(X[cal_idx])[:, 1]
        iso = IsotonicRegression(out_of_bounds="clip"); iso.fit(p_cal_raw, ycal)
        p_cal = iso.transform(p_cal_raw)
        p_te = iso.transform(model.predict_proba(X[te])[:, 1])
        s_cal = np.abs(p_cal - 0.5); s_te = np.abs(p_te - 0.5)
        line = f"win {w} hl={hl:5d} ({time.time()-t0:5.1f}s)"
        row = {"win": w, "hl": hl}
        for cov in COVS:
            thr = np.quantile(s_cal, 1 - cov)
            sel = s_te >= thr
            n = int(sel.sum())
            if n:
                correct = ((p_te[sel] > 0.5).astype(int) == yte[sel]).astype(int)
                results[hl][cov].extend(correct.tolist())
                hit = float(correct.mean())
            else:
                hit = float("nan")
            row[f"hit_{cov}"] = hit; row[f"n_{cov}"] = n
            line += f"  c{cov}: {hit:.3f}/{n}"
        perwin[hl].append(row)
        print(line, flush=True)

print("\n=== POOLED over 6 pre-windows ===")
summary = {}
for hl in HALF_LIVES:
    s = {}
    line = f"hl={hl:5d}"
    for cov in COVS:
        arr = results[hl][cov]
        n = len(arr); hit = float(np.mean(arr)) if n else float("nan")
        wins_above = sum(1 for r in perwin[hl] if r[f"n_{cov}"] >= 10 and r[f"hit_{cov}"] > 0.5)
        s[str(cov)] = {"n": n, "hit": round(hit, 4), "wins_above_50": wins_above}
        line += f"  c{cov}: {hit:.4f}/n={n}/w>{wins_above}"
    summary[hl] = s
    print(line)

with open(os.path.join(LAB, "results", "sweep_a3_recency_internal.json"), "w") as f:
    json.dump({"perwin": perwin, "summary": {str(k): v for k, v in summary.items()}}, f, indent=1, default=float)
print("done")
