"""fp_basebag_k4 — matched CONTROL for fp_bearfix_k4 (the +variant run).

Identical in every respect (baseline a3 K=4 LGBM config, 5-seed bag 101..105,
default |p-0.5| score, PRIMARY_COV=0.05, harness v2) except NO falling-regime
sample weighting. Purpose: attribute fp_bearfix_k4's result to the mechanism vs
the seed bag + v2 exact coverage, and establish the honest seed-bagged v2
benchmark the audit said was missing. PRIMARY_COV pre-registered at 0.05 before
first harness run.
"""
import numpy as np
import lightgbm as lgb

NAME = "fp_basebag_k4"
K = 4
PRIMARY_COV = 0.05
SEEDS = (101, 102, 103, 104, 105)


class SeedBag:
    def fit(self, X, y, sample_weight=None):
        self.models = []
        for s in SEEDS:
            m = lgb.LGBMClassifier(
                n_estimators=350, learning_rate=0.03, num_leaves=47, max_depth=6,
                min_child_samples=150, subsample=0.8, subsample_freq=1,
                colsample_bytree=0.6, reg_lambda=8.0, random_state=s,
                n_jobs=3, verbose=-1)
            m.fit(X, y, sample_weight=sample_weight)
            self.models.append(m)
        return self

    def predict_proba(self, X):
        return np.mean([m.predict_proba(X) for m in self.models], axis=0)


def make_model():
    return SeedBag()
