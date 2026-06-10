"""a3_mh_shlogfull_k3 — capacity-diverse ensemble, K=3, PRIMARY_COV=0.025.

Members (all on the full 163-feature set):
  * shallow LGBM  (depth 3, 7 leaves)  — fast, low-capacity learner
  * logistic regression (scaled, C=0.05) — linear learner
  * full LGBM (baseline config)          — medium-capacity learner
Inside fit: members train on the first 85% of the fit slice; each member gets a
private isotonic calibration on the last 15% (embargoed EMB=63 bars); ensemble
probability = mean of per-member calibrated probabilities. The harness then
applies its own isotonic on the calib slice as usual.

Chosen from a pre-window internal sweep (6 pseudo-windows ending 2025-08-01,
harness-faithful geometry): this combo scored 56.3% @cov2.5% (n=2046, 6/6
windows >50%) vs 54.8% for the full LGBM alone on the same splits.
"""
import numpy as np
import lightgbm as lgb
from sklearn.isotonic import IsotonicRegression
from sklearn.linear_model import LogisticRegression
from sklearn.preprocessing import StandardScaler
from sklearn.pipeline import make_pipeline

NAME = "a3_mh_shlogfull_k3"
K = 3
PRIMARY_COV = 0.025
SEED = 42
EMB = K + 60


def _lgbm(**kw):
    base = dict(n_estimators=350, learning_rate=0.03, num_leaves=47, max_depth=6,
                min_child_samples=150, subsample=0.8, subsample_freq=1,
                colsample_bytree=0.6, reg_lambda=8.0, random_state=SEED,
                n_jobs=3, verbose=-1)
    base.update(kw)
    return lgb.LGBMClassifier(**base)


def _members():
    return [
        ("shallow", lambda: _lgbm(n_estimators=250, learning_rate=0.06,
                                  num_leaves=7, max_depth=3, reg_lambda=4.0)),
        ("logit", lambda: make_pipeline(
            StandardScaler(),
            LogisticRegression(C=0.05, max_iter=2000, random_state=SEED))),
        ("full", lambda: _lgbm()),
    ]


class CapacityEnsemble:
    """Members fit on first 85% of the fit slice; per-member isotonic fit on the
    embargoed last 15%; predict_proba = mean of calibrated member probs."""

    def __init__(self, members=None):
        self.members = members or _members()

    def fit(self, X, y, sample_weight=None):
        n = len(X)
        icut = int(n * 0.85)
        core = np.arange(icut)
        inner = np.arange(icut + EMB, n)
        y = np.asarray(y)
        self.fitted_ = []
        for name, fac in self.members:
            m = fac()
            if sample_weight is not None:
                try:
                    m.fit(X[core], y[core], sample_weight=np.asarray(sample_weight)[core])
                except TypeError:
                    m.fit(X[core], y[core])
            else:
                m.fit(X[core], y[core])
            iso = IsotonicRegression(out_of_bounds="clip", y_min=0.02, y_max=0.98)
            iso.fit(m.predict_proba(X[inner])[:, 1], y[inner])
            self.fitted_.append((name, m, iso))
        return self

    def predict_proba(self, X):
        cols = [iso.transform(m.predict_proba(X)[:, 1]) for _, m, iso in self.fitted_]
        p = np.column_stack(cols).mean(1)
        return np.column_stack([1 - p, p])


def make_model():
    return CapacityEnsemble()
