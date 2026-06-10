"""syn_k4_ms_agree — synthesis: multi-speed ensemble at K=4 + agreement shrinkage.

Hybrid of the two ensemble winners:
  * a3_mh_shlogfull_k3's capacity-diverse members (shallow LGBM / logistic /
    full LGBM), each with a private isotonic on an embargoed inner slice,
    transplanted to K=4 (the horizon winner, a3_k4_lgbm).
  * a3_calmean_gate3_k3's concordance gate: the calibrated-mean probability is
    shrunk fully to 0.5 whenever the three calibrated member sides disagree,
    with a tiny raw-mean tie-breaker (EPS=1e-4) that keeps ordering strict
    inside isotonic plateaus without ever flipping a side.

The harness then applies its own isotonic on the calib slice and selects by
|p-0.5| with thresholds from calib quantiles as usual — disagreement rows fall
to the bottom of the confidence ranking, so only concordant calls trade.

PRIMARY_COV pre-registered at 0.025 before first harness run.
"""
import numpy as np
import lightgbm as lgb
from sklearn.isotonic import IsotonicRegression
from sklearn.linear_model import LogisticRegression
from sklearn.preprocessing import StandardScaler
from sklearn.pipeline import make_pipeline

NAME = "syn_k4_ms_agree"
K = 4
PRIMARY_COV = 0.025
SEED = 42
EMB = K + 60  # 64


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


class CapacityAgreementEnsemble:
    """Members fit on first 85% of the fit slice; per-member isotonic fit on the
    embargoed last 15%; predict_proba = mean of calibrated member probs, shrunk
    fully to 0.5 when the three calibrated sides disagree (calmean-style gate),
    plus a tiny raw-mean tie-breaker for strict ordering."""

    EPS = 1e-4  # tie-breaker weight; max |contribution| = 0.5*EPS = 5e-5

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
        raw = [m.predict_proba(X)[:, 1] for _, m, _ in self.fitted_]
        cal = [iso.transform(r) for (_, _, iso), r in zip(self.fitted_, raw)]
        cm = np.column_stack(cal).mean(1)
        sides = [c > 0.5 for c in cal]
        agree = (sides[0] == sides[1]) & (sides[1] == sides[2])
        rm = np.column_stack(raw).mean(1)
        p = 0.5 + ((cm - 0.5) * agree + self.EPS * (rm - 0.5)) / (1.0 + self.EPS)
        p = np.clip(p, 1e-6, 1 - 1e-6)
        return np.column_stack([1.0 - p, p])


def make_model():
    return CapacityAgreementEnsemble()
