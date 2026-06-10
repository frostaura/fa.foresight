"""Faithful pre-window test of the wrapper code path (internal 90/10 calib).

Replicates chaos_harness_v1 mechanics on 4 pre-2025-08-01 windows: trains the
3 wrapper models on the first 90% of the harness-fit slice, per-model isotonic
on the last 10% (purged), then evaluates design variants through harness-style
outer isotonic + calib-quantile thresholds.
"""
import os, time, warnings
warnings.filterwarnings("ignore")
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
WLEN = 2188800000
N_WIN = 4
COVS = [0.05, 0.025, 0.01]
EPS = 1e-4

df = pd.read_pickle(os.path.join(LAB, "features_v1.pkl"))
df = df[df["open_time"] < PRE_END].reset_index(drop=True)
feats = [c for c in df.columns if c not in ("open_time", "close")]
c = df["close"].values.astype(float)
ot = df["open_time"].values.astype(np.int64)
fwd = np.full(len(c), np.nan); fwd[:-K] = c[K:] / c[:-K] - 1
y = (fwd > 0).astype(float)
valid = df[feats].notna().all(axis=1).values & ~np.isnan(fwd)
X = df[feats].values.astype(np.float64)

def mk(nm):
    if nm == "lgb":
        return lgb.LGBMClassifier(n_estimators=350, learning_rate=0.03,
            num_leaves=47, max_depth=6, min_child_samples=150, subsample=0.8,
            subsample_freq=1, colsample_bytree=0.6, reg_lambda=8.0,
            random_state=42, n_jobs=3, verbose=-1)
    if nm == "xgb":
        return xgb.XGBClassifier(n_estimators=350, learning_rate=0.03,
            max_depth=6, min_child_weight=150, subsample=0.8,
            colsample_bytree=0.6, reg_lambda=8.0, tree_method="hist",
            random_state=42, n_jobs=3, eval_metric="logloss")
    return Pipeline([("sc", StandardScaler()),
                     ("lr", LogisticRegression(C=0.5, max_iter=2000))])

bounds = [PRE_END - (N_WIN - i) * WLEN for i in range(N_WIN + 1)]
cache = []
for w in range(N_WIN):
    ws, we = bounds[w], bounds[w + 1]
    te = np.where(valid & (ot >= ws) & (ot < we))[0]
    tr = np.where(valid & (ot < ws - EMB * 300000))[0]
    cut = int(len(tr) * 0.90)
    fit_idx, cal_idx = tr[:cut], tr[cut:][EMB:]   # harness outer split
    yfit = y[fit_idx].astype(int)
    # wrapper-internal split of fit slice
    icut = int(len(fit_idx) * 0.90)
    Xf, yf = X[fit_idx[:icut]], yfit[:icut]
    Xc, yc = X[fit_idx[icut + EMB:]], yfit[icut + EMB:]
    t0 = time.time()
    raw_c, raw_t, cal_c, cal_t = {}, {}, {}, {}
    for nm in ("lgb", "xgb", "log"):
        m = mk(nm); m.fit(Xf, yf)
        iso = IsotonicRegression(out_of_bounds="clip")
        iso.fit(m.predict_proba(Xc)[:, 1], yc)
        raw_c[nm] = m.predict_proba(X[cal_idx])[:, 1]
        raw_t[nm] = m.predict_proba(X[te])[:, 1]
        cal_c[nm] = iso.transform(raw_c[nm])
        cal_t[nm] = iso.transform(raw_t[nm])
        print(f"win {w} {nm} {time.time()-t0:.0f}s", flush=True)
    cache.append(dict(w=w, ycal=y[cal_idx].astype(int), yte=y[te].astype(int),
                      raw_c=raw_c, raw_t=raw_t, cal_c=cal_c, cal_t=cal_t))

def shape(raw, cal, design):
    cm3 = (cal["lgb"] + cal["xgb"] + cal["log"]) / 3.0
    rm3 = (raw["lgb"] + raw["xgb"] + raw["log"]) / 3.0
    s = [cal[nm] > 0.5 for nm in ("lgb", "xgb", "log")]
    a3 = (s[0] == s[1]) & (s[1] == s[2])
    at = s[0] == s[1]
    cm2 = (cal["lgb"] + cal["xgb"]) / 2.0
    rm2 = (raw["lgb"] + raw["xgb"]) / 2.0
    if design == "J_gate3":
        return 0.5 + ((cm3 - 0.5) * a3 + EPS * (rm3 - 0.5)) / (1 + EPS)
    if design == "I_nogate":
        return 0.5 + ((cm3 - 0.5) + EPS * (rm3 - 0.5)) / (1 + EPS)
    if design == "K_trees":
        return 0.5 + ((cm2 - 0.5) * at + EPS * (rm2 - 0.5)) / (1 + EPS)
    if design == "lgb_only":
        return raw["lgb"]
    raise ValueError(design)

designs = ["lgb_only", "I_nogate", "J_gate3", "K_trees"]
res = {d: {cov: [] for cov in COVS} for d in designs}
for cw in cache:
    for d in designs:
        p_cal_raw = shape(cw["raw_c"], cw["cal_c"], d)
        p_te_raw = shape(cw["raw_t"], cw["cal_t"], d)
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

print(f"\n{'design':10s} " + " ".join(f"{'cov'+str(cov):>18s}" for cov in COVS))
for d in designs:
    line = f"{d:10s} "
    for cov in COVS:
        rows = res[d][cov]
        ntot = sum(n for n, _ in rows)
        hits = sum(n * h for n, h in rows if n) / max(ntot, 1)
        wa = sum(1 for n, h in rows if n >= 10 and h > 0.5)
        line += f"  {hits:.4f}/{ntot:5d}/{wa}w "
    print(line)
print("\nper-window detail @cov0.025:")
for d in designs:
    print(f"{d:10s}", [(n, None if n == 0 else round(h, 3)) for n, h in res[d][0.025]])
