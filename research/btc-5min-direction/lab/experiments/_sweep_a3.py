"""Internal pre-window sweep for a3 regime-mixture campaign.

NOT the chaos harness. Uses ONLY rows with open_time < 1754006400000 (pre 2025-08-01).
Replicates harness geometry (fit/calib split, isotonic, calib-quantile thresholds)
on pseudo-windows carved from Mar-Jul 2025.
"""
import warnings; warnings.filterwarnings("ignore")
import os, sys, json, time
import numpy as np
import pandas as pd
import lightgbm
from sklearn.isotonic import IsotonicRegression

LAB = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PRE_END = 1754006400000
WIN_MS = 2188800000  # 25.33 days, same as harness windows
N_WIN = int(sys.argv[1]) if len(sys.argv) > 1 else 5
K = 3
EMB = K + 60
COVERAGES = [0.10, 0.05, 0.025, 0.01]
SEED = 42

BASE_PARAMS = dict(n_estimators=350, learning_rate=0.03, num_leaves=47, max_depth=6,
                   min_child_samples=150, subsample=0.8, subsample_freq=1,
                   colsample_bytree=0.6, reg_lambda=8.0, random_state=42,
                   n_jobs=3, verbose=-1)
BUCKET_PARAMS = dict(BASE_PARAMS, n_estimators=250)


class RegimeMixture:
    """Bucket rows by causal regime key (quantiles of regime cols computed from fit
    data); fit one LGBM per bucket + global fallback; route at predict time.
    blend: weight on global model when a bucket model exists (0 = pure specialist)."""

    def __init__(self, regime_specs, min_bucket=5000, blend=0.0,
                 bucket_params=None, global_params=None):
        # regime_specs: list of (col_idx, n_bins)
        self.specs = regime_specs
        self.min_bucket = min_bucket
        self.blend = blend
        self.bp = bucket_params or BUCKET_PARAMS
        self.gp = global_params or BASE_PARAMS

    def _bucket_ids(self, X):
        bid = np.zeros(len(X), dtype=int)
        mult = 1
        for (ci, nb), edges in zip(self.specs, self.edges_):
            d = np.digitize(X[:, ci], edges)  # 0..nb-1
            bid = bid + mult * d
            mult *= nb
        return bid

    def fit(self, X, y, sample_weight=None):
        y = np.asarray(y)
        self.edges_ = []
        for ci, nb in self.specs:
            qs = np.quantile(X[:, ci], np.linspace(0, 1, nb + 1)[1:-1])
            self.edges_.append(qs)
        bid = self._bucket_ids(X)
        self.global_ = lightgbm.LGBMClassifier(**self.gp)
        self.global_.fit(X, y)
        self.models_ = {}
        for b in np.unique(bid):
            m = bid == b
            if m.sum() >= self.min_bucket and len(np.unique(y[m])) == 2:
                mod = lightgbm.LGBMClassifier(**self.bp)
                mod.fit(X[m], y[m])
                self.models_[int(b)] = mod
        return self

    def predict_proba(self, X):
        p = self.global_.predict_proba(X)[:, 1]
        bid = self._bucket_ids(X)
        for b, mod in self.models_.items():
            m = bid == b
            if m.any():
                pb = mod.predict_proba(X[m])[:, 1]
                p[m] = self.blend * p[m] + (1 - self.blend) * pb
        return np.column_stack([1 - p, p])


