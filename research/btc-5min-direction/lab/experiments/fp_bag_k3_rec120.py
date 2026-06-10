"""FINAL ROUND fp lane: 5-seed-bagged baseline LGBM, K=3, recency half-life 120d.

Same as fp_bag_k4_rec120 but at K=3 (15-min horizon, the original baseline K).
Seed bag B=5 (random_state 101..105), predict_proba = mean across seeds.
Recency weights w = 0.5 ** (age_days / 120).

PRIMARY_COV pre-registered at 0.05 BEFORE first harness run.
"""
import numpy as np
import lightgbm

NAME = "fp_bag_k3_rec120"
K = 3
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
