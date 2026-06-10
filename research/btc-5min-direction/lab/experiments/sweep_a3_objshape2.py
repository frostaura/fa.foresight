"""Internal PRE-WINDOW sweep pass 2: mild objective shaping (a3). NOT a harness run.

Pass 1 (sweep_a3_objshape.py) showed aggressive |fwd|/vol weights HURT at cov
0.025; cb_vn12 marginally above base. This pass probes the mild end:
  blendL_vn12   w = 1 + L * (clip(r/med, 0.2, 5) - 1), r = |fwd|/(rv_cc_12*sqrt(K))
  vn12_07_2     w = clip(r/med, 0.7, 2)
  cb_*          class-rebalanced variants
base re-run as control.
"""
import warnings; warnings.filterwarnings("ignore")
import os, json, time
import numpy as np
import pandas as pd
import lightgbm
from sklearn.isotonic import IsotonicRegression

LAB = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
K = 3
EMB = K + 60
CALIB_FRAC = 0.10
COVS = [0.10, 0.05, 0.025, 0.01]
PRE_END = 1754006400000
WIN_MS = int(25.3 * 86400 * 1000)
N_WIN = 6

def make_model():
    return lightgbm.LGBMClassifier(
        n_estimators=350, learning_rate=0.03, num_leaves=47, max_depth=6,
        min_child_samples=150, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.6, reg_lambda=8.0, random_state=42,
        n_jobs=3, verbose=-1)

def ratio(df, fwd):
    rv = df["rv_cc_12"].values.astype(float)
    r = np.abs(fwd) / (rv * np.sqrt(K) + 1e-12)
    return r / np.median(r)

def classbal(w, y):
    w = w.copy()
    su, sd = w[y == 1].sum(), w[y == 0].sum()
    if su > 0 and sd > 0:
        tgt = (su + sd) / 2.0
        w[y == 1] *= tgt / su
        w[y == 0] *= tgt / sd
    return w

def blend(df, fwd, lam):
    return 1.0 + lam * (np.clip(ratio(df, fwd), 0.2, 5) - 1.0)

CONFIGS = {
    "base":           lambda df, y, fw: np.ones(len(y)),
    "blend25_vn12":   lambda df, y, fw: blend(df, fw, 0.25),
    "blend50_vn12":   lambda df, y, fw: blend(df, fw, 0.50),
    "cb_blend50":     lambda df, y, fw: classbal(blend(df, fw, 0.50), y),
    "vn12_07_2":      lambda df, y, fw: np.clip(ratio(df, fw), 0.7, 2),
    "cb_vn12_07_2":   lambda df, y, fw: classbal(np.clip(ratio(df, fw), 0.7, 2), y),
    "cb_vn12_05_3":   lambda df, y, fw: classbal(np.clip(ratio(df, fw), 0.5, 3), y),
}

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
perwin = {name: [] for name in CONFIGS}

for w in range(N_WIN):
    ws, we = bounds[w], bounds[w + 1]
    te = np.where(valid & (ot >= ws) & (ot < we))[0]
    tr = np.where(valid & (ot < ws - EMB * 300000))[0]
    if len(te) == 0 or len(tr) < 5000:
        print(f"win {w}: skipped"); continue
    cut = int(len(tr) * (1 - CALIB_FRAC))
    fit_idx, cal_idx = tr[:cut], tr[cut:][EMB:]
    yfit, fwfit = yK[fit_idx].astype(int), fwdK[fit_idx]
    dffit = feat.iloc[fit_idx]
    ycal = yK[cal_idx].astype(int)
    yte = yK[te].astype(int)
    for name, fn in CONFIGS.items():
        t0 = time.time()
        sw = np.asarray(fn(dffit, yfit, fwfit), dtype=float)
        model = make_model()
        model.fit(X[fit_idx], yfit, sample_weight=sw)
        p_cal_raw = model.predict_proba(X[cal_idx])[:, 1]
        iso = IsotonicRegression(out_of_bounds="clip"); iso.fit(p_cal_raw, ycal)
        p_cal = iso.transform(p_cal_raw)
        p_te = iso.transform(model.predict_proba(X[te])[:, 1])
        s_cal = np.abs(p_cal - 0.5); s_te = np.abs(p_te - 0.5)
        row = dict(win=w, cfg=name)
        for cov in COVS:
            thr = np.quantile(s_cal, 1 - cov)
            sel = s_te >= thr
            n = int(sel.sum())
            row[f"n_{cov}"] = n
            row[f"hit_{cov}"] = float((((p_te[sel] > 0.5).astype(int)) == yte[sel]).mean()) if n else float("nan")
        perwin[name].append(row)
        print(f"win {w} {name:14s} ({time.time()-t0:5.1f}s)  " +
              " ".join(f"c{cov}: {row[f'hit_{cov}']:.3f}/{row[f'n_{cov}']}" for cov in COVS), flush=True)

print("\n=== POOLED over %d pre-windows ===" % N_WIN)
summary = {}
for name, rows in perwin.items():
    s = {}
    line = f"{name:14s}"
    for cov in COVS:
        tot = sum(r[f"n_{cov}"] for r in rows)
        hit = sum(r[f"hit_{cov}"] * r[f"n_{cov}"] for r in rows if r[f"n_{cov}"]) / max(tot, 1)
        above = sum(1 for r in rows if r[f"n_{cov}"] >= 30 and r[f"hit_{cov}"] > 0.5)
        s[str(cov)] = dict(n=tot, hit=round(hit, 4), wins_above=above)
        line += f"  c{cov}: {hit:.4f}/n={tot}/w>{above}"
    summary[name] = s
    print(line)

with open(os.path.join(LAB, "results", "sweep_a3_objshape2_internal.json"), "w") as f:
    json.dump(dict(perwin=perwin, summary=summary), f, indent=1)
print("done")
