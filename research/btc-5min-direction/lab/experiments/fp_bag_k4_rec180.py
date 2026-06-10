"""FINAL ROUND fp lane run 3: 5-seed-bagged baseline LGBM, K=4, recency half-life 180d.

K=4 won run 1 vs run 2 (58.29% vs 56.91% pooled at cov 5%); this probes a gentler
decay (HL=180) at the winning K. Same seed bag (101..105), same baseline config.

PRIMARY_COV pre-registered at 0.05 BEFORE first harness run.
"""
import numpy as np
import lightgbm

NAME = "fp_bag_k4_rec180"
K = 4
PRIMARY_COV = 0.05
HALF_LIFE_DAYS = 180.0
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
