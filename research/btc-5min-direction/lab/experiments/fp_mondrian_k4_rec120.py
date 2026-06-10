"""FINAL ROUND fp lane: fp_mondrian_k4 + exponential recency weights (HL=120d).

Identical to fp_mondrian_k4 (per-regime Mondrian isotonic calibration over a
5-seed-bagged baseline LGBM, K=4) plus the a3_recency_hl120 sample-weight hook:
    w = 0.5 ** (age_days / 120), age relative to newest fit row.
The wrapper subsets the harness-supplied weights to its internal 85% fit slice;
the regime isotonics stay unweighted (calibration should reflect plain
empirical frequency on the holdout).

PRIMARY_COV pre-registered at 0.05 BEFORE first harness run.
"""
import os
import json
import numpy as np
import lightgbm
from sklearn.isotonic import IsotonicRegression

NAME = "fp_mondrian_k4_rec120"
K = 4
PRIMARY_COV = 0.05
SEEDS = [101, 102, 103, 104, 105]
EMB = K + 60
HOLDOUT_FRAC = 0.15
MIN_BUCKET = 800
HALF_LIFE_DAYS = 120.0

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


class MondrianBag:
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
            if sel.sum() >= MIN_BUCKET:
                iso = IsotonicRegression(out_of_bounds="clip")
                iso.fit(p_h[sel], yh[sel])
                self.iso_bucket_[b] = iso
        return self

    def predict_proba(self, X):
        X = np.asarray(X, dtype=np.float64)
        p_raw = np.mean([m.predict_proba(X)[:, 1] for m in self.models_], axis=0)
        p = self.iso_global_.transform(p_raw)
        b = _bucket(X)
        for bk, iso in self.iso_bucket_.items():
            sel = b == bk
            if sel.any():
                p[sel] = iso.transform(p_raw[sel])
        p = np.clip(p + 1e-4 * (p_raw - 0.5), 1e-6, 1 - 1e-6)
        return np.column_stack([1.0 - p, p])


def make_model():
    return MondrianBag()


def sample_weight(df_fit, y_fit, fwd_fit):
    ot = df_fit["open_time"].values.astype(np.int64)
    age_days = (ot.max() - ot) / 86400000.0
    return 0.5 ** (age_days / HALF_LIFE_DAYS)
