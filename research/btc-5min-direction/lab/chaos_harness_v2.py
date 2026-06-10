"""CHAOS HARNESS v2 — successor to v1 after the 2026-06-10 adversarial audit.

v2 changes (audit findings, geometry & metrics otherwise identical to v1):
  1. EXACT COVERAGE: isotonic calibration collapses scores into plateaus, so v1's
     quantile gate selected whole tie-blocks (realized coverage 1.5-4x nominal).
     v2 tie-breaks the confidence score with the raw pre-isotonic confidence so
     quantile thresholds are exact. Realized coverage is now also logged per window.
  2. SCORE-HOOK HOLE CLOSED: score() now receives only feature columns + open_time
     (v1 passed the full frame incl. close, letting a hostile hook compute the label).
  3. Per-window realized coverage in output; harness field = chaos_harness_v2.

THIS FILE IS FROZEN. Experiments plug in via a config module; nobody edits this file.
Every evaluation is appended to an immutable trial ledger (lab/ledger.jsonl).

Geometry (timestamp-pinned, identical for every experiment regardless of feature NaNs):
  * 12 test windows spanning 2025-08-01T00:00Z .. 2026-06-01T00:00Z (boundaries hard-coded).
  * June 1-10 2026 is the LOCKED holdout: this harness refuses any dataset containing it.
  * Per window: train = all valid rows strictly before window_start minus EMB bars;
    fit = first 90% of train; calib = last 10% of train (purged EMB bars after fit);
    isotonic calibration fit on calib; confidence thresholds = quantiles of the CALIB
    score distribution only. Test rows touched exactly once.
  * EMB = K + 60 bars (label overlap + 5h of rolling-feature memory).

Experiment module contract (python file passed as argv[1]) — define:
  NAME            str, experiment id (required)
  K               int, label horizon in 5m bars (required; label = sign of close[t+K]/close[t]-1)
  PRIMARY_COV     float, the pre-registered operating coverage (required; one of COVERAGES)
  make_model()    -> sklearn-like estimator with fit/predict_proba       (required)
  FEATURES        list[str] of feature columns to use (optional; default = all)
  train_mask(df_fit, y_fit, fwd_fit) -> bool mask: rows to KEEP for fitting (optional)
  sample_weight(df_fit, y_fit, fwd_fit) -> weights (optional)
  score(p_calibrated, df_rows) -> confidence array (optional; default |p-0.5|).
                  Same function is applied to calib and test; threshold always from calib.

Usage: python3 chaos_harness_v1.py <experiment.py> [features.pkl]
Output: lab/results/<NAME>.json + ledger row + stdout table.
"""
import warnings; warnings.filterwarnings("ignore")
import sys, os, json, time, hashlib, importlib.util
import numpy as np
import pandas as pd
from sklearn.isotonic import IsotonicRegression

LAB = os.path.dirname(os.path.abspath(__file__))
LEDGER = os.path.join(LAB, "ledger.jsonl")
HASHFILE = os.path.join(LAB, "dataset_hash.txt")
RESULTS = os.path.join(LAB, "results")
HOLDOUT_START_MS = 1780272000000          # 2026-06-01T00:00Z — nothing at/after this may exist
WINDOW_BOUNDS_MS = [1754006400000, 1756195200000, 1758384000000, 1760572800000,
                    1762761600000, 1764950400000, 1767139200000, 1769328000000,
                    1771516800000, 1773705600000, 1775894400000, 1778083200000,
                    1780272000000]
COVERAGES = [0.10, 0.05, 0.025, 0.01, 0.005]
SEED = 42
MIN_TRADES_WINDOW = 30      # Rule 7: fewer voids the operating point
MIN_TRADES_POOLED = 2500
FEE_BIN = 0.02
CALIB_FRAC = 0.10

def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()

def wilson_lb(p, n, z):
    if n == 0: return float("nan")
    d = 1 + z * z / n
    c = p + z * z / (2 * n)
    s = z * np.sqrt(p * (1 - p) / n + z * z / (4 * n * n))
    return (c - s) / d

