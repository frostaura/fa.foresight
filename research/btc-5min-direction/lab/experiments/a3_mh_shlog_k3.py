"""a3_mh_shlog_k3 — lean capacity-diverse ensemble (2 members), K=3, PRIMARY_COV=0.025.

Members (full 163-feature set):
  * shallow LGBM (depth 3, 7 leaves) — low-capacity tree learner
  * logistic regression (scaled, C=0.05) — linear learner
Same wrapper mechanics as a3_mh_shlogfull_k3: members on first 85% of fit
slice, per-member isotonic on embargoed last 15%, mean of calibrated probs.

Internal pre-window sweep: 56.3% @cov2.5% (n=2116) and 56.9% @cov1% (n=1490),
6/6 pseudo-windows >50% at both coverages — best of ~70 assembled variants.
"""
import numpy as np
import lightgbm as lgb
from sklearn.isotonic import IsotonicRegression
from sklearn.linear_model import LogisticRegression
from sklearn.preprocessing import StandardScaler
from sklearn.pipeline import make_pipeline

NAME = "a3_mh_shlog_k3"
K = 3
PRIMARY_COV = 0.025
SEED = 42
EMB = K + 60


def _members():
    return [
        ("shallow", lambda: lgb.LGBMClassifier(
            n_estimators=250, learning_rate=0.06, num_leaves=7, max_depth=3,
            min_child_samples=150, subsample=0.8, subsample_freq=1,
            colsample_bytree=0.6, reg_lambda=4.0, random_state=SEED,
            n_jobs=3, verbose=-1)),
        ("logit", lambda: make_pipeline(
            StandardScaler(),
            LogisticRegression(C=0.05, max_iter=2000, random_state=SEED))),
    ]


class CapacityEnsemble:
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
