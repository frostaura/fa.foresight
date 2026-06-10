"""fp final-round honest horizon map: 5-seed-bagged baseline LGBM, K=4 (20-min).

Audit response: single-seed results are RNG coin flips (a3_k4 59.12% -> 55.32%
under seed 7). This bags B=5 LGBMs (random_state 101..105), predict_proba = mean,
to measure skill with seed luck removed. Baseline hyperparameters, no hooks.
PRIMARY_COV pre-registered at 0.05 before first harness run (cov 2.5% pooled n
would fall below the 2,500 protocol floor under v2 exact coverage).
"""
import numpy as np
import lightgbm

NAME = "fp_bag_k4"
K = 4
PRIMARY_COV = 0.05

SEEDS = (101, 102, 103, 104, 105)


class SeedBagLGBM:
    """Average predict_proba over B baseline LGBMs differing only in random_state."""

    def __init__(self):
        self.models = [
            lightgbm.LGBMClassifier(
                n_estimators=350,
                learning_rate=0.03,
                num_leaves=47,
                max_depth=6,
                min_child_samples=150,
                subsample=0.8,
                subsample_freq=1,
                colsample_bytree=0.6,
                reg_lambda=8.0,
                random_state=s,
                n_jobs=3,
                verbose=-1,
            )
            for s in SEEDS
        ]

    def fit(self, X, y, sample_weight=None):
        for m in self.models:
            if sample_weight is not None:
                m.fit(X, y, sample_weight=sample_weight)
            else:
                m.fit(X, y)
        return self

    def predict_proba(self, X):
        return np.mean([m.predict_proba(X) for m in self.models], axis=0)


def make_model():
    return SeedBagLGBM()
