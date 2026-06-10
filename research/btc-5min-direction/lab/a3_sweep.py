#!/usr/bin/env python3
"""a3 internal sweep — meta-labeling designs, PRE-WINDOW data only (ot < 2025-08-01).

Mimics chaos_harness_v1 geometry on 4 pseudo-windows (25.33d each) ending 2025-08-01.
NOT a results generator — selection tool only. K=3, eval coverages 2.5%/1%.

Configs evaluated per pseudo-window (sharing primary fits):
  base      : primary LGBM trained on all fit rows (the baseline)
  d1_c1     : 75/25 meta (single split), q = .5 + sign*(m*|p-.5|)
  d1_c2     : same meta,             q = .5 + sign*(m*.5)          (rank by meta alone)
  d1_c3     : same meta,             q = .5 + sign*sqrt(m*2*|p-.5|)/2 (geometric blend)
  d2_c1     : cross-fitted meta (2 expanding folds: 50->75, 75->100 of fit), comp C1
  d2_c3     : cross-fitted meta, comp C3
"""
import warnings; warnings.filterwarnings("ignore")
import os, sys, time
import numpy as np
import pandas as pd
import lightgbm as lgb
from sklearn.isotonic import IsotonicRegression

LAB = os.path.dirname(os.path.abspath(__file__))
K = 3
EMB = K + 60
CALIB_FRAC = 0.10
PURGE = 70
WALL = 1754006400000          # 2025-08-01
WLEN = 2188800000             # 25.33 days in ms
NWIN = 4
COVS = [0.025, 0.01]

META_COLS = ["rv48_prank_30d", "range_prank_30d", "vov_48", "er_12", "er_48", "er_288",
             "adx_14", "xa_dvol_pct90d", "xa_dvol_chg12h_z", "xa_dvol_vel_z", "xa_vrp_z",
             "xa_breadth_1h", "xa_breadth_4h", "xa_mean_corr288", "xa_dispersion_1h_z",
             "funding_pctile_90d", "pred_funding_z", "oi_fuel_z", "oi_pctile_30d",
             "crowding_score", "sess_us_cash", "sess_asia"]


def primary_model():
    return lgb.LGBMClassifier(n_estimators=350, learning_rate=0.03, num_leaves=47,
                              max_depth=6, min_child_samples=150, subsample=0.8,
                              subsample_freq=1, colsample_bytree=0.6, reg_lambda=8.0,
                              random_state=42, n_jobs=3, verbose=-1)


def meta_model():
    return lgb.LGBMClassifier(n_estimators=150, learning_rate=0.05, max_depth=4,
                              num_leaves=15, min_child_samples=100, subsample=0.8,
                              subsample_freq=1, colsample_bytree=0.8, reg_lambda=5.0,
                              random_state=42, n_jobs=3, verbose=-1)


def meta_X(p, Xreg):
    return np.column_stack([p, np.abs(p - 0.5), Xreg])


def compose(p, m, mode):
    s = np.sign(p - 0.5)
    a = np.abs(p - 0.5)
    if mode == "c1":
        return 0.5 + s * (m * a)
    if mode == "c2":
        return 0.5 + s * (m * 0.5)
    if mode == "c3":
        return 0.5 + s * np.sqrt(np.clip(m * 2 * a, 0, 1)) / 2
    raise ValueError(mode)


