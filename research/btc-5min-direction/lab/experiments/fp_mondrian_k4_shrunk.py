"""FINAL ROUND fp lane: fp_mondrian_k4 with SHRUNKEN per-regime calibration.

Identical to fp_mondrian_k4 (5-seed-bagged baseline LGBM, K=4, per-regime
isotonic over sign(ret_48) x vol-tercile buckets learned on an internal purged
15% holdout) except the emitted probability shrinks the per-bucket isotonic
toward the global isotonic by bucket sample size:

    p = w_b * iso_bucket(p_raw) + (1 - w_b) * iso_global(p_raw),
    w_b = n_b / (n_b + 1600)

Rationale (run-1 diagnostics): fp_mondrian_k4 hit 57.66% pooled at cov 5%
but showed per-bucket calibration noise — realized coverage drifted to ~2x
nominal in windows 2 and 6, meaning bucket isotonics fit on 1-4k holdout rows
move the score distribution between calib and test. Shrinkage keeps the
regime-conditional correction (bear overconfidence fix) while damping the
small-sample wiggle. Buckets below 800 rows already fall back to global
(w_b = 0). Tiny monotone tie-break term retained.

PRIMARY_COV pre-registered at 0.05 BEFORE first harness run.
"""
import os
import json
import numpy as np
import lightgbm
from sklearn.isotonic import IsotonicRegression

NAME = "fp_mondrian_k4_shrunk"
K = 4
PRIMARY_COV = 0.05
SEEDS = [101, 102, 103, 104, 105]
EMB = K + 60
HOLDOUT_FRAC = 0.15
MIN_BUCKET = 800
SHRINK_N0 = 1600.0

_MANIFEST = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                         "..", "features", "manifest.json")
FEATURES = list(json.load(open(_MANIFEST)))
IDX_RET48 = FEATURES.index("ret_48")          # = 19
IDX_VOLP = FEATURES.index("rv48_prank_30d")   # = 7


def _lgbm(seed):
    return lightgbm.LGBMClassifier(
        n_estimators=350,
        learning_rate=0.03,
        num_leaves=47,
        max_depth=6,
        min_child_samples=150,
        subsample=0.8,
        subsample_freq=1,
        colsample_bytree=0.6,
        reg_lambda=8.0,
        random_state=seed,
        n_jobs=3,
        verbose=-1,
    )


def _bucket(X):
    up = (X[:, IDX_RET48] >= 0).astype(int)
    v = X[:, IDX_VOLP]
    terc = np.where(v < 1.0 / 3.0, 0, np.where(v < 2.0 / 3.0, 1, 2))
    return up * 3 + terc


class MondrianBagShrunk:
    def fit(self, X, y, sample_weight=None):
        X = np.asarray(X, dtype=np.float64)
        y = np.asarray(y).astype(int)
        n = len(X)
        cut = int(n * (1.0 - HOLDOUT_FRAC))
        Xf, yf = X[:cut], y[:cut]
        wf = None if sample_weight is None else np.asarray(sample_weight, float)[:cut]
        Xh, yh = X[cut + EMB:], y[cut + EMB:]

        self.models_ = []
        for s in SEEDS:
            m = _lgbm(s)
            m.fit(Xf, yf, sample_weight=wf)
            self.models_.append(m)

        p_h = np.mean([m.predict_proba(Xh)[:, 1] for m in self.models_], axis=0)
        self.iso_global_ = IsotonicRegression(out_of_bounds="clip")
        self.iso_global_.fit(p_h, yh)
        self.iso_bucket_ = {}
        b_h = _bucket(Xh)
        for b in range(6):
            sel = b_h == b
            n_b = int(sel.sum())
            if n_b >= MIN_BUCKET:
                iso = IsotonicRegression(out_of_bounds="clip")
                iso.fit(p_h[sel], yh[sel])
                self.iso_bucket_[b] = (iso, n_b / (n_b + SHRINK_N0))
        return self

    def predict_proba(self, X):
        X = np.asarray(X, dtype=np.float64)
        p_raw = np.mean([m.predict_proba(X)[:, 1] for m in self.models_], axis=0)
        p_glob = self.iso_global_.transform(p_raw)
        p = p_glob.copy()
        b = _bucket(X)
        for bk, (iso, w) in self.iso_bucket_.items():
            sel = b == bk
            if sel.any():
                p[sel] = w * iso.transform(p_raw[sel]) + (1.0 - w) * p_glob[sel]
        p = np.clip(p + 1e-4 * (p_raw - 0.5), 1e-6, 1 - 1e-6)
        return np.column_stack([1.0 - p, p])


def make_model():
    return MondrianBagShrunk()
