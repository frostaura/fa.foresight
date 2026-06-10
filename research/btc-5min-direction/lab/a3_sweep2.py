#!/usr/bin/env python3
"""a3 internal sweep 2 — refinements of d2 cross-fitted meta-labeling. PRE-WINDOW only.

Configs (all composition c1: q = .5 + sign*(m*|p-.5|)):
  base    : primary alone
  d2      : 2 expanding folds (50->75, 75->100), regime meta cols      [sweep-1 winner]
  d3      : 3 expanding folds (25->50, 50->75, 75->100)
  d2_all  : d2 but meta sees [p,|p-.5|] + ALL features
  d2_wt   : d2 with meta sample_weight = 0.25 + |p-0.5| (focus on confident rows)
"""
import warnings; warnings.filterwarnings("ignore")
import os, time
import numpy as np
import pandas as pd
import lightgbm as lgb
from sklearn.isotonic import IsotonicRegression

LAB = os.path.dirname(os.path.abspath(__file__))
K = 3
EMB = K + 60
CALIB_FRAC = 0.10
PURGE = 70
WALL = 1754006400000
WLEN = 2188800000
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

    def mX_reg(p, Xr): return np.column_stack([p, np.abs(p - 0.5), Xr[:, reg_idx]])
    def mX_all(p, Xr): return np.column_stack([p, np.abs(p - 0.5), Xr])

    configs = ["base", "d2", "d3", "d2_all", "d2_wt"]
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
        print(f"win {w} n_fit={n}", flush=True)

        cut25, cut50, cut75 = int(n*0.25), int(n*0.50), int(n*0.75)
        prim25 = primary_model(); prim25.fit(Xf[:cut25], yfit[:cut25])
        prim50 = primary_model(); prim50.fit(Xf[:cut50], yfit[:cut50])
        prim75 = primary_model(); prim75.fit(Xf[:cut75], yfit[:cut75])
        prim_all = primary_model(); prim_all.fit(Xf, yfit)

        folds = []
        for prim, sl in [(prim25, slice(cut25+PURGE, cut50)),
                         (prim50, slice(cut50+PURGE, cut75)),
                         (prim75, slice(cut75+PURGE, n))]:
            p_ = prim.predict_proba(Xf[sl])[:, 1]
            corr = ((p_ > 0.5).astype(int) == yfit[sl]).astype(int)
            folds.append((p_, Xf[sl], corr))

        p2 = np.concatenate([folds[1][0], folds[2][0]])
        X2 = np.vstack([folds[1][1], folds[2][1]])
        c2 = np.concatenate([folds[1][2], folds[2][2]])
        p3 = np.concatenate([f[0] for f in folds])
        X3 = np.vstack([f[1] for f in folds])
        c3 = np.concatenate([f[2] for f in folds])

        m_d2 = meta_model(); m_d2.fit(mX_reg(p2, X2), c2)
        m_d3 = meta_model(); m_d3.fit(mX_reg(p3, X3), c3)
        m_all = meta_model(); m_all.fit(mX_all(p2, X2), c2)
        m_wt = meta_model(); m_wt.fit(mX_reg(p2, X2), c2, sample_weight=0.25 + np.abs(p2 - 0.5))

        p_cal = prim_all.predict_proba(Xc_)[:, 1]
        p_te = prim_all.predict_proba(Xt)[:, 1]

        def comp(meta, mxf, pc, pt):
            mc = meta.predict_proba(mxf(pc, Xc_))[:, 1]
            mt = meta.predict_proba(mxf(pt, Xt))[:, 1]
            return (0.5 + np.sign(pc-0.5)*mc*np.abs(pc-0.5),
                    0.5 + np.sign(pt-0.5)*mt*np.abs(pt-0.5))

        raw = {"base": (p_cal, p_te),
               "d2": comp(m_d2, mX_reg, p_cal, p_te),
               "d3": comp(m_d3, mX_reg, p_cal, p_te),
               "d2_all": comp(m_all, mX_all, p_cal, p_te),
               "d2_wt": comp(m_wt, mX_reg, p_cal, p_te)}
        for cfg, (qc, qt) in raw.items():
            iso = IsotonicRegression(out_of_bounds="clip"); iso.fit(qc, ycal)
            pc, pt = iso.transform(qc), iso.transform(qt)
            sc, st = np.abs(pc - 0.5), np.abs(pt - 0.5)
            line = f"  {cfg:7s}"
            for cov in COVS:
                thr = np.quantile(sc, 1 - cov)
                sel = st >= thr
                ns = int(sel.sum())
                if ns:
                    ok = ((pt[sel] > 0.5).astype(int) == yte[sel]).astype(int)
                    trades[cfg][cov].extend(ok.tolist())
                    line += f"  cov{cov}: {ok.mean():.4f}/{ns}"
                else:
                    line += f"  cov{cov}: -/0"
            print(line, flush=True)

    print("\n=== POOLED over 4 pre-windows ===")
    for cfg in configs:
        line = f"{cfg:7s}"
        for cov in COVS:
            t = trades[cfg][cov]
            line += f"  cov{cov}: {np.mean(t):.4f}/n={len(t)}" if t else f"  cov{cov}: -/0"
        print(line)
    print(f"elapsed {time.time()-t0:.0f}s")


if __name__ == "__main__":
    main()