def main():
    t0 = time.time()
    df = pd.read_pickle(os.path.join(LAB, "features_v1.pkl"))
    cols = [c for c in df.columns if c not in ("open_time", "close")]
    reg_idx = np.array([cols.index(c) for c in META_COLS])
    c = df["close"].values.astype(float)
    ot = df["open_time"].values.astype(np.int64)
    fwd = np.full(len(c), np.nan); fwd[:-K] = c[K:] / c[:-K] - 1
    y = (fwd > 0).astype(float)
    valid = df[cols].notna().all(axis=1).values & ~np.isnan(fwd)
    X = df[cols].values.astype(np.float64)
    del df

    configs = ["base", "d1_c1", "d1_c2", "d1_c3", "d2_c1", "d2_c3"]
    trades = {cfg: {cov: [] for cov in COVS} for cfg in configs}
    for w in range(NWIN):
        ws = WALL - (NWIN - w) * WLEN
        we = ws + WLEN
        te = np.where(valid & (ot >= ws) & (ot < we))[0]
        tr = np.where(valid & (ot < ws - EMB * 300000))[0]
        cut = int(len(tr) * (1 - CALIB_FRAC))
        fit_idx, cal_idx = tr[:cut], tr[cut:][EMB:]
        yfit = y[fit_idx].astype(int); ycal = y[cal_idx].astype(int); yte = y[te].astype(int)
        Xf, Xc_, Xt = X[fit_idx], X[cal_idx], X[te]
        n = len(fit_idx)
        print(f"win {w} {pd.to_datetime(ws,unit='ms').date()} n_fit={n} n_cal={len(cal_idx)} n_te={len(te)}", flush=True)

        # --- primary fits ---
        cut50, cut75 = int(n * 0.50), int(n * 0.75)
        prim75 = primary_model(); prim75.fit(Xf[:cut75], yfit[:cut75])
        prim50 = primary_model(); prim50.fit(Xf[:cut50], yfit[:cut50])
        prim_all = primary_model(); prim_all.fit(Xf, yfit)

        # --- D1 meta: train on last 25% (purged) using prim75 OOS probs ---
        b = slice(cut75 + PURGE, n)
        p_b = prim75.predict_proba(Xf[b])[:, 1]
        corr_b = ((p_b > 0.5).astype(int) == yfit[b]).astype(int)
        meta1 = meta_model(); meta1.fit(meta_X(p_b, Xf[b][:, reg_idx]), corr_b)

        # --- D2 meta: cross-fitted on folds [50+P,75) via prim50 and [75+P,100) via prim75 ---
        f1 = slice(cut50 + PURGE, cut75)
        p_f1 = prim50.predict_proba(Xf[f1])[:, 1]
        corr_f1 = ((p_f1 > 0.5).astype(int) == yfit[f1]).astype(int)
        mX2 = np.vstack([meta_X(p_f1, Xf[f1][:, reg_idx]), meta_X(p_b, Xf[b][:, reg_idx])])
        my2 = np.concatenate([corr_f1, corr_b])
        meta2 = meta_model(); meta2.fit(mX2, my2)

        # --- predictions (primary refit on all for deployment) ---
        p_cal_prim = prim_all.predict_proba(Xc_)[:, 1]
        p_te_prim = prim_all.predict_proba(Xt)[:, 1]
        m1_cal = meta1.predict_proba(meta_X(p_cal_prim, Xc_[:, reg_idx]))[:, 1]
        m1_te = meta1.predict_proba(meta_X(p_te_prim, Xt[:, reg_idx]))[:, 1]
        m2_cal = meta2.predict_proba(meta_X(p_cal_prim, Xc_[:, reg_idx]))[:, 1]
        m2_te = meta2.predict_proba(meta_X(p_te_prim, Xt[:, reg_idx]))[:, 1]

        raw = {
            "base": (p_cal_prim, p_te_prim),
            "d1_c1": (compose(p_cal_prim, m1_cal, "c1"), compose(p_te_prim, m1_te, "c1")),
            "d1_c2": (compose(p_cal_prim, m1_cal, "c2"), compose(p_te_prim, m1_te, "c2")),
            "d1_c3": (compose(p_cal_prim, m1_cal, "c3"), compose(p_te_prim, m1_te, "c3")),
            "d2_c1": (compose(p_cal_prim, m2_cal, "c1"), compose(p_te_prim, m2_te, "c1")),
            "d2_c3": (compose(p_cal_prim, m2_cal, "c3"), compose(p_te_prim, m2_te, "c3")),
        }
        for cfg, (qc, qt) in raw.items():
            iso = IsotonicRegression(out_of_bounds="clip"); iso.fit(qc, ycal)
            pc, pt = iso.transform(qc), iso.transform(qt)
            sc, st = np.abs(pc - 0.5), np.abs(pt - 0.5)
            line = f"  {cfg:6s}"
            for cov in COVS:
                thr = np.quantile(sc, 1 - cov)
                sel = st >= thr
                ns = int(sel.sum())
                if ns:
                    hit = float(((pt[sel] > 0.5).astype(int) == yte[sel]).mean())
                    trades[cfg][cov].extend(((pt[sel] > 0.5).astype(int) == yte[sel]).astype(int).tolist())
                else:
                    hit = float("nan")
                line += f"  cov{cov}: {hit:.4f}/{ns}"
            print(line, flush=True)

    print("\n=== POOLED over 4 pre-windows ===")
    for cfg in configs:
        line = f"{cfg:6s}"
        for cov in COVS:
            t = trades[cfg][cov]
            line += f"  cov{cov}: {np.mean(t):.4f}/n={len(t)}" if t else f"  cov{cov}: -/0"
        print(line)
    print(f"elapsed {time.time()-t0:.0f}s")


if __name__ == "__main__":
    main()
