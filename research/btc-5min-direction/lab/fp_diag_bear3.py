"""fp_diag_bear3 — offline pre-window test of TRAINING-side bear mechanisms.
Uses fp_diag_bear2_cache.pkl baseline predictions as the generalist reference.

A: interaction features (fall/crash flags x momentum cols) appended, full refit.
B: routed: falling-regime specialist (trained on ret_288 < -0.01 rows only),
   generalist = cached baseline; route by bar's current ret_288.
C: sample-weight: falling-regime rows upweighted x2 (regime emphasis, sign-neutral).
Eval at 5% cov, default |p-0.5| score, harness-like isotonic per fold.
"""
import warnings; warnings.filterwarnings("ignore")
import numpy as np, pandas as pd, lightgbm, os, pickle
from sklearn.isotonic import IsotonicRegression

LAB = os.path.dirname(os.path.abspath(__file__))
K = 4; EMB = K + 60; SEEDS = (101, 102, 103, 104, 105); COV = 0.05
df = pd.read_pickle(os.path.join(LAB, "features_v1.pkl"))
drop = {"open_time", "close", "y", "fwd_ret"}
cols = [c for c in df.columns if c not in drop]
c = df["close"].values.astype(float); ot = df["open_time"].values.astype(np.int64)
fwdK = np.full(len(c), np.nan); fwdK[:-K] = c[K:] / c[:-K] - 1
yK = (fwdK > 0).astype(float)
valid = df[cols].notna().all(axis=1).values & ~np.isnan(fwdK)
X = df[cols].values.astype(np.float64)
folds = pd.date_range("2025-02-01", "2025-08-01", freq="MS").astype(np.int64) // 10**6
cache = pickle.load(open(os.path.join(LAB, "fp_diag_bear2_cache.pkl"), "rb"))
ret288 = df["ret_288"].values

MOM = ["retn_3", "retn_6", "retn_12", "retn_48", "dmi_14", "rsi_14", "ti_ema12",
       "vwap_dev_48", "m1_last_retn", "sma_z_48"]
mom_ix = [cols.index(m) for m in MOM]
r_ix = cols.index("ret_288")

def aug(Xa):
    r = Xa[:, r_ix]
    fall = (r < -0.01).astype(float)[:, None]
    crash = (r < -0.025).astype(float)[:, None]
    M = Xa[:, mom_ix]
    return np.hstack([Xa, fall * M, crash * M, fall, crash])

def mk(seed, nj=6):
    return lightgbm.LGBMClassifier(n_estimators=350, learning_rate=0.03, num_leaves=47,
        max_depth=6, min_child_samples=150, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.6, reg_lambda=8.0, random_state=seed, n_jobs=nj, verbose=-1)

def run_eval(p_cal_raw_f, p_te_raw_f, label):
    hits, fall_hits, crash_hits, per = [], [], [], []
    for f, p_cal_raw, p_te_raw in zip(cache, p_cal_raw_f, p_te_raw_f):
        iso = IsotonicRegression(out_of_bounds="clip")
        iso.fit(p_cal_raw, yK[f["cal"]].astype(int))
        p_cal = iso.transform(p_cal_raw); p_te = iso.transform(p_te_raw)
        s_cal = np.abs(p_cal - 0.5) + 1e-7 * np.abs(p_cal_raw - 0.5)
        s_te = np.abs(p_te - 0.5) + 1e-7 * np.abs(p_te_raw - 0.5)
        thr = np.quantile(s_cal, 1 - COV); sel = s_te >= thr
        t = f["te"][sel]
        cor = ((p_te[sel] > 0.5).astype(int) == yK[t].astype(int)).astype(int)
        hits.extend(cor); per.append((len(cor), round(float(np.mean(cor)), 3)))
        fall_hits.extend(cor[ret288[t] < -0.01]); crash_hits.extend(cor[ret288[t] < -0.025])
    print(f"{label}: n={len(hits)} hit={np.mean(hits):.4f} "
          f"fall={np.mean(fall_hits):.4f}({len(fall_hits)}) "
          f"crash={np.mean(crash_hits):.4f}({len(crash_hits)}) per-fold={per}", flush=True)

# baseline from cache
run_eval([f["p_cal_raw"] for f in cache], [f["p_te_raw"] for f in cache], "BASE     ")

# A: interactions
A_cal, A_te = [], []
for f in cache:
    i = f["fold"]; ws = folds[i]
    tr = np.where(valid & (ot < ws - EMB * 300000))[0]
    cut = int(len(tr) * 0.9); fit_idx, cal_idx = tr[:cut], tr[cut:][EMB:]
    Xf, Xc, Xt = aug(X[fit_idx]), aug(X[cal_idx]), aug(X[f["te"]])
    pc, pt = [], []
    for s in SEEDS:
        m = mk(s); m.fit(Xf, yK[fit_idx].astype(int))
        pc.append(m.predict_proba(Xc)[:, 1]); pt.append(m.predict_proba(Xt)[:, 1])
    A_cal.append(np.mean(pc, axis=0)); A_te.append(np.mean(pt, axis=0))
    print(f"A fold {i} done", flush=True)
run_eval(A_cal, A_te, "A interact")

# B: routed specialist (generalist = cache)
B_cal, B_te = [], []
for f in cache:
    i = f["fold"]; ws = folds[i]
    tr = np.where(valid & (ot < ws - EMB * 300000))[0]
    cut = int(len(tr) * 0.9); fit_idx, cal_idx = tr[:cut], tr[cut:][EMB:]
    sub = fit_idx[ret288[fit_idx] < -0.01]
    pc, pt = [], []
    for s in SEEDS:
        m = mk(s); m.fit(X[sub], yK[sub].astype(int))
        pc.append(m.predict_proba(X[cal_idx])[:, 1]); pt.append(m.predict_proba(X[f["te"]])[:, 1])
    spc, spt = np.mean(pc, axis=0), np.mean(pt, axis=0)
    bc = np.where(ret288[cal_idx] < -0.01, spc, f["p_cal_raw"])
    bt = np.where(ret288[f["te"]] < -0.01, spt, f["p_te_raw"])
    B_cal.append(bc); B_te.append(bt)
    print(f"B fold {i} done (sub n={len(sub)})", flush=True)
run_eval(B_cal, B_te, "B routed  ")

# C: falling rows upweighted x2
C_cal, C_te = [], []
for f in cache:
    i = f["fold"]; ws = folds[i]
    tr = np.where(valid & (ot < ws - EMB * 300000))[0]
    cut = int(len(tr) * 0.9); fit_idx, cal_idx = tr[:cut], tr[cut:][EMB:]
    w = np.where(ret288[fit_idx] < -0.01, 2.0, 1.0)
    pc, pt = [], []
    for s in SEEDS:
        m = mk(s); m.fit(X[fit_idx], yK[fit_idx].astype(int), sample_weight=w)
        pc.append(m.predict_proba(X[cal_idx])[:, 1]); pt.append(m.predict_proba(X[f["te"]])[:, 1])
    C_cal.append(np.mean(pc, axis=0)); C_te.append(np.mean(pt, axis=0))
    print(f"C fold {i} done", flush=True)
run_eval(C_cal, C_te, "C upweight")
print("done")
