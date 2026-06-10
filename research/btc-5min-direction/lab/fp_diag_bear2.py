"""fp_diag_bear2 — cache full pre-window fold predictions, then grid candidate
score gates offline (no harness runs burned). 5-seed bag to match the real module.
"""
import warnings; warnings.filterwarnings("ignore")
import numpy as np, pandas as pd, lightgbm, os, pickle

LAB = os.path.dirname(os.path.abspath(__file__))
K = 4; EMB = K + 60; SEEDS = (101, 102, 103, 104, 105)
df = pd.read_pickle(os.path.join(LAB, "features_v1.pkl"))
drop = {"open_time", "close", "y", "fwd_ret"}
cols = [c for c in df.columns if c not in drop]
c = df["close"].values.astype(float); ot = df["open_time"].values.astype(np.int64)
fwdK = np.full(len(c), np.nan); fwdK[:-K] = c[K:] / c[:-K] - 1
yK = (fwdK > 0).astype(float)
valid = df[cols].notna().all(axis=1).values & ~np.isnan(fwdK)
X = df[cols].values.astype(np.float64)
folds = pd.date_range("2025-02-01", "2025-08-01", freq="MS").astype(np.int64) // 10**6

CACHE = os.path.join(LAB, "fp_diag_bear2_cache.pkl")
if os.path.exists(CACHE):
    cache = pickle.load(open(CACHE, "rb"))
else:
    def mk(seed):
        return lightgbm.LGBMClassifier(n_estimators=350, learning_rate=0.03,
            num_leaves=47, max_depth=6, min_child_samples=150, subsample=0.8,
            subsample_freq=1, colsample_bytree=0.6, reg_lambda=8.0,
            random_state=seed, n_jobs=6, verbose=-1)
    cache = []
    for i in range(len(folds) - 1):
        ws, we = folds[i], folds[i + 1]
        te = np.where(valid & (ot >= ws) & (ot < we))[0]
        tr = np.where(valid & (ot < ws - EMB * 300000))[0]
        cut = int(len(tr) * 0.9)
        fit_idx, cal_idx = tr[:cut], tr[cut:][EMB:]
        pc, pt = [], []
        for seed in SEEDS:
            m = mk(seed); m.fit(X[fit_idx], yK[fit_idx].astype(int))
            pc.append(m.predict_proba(X[cal_idx])[:, 1])
            pt.append(m.predict_proba(X[te])[:, 1])
        cache.append(dict(fold=i, te=te, cal=cal_idx,
                          p_cal_raw=np.mean(pc, axis=0), p_te_raw=np.mean(pt, axis=0)))
        print(f"cached fold {i}", flush=True)
    pickle.dump(cache, open(CACHE, "wb"))

from sklearn.isotonic import IsotonicRegression
ret288 = df["ret_288"].values

def evaluate(lam_short, mu_crash, crash_thr=-0.025, cov=0.05):
    """score = |p-0.5| * lam_short (if short) * mu_crash (if crash state)."""
    hits, sides, fall_hits, crash_hits, fold_hits = [], [], [], [], []
    for f in cache:
        iso = IsotonicRegression(out_of_bounds="clip")
        iso.fit(f["p_cal_raw"], yK[f["cal"]].astype(int))
        p_cal = iso.transform(f["p_cal_raw"]); p_te = iso.transform(f["p_te_raw"])
        def sc(p, idx, praw):
            s = np.abs(p - 0.5)
            s = np.where(p < 0.5, s * lam_short, s)
            s = np.where(ret288[idx] < crash_thr, s * mu_crash, s)
            return s + 1e-7 * np.abs(praw - 0.5)
        s_cal = sc(p_cal, f["cal"], f["p_cal_raw"])
        s_te = sc(p_te, f["te"], f["p_te_raw"])
        thr = np.quantile(s_cal, 1 - cov)
        sel = s_te >= thr
        t = f["te"][sel]
        cor = ((p_te[sel] > 0.5).astype(int) == yK[t].astype(int)).astype(int)
        hits.extend(cor); sides.extend((p_te[sel] > 0.5).astype(int))
        fmask = ret288[t] < -0.01; cmask = ret288[t] < crash_thr
        fall_hits.extend(cor[fmask]); crash_hits.extend(cor[cmask])
        fold_hits.append((len(cor), float(np.mean(cor)) if len(cor) else np.nan))
    n = len(hits)
    return dict(n=n, hit=float(np.mean(hits)), long_frac=float(np.mean(sides)),
                n_fall=len(fall_hits), hit_fall=float(np.mean(fall_hits)) if fall_hits else np.nan,
                n_crash=len(crash_hits), hit_crash=float(np.mean(crash_hits)) if crash_hits else np.nan,
                folds_above50=sum(1 for n_, h in fold_hits if h > 0.5),
                min_fold_n=min(n_ for n_, _ in fold_hits), fold_hits=fold_hits)

print("\nlam_short mu_crash ->  n  hit  longfrac  hit_fall(n)  hit_crash(n)  folds>50  minN")
for lam in (1.0, 0.85, 0.7, 0.55, 0.4, 0.0):
    for mu in (1.0, 0.7, 0.4):
        r = evaluate(lam, mu)
        print(f"lam={lam:4.2f} mu={mu:3.1f}: n={r['n']:5d} hit={r['hit']:.4f} LF={r['long_frac']:.2f} "
              f"fall={r['hit_fall']:.4f}({r['n_fall']:4d}) crash={r['hit_crash']:.4f}({r['n_crash']:3d}) "
              f"f>50={r['folds_above50']}/6 minN={r['min_fold_n']}", flush=True)
# per-fold detail for the leading configs
for lam, mu in [(1.0, 1.0), (0.7, 0.7), (0.55, 0.7), (0.7, 1.0)]:
    r = evaluate(lam, mu)
    print(f"\nlam={lam} mu={mu} per-fold:", [(n_, round(h, 3)) for n_, h in r["fold_hits"]])
