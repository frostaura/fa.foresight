"""ONE-SHOT champion validation on the locked June 1-10 2026 holdout (PROTOCOL Rule 5).

Runs EXACTLY ONCE, on the single nominated champion, after the campaign closes.
Role: falsification, not estimation — a 10-day window cannot certify 60%, but a real
all-weather edge must not collapse in a crash leg it has never seen (BTC -16% over
the holdout, a regime no model in this campaign ever trained on).

Mirrors chaos_harness_v2 semantics for a single test window = [2026-06-01, end of data):
train = all valid rows before 2026-06-01 minus EMB bars; fit = first 90%; calib = last
10% (purged); isotonic from calib; threshold = calib quantile at the champion's
pre-registered PRIMARY_COV with the v2 raw-confidence tie-break.

Usage: python3 holdout_oneshot.py <champion_module.py> <features_holdout.pkl>
The features file must be built by the SAME deterministic build scripts with
--end 2026-06-11 and must match features_v1.pkl exactly on all pre-June rows
(this script verifies that on a sample before doing anything else).
"""
import warnings; warnings.filterwarnings("ignore")
import sys, os, json, time, importlib.util
import numpy as np
import pandas as pd
from sklearn.isotonic import IsotonicRegression

LAB = os.path.dirname(os.path.abspath(__file__))
HOLDOUT_START_MS = 1780272000000
SEED = 42
CALIB_FRAC = 0.10
FEE_BIN = 0.02
MARKER = os.path.join(LAB, "HOLDOUT_CONSUMED")

def wilson_lb(p, n, z):
    if n == 0: return float("nan")
    d = 1 + z * z / n; c = p + z * z / (2 * n)
    s = z * np.sqrt(p * (1 - p) / n + z * z / (4 * n * n))
    return (c - s) / d

def main():
    assert not os.path.exists(MARKER), \
        "HOLDOUT ALREADY CONSUMED — Rule 5 allows exactly one run. Refusing."
    exp_path, feat_path = sys.argv[1], sys.argv[2]
    spec = importlib.util.spec_from_file_location("exp", exp_path)
    exp = importlib.util.module_from_spec(spec); spec.loader.exec_module(exp)
    K, prim = int(exp.K), float(exp.PRIMARY_COV)
    EMB = K + 60
    np.random.seed(SEED)

    feat = pd.read_pickle(feat_path)
    v1 = pd.read_pickle(os.path.join(LAB, "features_v1.pkl"))
    # holdout features must reproduce the frozen pre-June rows exactly (sampled check)
    pre = feat[feat["open_time"] < HOLDOUT_START_MS]
    assert len(pre) == len(v1), f"pre-June row mismatch: {len(pre)} vs {len(v1)}"
    idx = np.random.default_rng(0).choice(len(v1), 2000, replace=False)
    common = [c for c in v1.columns if c in feat.columns]
    a = pre.iloc[idx][common].reset_index(drop=True)
    b = v1.iloc[idx][common].reset_index(drop=True)
    pd.testing.assert_frame_equal(a, b, check_exact=False, rtol=1e-9, atol=1e-12)
    print("pre-June rows match features_v1.pkl on 2000-row sample — build is faithful")

    cols = list(getattr(exp, "FEATURES", None) or [c for c in feat.columns
                if c not in ("open_time", "close", "y", "fwd_ret")])
    c = feat["close"].values.astype(float); ot = feat["open_time"].values.astype(np.int64)
    fwdK = np.full(len(c), np.nan); fwdK[:-K] = c[K:] / c[:-K] - 1
    yK = (fwdK > 0).astype(float)
    valid = feat[cols].notna().all(axis=1).values & ~np.isnan(fwdK)
    X = feat[cols].values.astype(np.float64)

    te = np.where(valid & (ot >= HOLDOUT_START_MS))[0]
    tr = np.where(valid & (ot < HOLDOUT_START_MS - EMB * 300000))[0]
    cut = int(len(tr) * (1 - CALIB_FRAC))
    fit_idx, cal_idx = tr[:cut], tr[cut:][EMB:]
    yfit, fwfit = yK[fit_idx].astype(int), fwdK[fit_idx]
    dffit = feat.iloc[fit_idx]
    mask_fn = getattr(exp, "train_mask", None); weight_fn = getattr(exp, "sample_weight", None)
    if mask_fn is not None:
        m = np.asarray(mask_fn(dffit, yfit, fwfit), dtype=bool)
        fit_idx = fit_idx[m]; yfit = yfit[m]; fwfit = fwfit[m]; dffit = feat.iloc[fit_idx]
    kw = {}
    if weight_fn is not None:
        kw["sample_weight"] = np.asarray(weight_fn(dffit, yfit, fwfit), dtype=float)
    model = exp.make_model(); model.fit(X[fit_idx], yfit, **kw)

    p_cal_raw = model.predict_proba(X[cal_idx])[:, 1]
    ycal = yK[cal_idx].astype(int)
    iso = IsotonicRegression(out_of_bounds="clip"); iso.fit(p_cal_raw, ycal)
    p_cal = iso.transform(p_cal_raw)
    p_te_raw = model.predict_proba(X[te])[:, 1]
    p_te = iso.transform(p_te_raw)
    s_cal = np.abs(p_cal - 0.5) + 1e-7 * np.abs(p_cal_raw - 0.5)
    s_te = np.abs(p_te - 0.5) + 1e-7 * np.abs(p_te_raw - 0.5)
    thr = np.quantile(s_cal, 1 - prim)
    sel = s_te >= thr
    yte = yK[te].astype(int)
    correct = ((p_te[sel] > 0.5).astype(int) == yte[sel]).astype(int)
    n, hit = int(sel.sum()), float(correct.mean()) if sel.sum() else float("nan")
    z95 = 1.6448536269514722
    days = pd.to_datetime(ot[te[sel]], unit="ms").strftime("%m-%d")
    byday = pd.DataFrame({"day": days, "ok": correct}).groupby("day")["ok"].agg(["count", "mean"])

    move = (c[te[-1]] / c[te[0]] - 1) * 100
    out = dict(champion=exp.NAME, K=K, primary_cov=prim, holdout_bars=len(te),
               btc_move_pct=round(move, 1), n_trades=n, hit=round(hit, 4),
               wilson_lb95=round(wilson_lb(hit, n, z95), 4),
               binary_ev_even_odds=round(2 * hit - 1 - FEE_BIN, 4),
               by_day={d: [int(r["count"]), round(r["mean"], 3)] for d, r in byday.iterrows()},
               min_trades_rule70=bool(n >= 70), ts=int(time.time()))
    verdict = ("NOT FALSIFIED — holdout consistent with a real edge" if n >= 70 and hit > 0.5
               and wilson_lb(hit, n, z95) > 0.45 else
               "FALSIFIED or UNDERPOWERED — see numbers")
    out["verdict_heuristic"] = verdict
    with open(os.path.join(LAB, "results", "HOLDOUT_" + exp.NAME + ".json"), "w") as f:
        json.dump(out, f, indent=1)
    with open(MARKER, "w") as f:
        f.write(f"{exp.NAME} @ {time.ctime()}\n")
    print(json.dumps(out, indent=1))

if __name__ == "__main__":
    main()
