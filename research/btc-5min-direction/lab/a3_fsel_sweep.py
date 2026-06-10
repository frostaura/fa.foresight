#!/usr/bin/env python3
"""a3 feature-selection internal sweep — PRE-WINDOW only (ot < 2025-08-01).

Mimics chaos_harness_v1 geometry on 4 pseudo-windows (25.33d) ending 2025-08-01:
train < ws - EMB bars, fit=first 90%, calib=last 10% purged EMB, isotonic on calib,
thresholds from calib quantiles. K=3, coverages 2.5% / 1%.

Configs: all163, topN feature lists from a3_fsel_rank.json (gain ranking),
optionally a curated list. Selection tool only.
"""
import warnings; warnings.filterwarnings("ignore")
import os, json, time, sys
import numpy as np
import pandas as pd
import lightgbm as lgb
from sklearn.isotonic import IsotonicRegression

LAB = os.path.dirname(os.path.abspath(__file__))
K = 3
EMB = K + 60
CALIB_FRAC = 0.10
WALL = 1754006400000
WLEN = 2188800000
NWIN = 4
COVS = [0.025, 0.01]

def model():
    return lgb.LGBMClassifier(n_estimators=350, learning_rate=0.03, num_leaves=47,
                              max_depth=6, min_child_samples=150, subsample=0.8,
                              subsample_freq=1, colsample_bytree=0.6, reg_lambda=8.0,
                              random_state=42, n_jobs=3, verbose=-1)

def main():
    t0 = time.time()
    rank = json.load(open(os.path.join(LAB, "a3_fsel_rank.json")))
    ranked = [f for f, _ in rank["rank_gain"]]
    df = pd.read_pickle(os.path.join(LAB, "features_v1.pkl"))
    cols = [c for c in df.columns if c not in ("open_time", "close")]
    c = df["close"].values.astype(float)
    ot = df["open_time"].values.astype(np.int64)
    fwd = np.full(len(c), np.nan); fwd[:-K] = c[K:] / c[:-K] - 1
    y = (fwd > 0).astype(float)
    valid = df[cols].notna().all(axis=1).values & ~np.isnan(fwd)
    X = df[cols].values.astype(np.float64)
    del df

    curated = None
    cur_path = os.path.join(LAB, "a3_fsel_curated.json")
    if os.path.exists(cur_path):
        curated = json.load(open(cur_path))

    fsets = {"all163": cols,
             "top80": ranked[:80],
             "top40": ranked[:40],
             "top25": ranked[:25]}
    if curated:
        fsets["curated"] = curated
    fidx = {k: np.array([cols.index(f) for f in v]) for k, v in fsets.items()}

    trades = {k: {cov: [] for cov in COVS} for k in fsets}
    perwin = {k: {cov: [] for cov in COVS} for k in fsets}
    for w in range(NWIN):
        ws = WALL - (NWIN - w) * WLEN
        we = ws + WLEN
        te = np.where(valid & (ot >= ws) & (ot < we))[0]
        tr = np.where(valid & (ot < ws - EMB * 300000))[0]
        cut = int(len(tr) * (1 - CALIB_FRAC))
        fit_idx, cal_idx = tr[:cut], tr[cut:][EMB:]
        yfit, ycal, yte = y[fit_idx].astype(int), y[cal_idx].astype(int), y[te].astype(int)
        print(f"win {w} {pd.to_datetime(ws,unit='ms').date()} n_fit={len(fit_idx)} n_te={len(te)}", flush=True)
        for k, ji in fidx.items():
            m = model(); m.fit(X[np.ix_(fit_idx, ji)], yfit)
            qc = m.predict_proba(X[np.ix_(cal_idx, ji)])[:, 1]
            qt = m.predict_proba(X[np.ix_(te, ji)])[:, 1]
            iso = IsotonicRegression(out_of_bounds="clip"); iso.fit(qc, ycal)
            pc, pt = iso.transform(qc), iso.transform(qt)
            sc, st = np.abs(pc - 0.5), np.abs(pt - 0.5)
            line = f"  {k:8s}"
            for cov in COVS:
                thr = np.quantile(sc, 1 - cov)
                sel = st >= thr
                ns = int(sel.sum())
                if ns:
                    cor = ((pt[sel] > 0.5).astype(int) == yte[sel]).astype(int)
                    trades[k][cov].extend(cor.tolist())
                    perwin[k][cov].append(float(cor.mean()))
                    line += f"  cov{cov}: {cor.mean():.4f}/{ns}"
                else:
                    perwin[k][cov].append(float("nan"))
                    line += f"  cov{cov}: -/0"
            print(line, flush=True)

    print("\n=== POOLED over 4 pre-windows ===")
    res = {}
    for k in fsets:
        line = f"{k:8s}"
        res[k] = {}
        for cov in COVS:
            t = trades[k][cov]
            res[k][str(cov)] = dict(n=len(t), hit=float(np.mean(t)) if t else None,
                                    perwin=perwin[k][cov])
            line += f"  cov{cov}: {np.mean(t):.4f}/n={len(t)}" if t else f"  cov{cov}: -/0"
        print(line)
    with open(os.path.join(LAB, "a3_fsel_sweep.json"), "w") as f:
        json.dump(res, f, indent=1)
    print(f"elapsed {time.time()-t0:.0f}s")

if __name__ == "__main__":
    main()
