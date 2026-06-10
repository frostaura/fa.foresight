"""fp_blend_k4 — the honest blend (final-round, seed-bagged).

Per the 2026-06-10 adversarial audit:
  * SEED BAGGING: every tree model is trained B=5 times (random_state 101..105)
    and predict_proba is the bag mean — measures skill, not seed luck.
  * Blend of two genuinely different tree implementations:
      p = 0.5 * (p_lgbm_bag + p_xgb_bag)
    LGBM member  = the a3_k4 baseline config (n_estimators=350, lr=0.03,
                   num_leaves=47, max_depth=6, min_child_samples=150,
                   subsample=0.8/freq1, colsample_bytree=0.6, reg_lambda=8).
    XGB member   = hist, max_depth=6, n_estimators=350, lr=0.03,
                   subsample=0.8, colsample_bytree=0.6, reg_lambda=8.
  * K=4 (20-min horizon), PRIMARY_COV pre-registered at 0.05 (pooled n at 2.5%
    would fall below the 2,500 protocol floor under exact-coverage v2 gating).

Harness applies isotonic calibration + confidence gating downstream; this module
only supplies the blended raw probability.
"""
import numpy as np
import lightgbm
import xgboost
from joblib import Parallel, delayed

NAME = "fp_blend_k4"
K = 4
PRIMARY_COV = 0.05
SEEDS = [101, 102, 103, 104, 105]


def _lgbm(seed):
    return lightgbm.LGBMClassifier(
        n_estimators=350, learning_rate=0.03, num_leaves=47, max_depth=6,
        min_child_samples=150, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.6, reg_lambda=8.0, random_state=seed,
        n_jobs=3, verbose=-1)


def _xgb(seed):
    return xgboost.XGBClassifier(
        tree_method="hist", n_estimators=350, learning_rate=0.03, max_depth=6,
        subsample=0.8, colsample_bytree=0.6, reg_lambda=8.0,
        random_state=seed, n_jobs=3, verbosity=0, eval_metric="logloss")


class BlendBag:
    """5-seed-bagged LGBM + 5-seed-bagged XGB; p = 0.5*(mean_lgbm + mean_xgb)."""

    def fit(self, X, y, sample_weight=None):
        self.lgbms = [_lgbm(s) for s in SEEDS]
        self.xgbs = [_xgb(s) for s in SEEDS]

        def _fit(m):
            if sample_weight is not None:
                m.fit(X, y, sample_weight=sample_weight)
            else:
                m.fit(X, y)
            return m

        # trees release the GIL; 3 concurrent fits x n_jobs=3 each = 9 cores
        members = Parallel(n_jobs=3, backend="threading")(
            delayed(_fit)(m) for m in self.lgbms + self.xgbs)
        self.lgbms, self.xgbs = members[:5], members[5:]
        return self

    def predict_proba(self, X):
        p_l = np.mean([m.predict_proba(X)[:, 1] for m in self.lgbms], axis=0)
        p_x = np.mean([m.predict_proba(X)[:, 1] for m in self.xgbs], axis=0)
        p = 0.5 * (p_l + p_x)
        return np.column_stack([1 - p, p])


def make_model():
    return BlendBag()