def run_config(name, make_model, X, yK, fwdK, valid, ot, bounds):
    pooled = {cov: [] for cov in COVERAGES}
    perwin = []
    for w in range(len(bounds) - 1):
        ws, we = bounds[w], bounds[w + 1]
        te = np.where(valid & (ot >= ws) & (ot < we))[0]
        tr = np.where(valid & (ot < ws - EMB * 300000))[0]
        cut = int(len(tr) * 0.9)
        fit_idx, cal_idx = tr[:cut], tr[cut:][EMB:]
        model = make_model()
        model.fit(X[fit_idx], yK[fit_idx].astype(int))
        iso = IsotonicRegression(out_of_bounds="clip")
        p_cal_raw = model.predict_proba(X[cal_idx])[:, 1]
        iso.fit(p_cal_raw, yK[cal_idx].astype(int))
        p_cal = iso.transform(p_cal_raw)
        p_te = iso.transform(model.predict_proba(X[te])[:, 1])
        s_cal = np.abs(p_cal - 0.5); s_te = np.abs(p_te - 0.5)
        yte = yK[te].astype(int)
        row = {}
        for cov in COVERAGES:
            thr = np.quantile(s_cal, 1 - cov)
            sel = s_te >= thr
            if sel.sum():
                correct = ((p_te[sel] > 0.5).astype(int) == yte[sel])
                pooled[cov].extend(correct.tolist())
                row[cov] = (round(float(correct.mean()), 3), int(sel.sum()))
            else:
                row[cov] = (float("nan"), 0)
        perwin.append(row)
    out = {name: {}}
    line = f"{name:34s}"
    for cov in COVERAGES:
        arr = pooled[cov]
        hit = float(np.mean(arr)) if arr else float("nan")
        nw = sum(1 for r in perwin if r[cov][1] >= 10 and r[cov][0] > 0.5)
        line += f" | c{cov*100:4.1f}: {hit:.4f}/{len(arr):5d} w>{nw}"
        out[name][cov] = (hit, len(arr))
    print(line, flush=True)
    print("   perwin@2.5%:", [r[0.025] for r in perwin], flush=True)
    return out


def main():
    df = pd.read_pickle(os.path.join(LAB, "features_v1.pkl"))
    df = df[df["open_time"] < PRE_END].reset_index(drop=True)
    cols = [c for c in df.columns if c not in ("open_time", "close")]
    IDX = {c: i for i, c in enumerate(cols)}
    c = df["close"].values.astype(float)
    ot = df["open_time"].values.astype(np.int64)
    fwdK = np.full(len(c), np.nan); fwdK[:-K] = c[K:] / c[:-K] - 1
    yK = (fwdK > 0).astype(float)
    valid = df[cols].notna().all(axis=1).values & ~np.isnan(fwdK)
    X = df[cols].values.astype(np.float64)
    bounds = [PRE_END - (N_WIN - i) * WIN_MS for i in range(N_WIN + 1)]
    print("pseudo-windows:", [str(pd.to_datetime(b, unit='ms'))[:10] for b in bounds])
    print("valid rows:", valid.sum())

    vol = IDX["rv48_prank_30d"]; er = IDX["er_48"]; adx = IDX["adx_14"]
    er288 = IDX["er_288"]; dvol = IDX["xa_dvol_pct90d"]

    configs = [
        ("baseline_lgbm", lambda: lightgbm.LGBMClassifier(**BASE_PARAMS)),
        ("mix_vol3xer2_b0", lambda: RegimeMixture([(vol, 3), (er, 2)], 5000, 0.0)),
        ("mix_vol3xer2_b05", lambda: RegimeMixture([(vol, 3), (er, 2)], 5000, 0.5)),
        ("mix_vol3_b0", lambda: RegimeMixture([(vol, 3)], 5000, 0.0)),
        ("mix_vol3_b05", lambda: RegimeMixture([(vol, 3)], 5000, 0.5)),
        ("mix_vol2xadx2_b05", lambda: RegimeMixture([(vol, 2), (adx, 2)], 5000, 0.5)),
        ("mix_er288x3_b05", lambda: RegimeMixture([(er288, 3)], 5000, 0.5)),
    ]
    which = sys.argv[2].split(",") if len(sys.argv) > 2 else None
    for name, mk in configs:
        if which and name not in which:
            continue
        t0 = time.time()
        run_config(name, mk, X, yK, fwdK, valid, ot, bounds)
        print(f"   ({time.time()-t0:.0f}s)", flush=True)


if __name__ == "__main__":
    main()