def z_for(alpha):
    # one-sided upper z for confidence 1-alpha. Bugfix 2026-06-10: replaced a
    # hand-rolled Acklam approximation whose tail branches had swapped signs
    # (deflated LB printed above plain LB in ledger rows 1-2; hit/n unaffected).
    from statistics import NormalDist
    return NormalDist().inv_cdf(1 - alpha)

def ledger_count():
    if not os.path.exists(LEDGER): return 0
    with open(LEDGER) as f:
        return sum(1 for _ in f)

def bust_test(trades, K):
    """Quarter-Kelly bankroll sim per Rule 6. trades: chronological list of
    (bar_index, p_calibrated, correct). Settlement K bars later; 2% per-bet cap;
    10% concurrent-exposure cap (skip when binding); bust = equity < 25% of start.
    Plus 1000 block-bootstrap reorderings (block = 50 trades)."""
    def run(seq):
        eq, peak, max_dd = 1.0, 1.0, 0.0
        open_pos = []  # (settle_bar, stake, correct)
        exposure = 0.0
        for bar, p, correct in seq:
            settled = [o for o in open_pos if o[0] <= bar]
            open_pos = [o for o in open_pos if o[0] > bar]
            for _, stk, c in settled:
                exposure -= stk
                eq += stk * 0.98 if c else -stk
                peak = max(peak, eq); max_dd = max(max_dd, 1 - eq / peak)
                if eq < 0.25: return eq, max_dd, True
            f = 0.25 * max(0.0, 2 * p - 1 - FEE_BIN)
            stake = min(f, 0.02) * eq
            if stake > 0 and exposure + stake <= 0.10 * eq:
                open_pos.append((bar + K, stake, correct)); exposure += stake
        for _, stk, c in open_pos:
            eq += stk * 0.98 if c else -stk
            peak = max(peak, eq); max_dd = max(max_dd, 1 - eq / peak)
        return eq, max_dd, eq < 0.25
    base_eq, base_dd, base_bust = run(trades)
    rng = np.random.default_rng(SEED)
    n = len(trades); blocks = [trades[i:i+50] for i in range(0, n, 50)]
    terms, dds, busts = [], [], 0
    for _ in range(1000):
        order = rng.permutation(len(blocks))
        seq = [t for bi in order for t in blocks[bi]]
        # re-index bars so settlement spacing is preserved within blocks
        seq = [(i, p, c) for i, (_, p, c) in enumerate(seq)]
        eq, dd, b = run(seq)
        terms.append(eq); dds.append(dd); busts += int(b)
    return dict(chrono_terminal=round(base_eq, 4), chrono_max_dd=round(base_dd, 4),
                chrono_bust=bool(base_bust), boot_bust_rate=busts / 1000,
                boot_terminal_median=round(float(np.median(terms)), 4),
                boot_terminal_p5=round(float(np.percentile(terms, 5)), 4),
                boot_max_dd_median=round(float(np.median(dds)), 4))

