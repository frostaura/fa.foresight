"""fp_diag_bear4 — weight-strength sweep for mechanism C (falling-regime row
upweight), pre-window folds only. w in {1.5, 3.0} on ret_288<-0.01, plus a
graded variant (1 + 1.0*fall + 1.0*crash)."""
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

def mk(seed):
    return lightgbm.LGBMClassifier(n_estimators=350, learning_rate=0.03, num_leaves=47,
        max_depth=6, min_child_samples=150, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.6, reg_lambda=8.0, random_state=seed, n_jobs=6, verbose=-1)

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

def weight_fn_factory(kind):
    if kind == "w15": return lambda idx: np.where(ret288[idx] < -0.01, 1.5, 1.0)
    if kind == "w30": return lambda idx: np.where(ret288[idx] < -0.01, 3.0, 1.0)
    if kind == "graded": return lambda idx: 1.0 + 1.0 * (ret288[idx] < -0.01) + 1.0 * (ret288[idx] < -0.025)
    raise ValueError

for kind in ("w15", "w30", "graded"):
    wf = weight_fn_factory(kind)
    P_cal, P_te = [], []
    for f in cache:
        i = f["fold"]; ws = folds[i]
        tr = np.where(valid & (ot < ws - EMB * 300000))[0]
        cut = int(len(tr) * 0.9); fit_idx, cal_idx = tr[:cut], tr[cut:][EMB:]
        w = wf(fit_idx)
        pc, pt = [], []
        for s in SEEDS:
            m = mk(s); m.fit(X[fit_idx], yK[fit_idx].astype(int), sample_weight=w)
            pc.append(m.predict_proba(X[cal_idx])[:, 1]); pt.append(m.predict_proba(X[f["te"]])[:, 1])
        P_cal.append(np.mean(pc, axis=0)); P_te.append(np.mean(pt, axis=0))
        print(f"{kind} fold {i} done", flush=True)
    run_eval(P_cal, P_te, kind)
print("done")
