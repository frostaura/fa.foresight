#!/usr/bin/env python3
"""a3 multi-speed ensemble — internal PRE-WINDOW member cache.

Mirrors chaos_harness_v1 geometry on 6 pseudo-windows that end at 2025-08-01
(strictly pre-window data). For each window, fits 6 member models on the first
85% of the fit slice and caches predictions on: inner holdout (last 15% of fit,
embargoed), calib slice, test slice. Ensembles are assembled offline from cache.

K=3. EMB = K + 60 = 63.
"""
import os, sys, json, time, pickle
import numpy as np
import pandas as pd
import lightgbm as lgb
from sklearn.linear_model import LogisticRegression
from sklearn.preprocessing import StandardScaler
from sklearn.pipeline import make_pipeline

LAB = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
K = 3
EMB = K + 60
PRE_END = 1754006400000
WIN_MS = 2188800000  # same width as harness windows
NWIN = 6
CALIB_FRAC = 0.10
SEED = 42

FAST = ["ret_1","retn_1","ret_3","retn_3","ret_6","retn_6","ret_12","retn_12",
        "m1_ti_mean","m1_ti_last","m1_maxmove_share","m1_sign_agree","m1_last_retn",
        "ti_raw","ti_ema6","ti_ema12","ti_z_48","ti_persist_12",
        "body_range","wick_up_share","wick_dn_share","body_z_48","consec_runs",
        "vwap_dev_12","vol_z_12","sma_z_12","rsi_14",
        "large_flow_ema12","small_flow_ema12","lf_x_retn6","sf_x_retn6",
        "perp_ti_ema12","perp_spot_ti_gap","cascade_signed","cascade_decel",
        "doi_1b_z","doi_4b_z",
        "xa_eth_ret1_vn","xa_eth_ret3_vn","xa_sol_ret1_vn","xa_sol_ret3_vn",
        "xa_bnb_ret1_vn","xa_bnb_ret3_vn","xa_altidx_ret1_vn","xa_breadth_15m",
        "xa_cb_prem_bps","xa_cb_prem_ema3","rv_cc_12","rv_gk_12"]

def make_members(all_cols):
    fast_idx = np.array([all_cols.index(c) for c in FAST])
    slow_cols = [c for c in all_cols if c not in set(FAST)]
    slow_idx = np.array([all_cols.index(c) for c in slow_cols])
    full_idx = np.arange(len(all_cols))
    def lgbm(**kw):
        base = dict(n_estimators=350, learning_rate=0.03, num_leaves=47, max_depth=6,
                    min_child_samples=150, subsample=0.8, subsample_freq=1,
                    colsample_bytree=0.6, reg_lambda=8.0, random_state=SEED,
                    n_jobs=3, verbose=-1)
        base.update(kw)
        return lgb.LGBMClassifier(**base)
    return {
        "full":    (full_idx, lambda: lgbm()),
        "fast":    (fast_idx, lambda: lgbm(colsample_bytree=0.8)),
        "slow":    (slow_idx, lambda: lgbm(colsample_bytree=0.7)),
        "shallow": (full_idx, lambda: lgbm(n_estimators=250, learning_rate=0.06,
                                           num_leaves=7, max_depth=3, reg_lambda=4.0)),
        "deep":    (full_idx, lambda: lgbm(n_estimators=600, learning_rate=0.02,
                                           num_leaves=127, max_depth=-1,
                                           min_child_samples=300, colsample_bytree=0.5)),
        "logit":   (full_idx, lambda: make_pipeline(
                        StandardScaler(),
                        LogisticRegression(C=0.05, max_iter=2000, random_state=SEED))),
    }

def main():
    t0 = time.time()
    df = pd.read_pickle(os.path.join(LAB, "features_v1.pkl"))
    drop = {"open_time", "close", "y", "fwd_ret"}
    cols = [c for c in df.columns if c not in drop]
    c = df["close"].values.astype(float)
    ot = df["open_time"].values.astype(np.int64)
    fwdK = np.full(len(c), np.nan); fwdK[:-K] = c[K:] / c[:-K] - 1
    yK = (fwdK > 0).astype(float)
    valid = df[cols].notna().all(axis=1).values & ~np.isnan(fwdK)
    X = df[cols].values.astype(np.float64)
    members = make_members(cols)

    cache = {"cols": cols, "windows": []}
    bounds = [PRE_END - (NWIN - i) * WIN_MS for i in range(NWIN + 1)]
    for w in range(NWIN):
        ws, we = bounds[w], bounds[w + 1]
        te = np.where(valid & (ot >= ws) & (ot < we))[0]
        tr = np.where(valid & (ot < ws - EMB * 300000))[0]
        cut = int(len(tr) * (1 - CALIB_FRAC))
        fit_idx, cal_idx = tr[:cut], tr[cut:][EMB:]
        icut = int(len(fit_idx) * 0.85)
        core_idx, inner_idx = fit_idx[:icut], fit_idx[icut:][EMB:]
        wrec = {"win": w, "ws": int(ws), "we": int(we),
                "n_core": len(core_idx), "n_inner": len(inner_idx),
                "n_cal": len(cal_idx), "n_te": len(te),
                "y_inner": yK[inner_idx].astype(int), "y_cal": yK[cal_idx].astype(int),
                "y_te": yK[te].astype(int), "preds": {}}
        for name, (idx, fac) in members.items():
            m = fac()
            m.fit(X[np.ix_(core_idx, idx)], yK[core_idx].astype(int))
            wrec["preds"][name] = {
                "inner": m.predict_proba(X[np.ix_(inner_idx, idx)])[:, 1],
                "cal":   m.predict_proba(X[np.ix_(cal_idx, idx)])[:, 1],
                "te":    m.predict_proba(X[np.ix_(te, idx)])[:, 1]}
            print(f"win {w} member {name:8s} done  t={time.time()-t0:.0f}s", flush=True)
        cache["windows"].append(wrec)
    out = os.path.join(LAB, "experiments", "a3_mh_member_cache.pkl")
    with open(out, "wb") as f:
        pickle.dump(cache, f)
    print("saved", out, f"total {time.time()-t0:.0f}s")

if __name__ == "__main__":
    main()