def main():
    exp_path = sys.argv[1]
    feat_path = sys.argv[2] if len(sys.argv) > 2 else os.path.join(LAB, "features_v1.pkl")
    spec = importlib.util.spec_from_file_location("exp", exp_path)
    exp = importlib.util.module_from_spec(spec); spec.loader.exec_module(exp)
    name, K = exp.NAME, int(exp.K)
    assert exp.PRIMARY_COV in COVERAGES, "PRIMARY_COV must be one of COVERAGES"
    EMB = K + 60
    np.random.seed(SEED)

    dhash = sha256(feat_path)
    if os.path.exists(HASHFILE):
        frozen = open(HASHFILE).read().strip().split()[0]
        assert dhash == frozen, f"DATASET HASH MISMATCH: {dhash} != frozen {frozen}"
    else:
        with open(HASHFILE, "w") as f: f.write(dhash + "  " + os.path.basename(feat_path) + "\n")

    feat = pd.read_pickle(feat_path)
    assert feat["open_time"].max() < HOLDOUT_START_MS, "HOLDOUT VIOLATION: dataset contains rows >= 2026-06-01"
    assert feat["open_time"].is_monotonic_increasing
    drop_always = {"open_time", "close", "y", "fwd_ret"}
    cols = list(getattr(exp, "FEATURES", None) or [c for c in feat.columns if c not in drop_always])
    assert not (set(cols) & drop_always), "label/time columns cannot be features"

    c = feat["close"].values.astype(float); ot = feat["open_time"].values.astype(np.int64)
    fwdK = np.full(len(c), np.nan); fwdK[:-K] = c[K:] / c[:-K] - 1
    yK = (fwdK > 0).astype(float)
    valid = feat[cols].notna().all(axis=1).values & ~np.isnan(fwdK)
    X = feat[cols].values.astype(np.float64); df = feat  # keep df for hooks
    score_fn = getattr(exp, "score", None)
    mask_fn = getattr(exp, "train_mask", None)
    weight_fn = getattr(exp, "sample_weight", None)

    per_window, all_trades = [], {cov: [] for cov in COVERAGES}
    for w in range(12):
        ws, we = WINDOW_BOUNDS_MS[w], WINDOW_BOUNDS_MS[w + 1]
        te = np.where(valid & (ot >= ws) & (ot < we))[0]
        tr_end_time = ws - EMB * 300000
        tr = np.where(valid & (ot < tr_end_time))[0]
        if len(te) == 0 or len(tr) < 5000:
            per_window.append(dict(win=w, skipped=True)); continue
        cut = int(len(tr) * (1 - CALIB_FRAC))
        fit_idx, cal_idx = tr[:cut], tr[cut:][EMB:]
        ftr, ycal = fit_idx, yK[cal_idx].astype(int)
        yfit, fwfit = yK[ftr].astype(int), fwdK[ftr]
        dffit = df.iloc[ftr]
        if mask_fn is not None:
            m = np.asarray(mask_fn(dffit, yfit, fwfit), dtype=bool)
            ftr = ftr[m]; yfit = yfit[m]; fwfit = fwfit[m]; dffit = df.iloc[ftr]
        kw = {}
        if weight_fn is not None:
            kw["sample_weight"] = np.asarray(weight_fn(dffit, yfit, fwfit), dtype=float)
        model = exp.make_model()
        model.fit(X[ftr], yfit, **kw)
        p_cal_raw = model.predict_proba(X[cal_idx])[:, 1]
        iso = IsotonicRegression(out_of_bounds="clip"); iso.fit(p_cal_raw, ycal)
        p_cal = iso.transform(p_cal_raw)
        p_te = iso.transform(model.predict_proba(X[te])[:, 1])
        hook_cols = cols + ["open_time"]
        raw_cal = np.abs(p_cal_raw - 0.5)
        raw_te = np.abs(model.predict_proba(X[te])[:, 1] - 0.5)
        if score_fn is None:
            s_cal = np.abs(p_cal - 0.5) + 1e-7 * raw_cal   # raw conf breaks isotonic ties
            s_te = np.abs(p_te - 0.5) + 1e-7 * raw_te
        else:
            s_cal = np.asarray(score_fn(p_cal, df.iloc[cal_idx][hook_cols])) + 1e-7 * raw_cal
            s_te = np.asarray(score_fn(p_te, df.iloc[te][hook_cols])) + 1e-7 * raw_te
        yte, fwte = yK[te].astype(int), fwdK[te]
        move = (c[te[-1]] / c[te[0]] - 1) * 100
        regime = "bull" if move > 4 else ("bear" if move < -4 else "chop")
        row = dict(win=w, start=str(pd.to_datetime(ws, unit="ms")), end=str(pd.to_datetime(we, unit="ms")),
                   regime=regime, move_pct=round(move, 1), n_test=len(te), skipped=False)
        for cov in COVERAGES:
            thr = np.quantile(s_cal, 1 - cov)
            sel = s_te >= thr
            nsel = int(sel.sum())
            if nsel:
                correct = ((p_te[sel] > 0.5).astype(int) == yte[sel]).astype(int)
                row[f"n_{cov}"] = nsel; row[f"hit_{cov}"] = round(float(correct.mean()), 4)
                row[f"realcov_{cov}"] = round(nsel / len(te), 4)
                for j, k_, cor in zip(te[sel], p_te[sel], correct):
                    all_trades[cov].append((int(j), float(max(k_, 1 - k_)), int(cor)))
            else:
                row[f"n_{cov}"] = 0; row[f"hit_{cov}"] = float("nan")
        per_window.append(row)
        print(f"win {w:2d} {row['start'][:10]}..{row['end'][:10]} {regime:4s} move={move:+6.1f}% "
              + " ".join(f"c{cov}: {row[f'hit_{cov}']:.3f}/{row[f'n_{cov}']}" for cov in [0.10, 0.025]))

    n_eff = max(45, ledger_count() + 1)
    z_plain, z_defl = z_for(0.05), z_for(0.05 / n_eff)
    pooled = {}
    for cov in COVERAGES:
        tr_ = sorted(all_trades[cov])
        n = len(tr_); hit = float(np.mean([t[2] for t in tr_])) if n else float("nan")
        wins_ok = [r for r in per_window if not r.get("skipped") and r.get(f"n_{cov}", 0) >= MIN_TRADES_WINDOW]
        wins_above = sum(1 for r in wins_ok if r[f"hit_{cov}"] > 0.5)
        pooled[str(cov)] = dict(
            n=n, hit=round(hit, 4) if n else None,
            wilson_lb95=round(wilson_lb(hit, n, z_plain), 4) if n else None,
            wilson_lb_deflated=round(wilson_lb(hit, n, z_defl), 4) if n else None,
            windows_meeting_min=len(wins_ok), windows_above_50=wins_above,
            min_trades_ok=bool(n >= MIN_TRADES_POOLED and len(wins_ok) == sum(1 for r in per_window if not r.get("skipped"))),
            binary_ev_even_odds=round(2 * hit - 1 - FEE_BIN, 4) if n else None)
    prim = exp.PRIMARY_COV
    bust = bust_test(sorted(all_trades[prim]), K) if all_trades[prim] else None

    out = dict(name=name, K=K, primary_cov=prim, dataset_sha256=dhash, n_features=len(cols),
               n_eff=n_eff, z_deflated=round(z_defl, 3), per_window=per_window, pooled=pooled,
               bust_test_primary=bust, harness="chaos_harness_v2", ts=int(time.time()))
    os.makedirs(RESULTS, exist_ok=True)
    with open(os.path.join(RESULTS, f"{name}.json"), "w") as f: json.dump(out, f, indent=1)
    with open(LEDGER, "a") as f:
        f.write(json.dumps(dict(name=name, K=K, primary_cov=prim, ts=out["ts"], harness="v2", dataset=dhash[:16],
                                pooled={k: dict(n=v["n"], hit=v["hit"]) for k, v in pooled.items()})) + "\n")
    print("\n=== POOLED (trade-weighted) ===")
    for cov in COVERAGES:
        p_ = pooled[str(cov)]
        flag = " *PRIMARY*" if cov == prim else ""
        print(f"  cov {cov*100:5.1f}%: n={p_['n']:6d} hit={p_['hit']} LB95={p_['wilson_lb95']} "
              f"deflLB={p_['wilson_lb_deflated']} win>={MIN_TRADES_WINDOW}:{p_['windows_meeting_min']}/12 "
              f"wins>50%:{p_['windows_above_50']}{flag}")
    if bust: print("bust:", bust)
    print(f"\nresult -> results/{name}.json   (ledger row appended, N_eff now {n_eff})")

if __name__ == "__main__":
    main()
