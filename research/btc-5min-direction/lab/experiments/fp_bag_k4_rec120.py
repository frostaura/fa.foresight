"""FINAL ROUND fp lane: 5-seed-bagged baseline LGBM, K=4, recency half-life 120d.

The combination every synthesis agent recommended but nobody ran:
  - baseline LGBM hyperparameters (exp_baseline_k3 config),
  - SEED BAG B=5 (random_state 101..105), predict_proba = mean across seeds
    (audit finding 1: single-seed results are coin flips on top of skill),
  - exponential recency sample weights w = 0.5 ** (age_days / 120)
    (a3_recency_hl120_k3 hook pattern),
  - K=4 (20-min horizon, best single-seed K in a3 sweep).

PRIMARY_COV pre-registered at 0.05 BEFORE first harness run (audit finding 2:
v2 exact coverage makes cov 2.5% pooled n ~ 2,185 < 2,500 protocol floor).
"""
import numpy as np
import lightgbm

NAME = "fp_bag_k4_rec120"
K = 4
PRIMARY_COV = 0.05
HALF_LIFE_DAYS = 120.0
SEEDS = [101, 102, 103, 104, 105]


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


class SeedBag:
    """sklearn-like wrapper: fit B copies differing only in random_state,
    predict_proba = mean of member probabilities."""

    def __init__(self, seeds=SEEDS):
        self.seeds = list(seeds)
        self.models_ = []

    def fit(self, X, y, sample_weight=None):
        self.models_ = []
        for s in self.seeds:
            m = _lgbm(s)
            m.fit(X, y, sample_weight=sample_weight)
            self.models_.append(m)
        return self

    def predict_proba(self, X):
        return np.mean([m.predict_proba(X) for m in self.models_], axis=0)


def make_model():
    return SeedBag()


def sample_weight(df_fit, y_fit, fwd_fit):
    ot = df_fit["open_time"].values.astype(np.int64)
    age_days = (ot.max() - ot) / 86400000.0
    return 0.5 ** (age_days / HALF_LIFE_DAYS)
