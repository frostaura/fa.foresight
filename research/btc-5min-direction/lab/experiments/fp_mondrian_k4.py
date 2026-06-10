"""FINAL ROUND fp lane: per-regime (Mondrian) calibration, 5-seed-bagged LGBM, K=4.

Targets audit finding 3 (bear overconfidence: stated confidence exceeded realized
hit by 8-14pp in bear windows). Mechanism:
  - base = 5-seed bag of baseline LGBM (random_state 101..105), predict_proba = mean
    (audit finding 1: single-seed results are luck, not skill),
  - inside fit(): hold out the final 15% of the fit rows (purged EMB=64 bars),
    bucket holdout rows by a CAUSAL regime key:
        sign(ret_48) x tercile(rv48_prank_30d)   -> 6 buckets
    (ret_48 = 48-bar return, col idx 19; rv48_prank_30d = 30d percentile rank of
    48-bar realized vol, col idx 7 — both plain feature columns, fixed tercile
    edges 1/3, 2/3 because the vol feature is already a percentile),
  - fit one IsotonicRegression PER BUCKET mapping raw bagged p -> empirical P(up);
    buckets with < 800 holdout rows fall back to a global isotonic,
  - predict_proba emits the per-bucket-calibrated p plus a tiny monotone tie-break
    term 1e-4*(p_raw-0.5) so isotonic plateaus do not re-open the v1 tie-block
    coverage hole at the harness gate (shift <= 5e-5 in probability, calibration
    unaffected).
The harness's own global isotonic then sees already-regime-flattened probs and
roughly preserves them, so the confidence gate stops admitting overconfident
bear trades. Goal: stated confidence == realized hit in EVERY regime.

PRIMARY_COV pre-registered at 0.05 BEFORE first harness run (audit finding 2:
v2 exact coverage makes cov 2.5% pooled n ~ 2,185 < the 2,500 protocol floor).
"""
import os
import json
import numpy as np
import lightgbm
from sklearn.isotonic import IsotonicRegression

NAME = "fp_mondrian_k4"
K = 4
PRIMARY_COV = 0.05
SEEDS = [101, 102, 103, 104, 105]
EMB = K + 60                      # mirror harness purge
HOLDOUT_FRAC = 0.15
MIN_BUCKET = 800

# Pin feature order to the manifest so column indices are stable facts, not guesses.
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
    """Causal regime key per row: sign(ret_48) x vol tercile -> int in [0, 5]."""
    up = (X[:, IDX_RET48] >= 0).astype(int)
    v = X[:, IDX_VOLP]
    terc = np.where(v < 1.0 / 3.0, 0, np.where(v < 2.0 / 3.0, 1, 2))
    return up * 3 + terc


class MondrianBag:
    """5-seed-bagged LGBM with per-regime isotonic calibration learned on an
    internal purged chronological holdout. sklearn-like fit/predict_proba."""

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
        # monotone micro tie-break so plateaus stay sortable at the gate
        p = np.clip(p + 1e-4 * (p_raw - 0.5), 1e-6, 1 - 1e-6)
        return np.column_stack([1.0 - p, p])


def make_model():
    return MondrianBag()
