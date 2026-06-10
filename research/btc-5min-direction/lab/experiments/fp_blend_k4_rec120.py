"""fp_blend_k4_rec120 — the honest blend + recency weighting (final-round).

Identical to fp_blend_k4 (5-seed-bagged LGBM + 5-seed-bagged XGB, p = mean of
member-bag means) with exponential recency sample weights on BOTH members:
    w = 0.5 ** (age_days / 120), age relative to newest fit row.
K=4, PRIMARY_COV pre-registered at 0.05.
"""
import numpy as np
import lightgbm
import xgboost
from joblib import Parallel, delayed

NAME = "fp_blend_k4_rec120"
K = 4
PRIMARY_COV = 0.05
SEEDS = [101, 102, 103, 104, 105]
HALF_LIFE_DAYS = 120.0


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
    def fit(self, X, y, sample_weight=None):
        self.lgbms = [_lgbm(s) for s in SEEDS]
        self.xgbs = [_xgb(s) for s in SEEDS]

        def _fit(m):
            if sample_weight is not None:
                m.fit(X, y, sample_weight=sample_weight)
            else:
                m.fit(X, y)
            return m

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


def sample_weight(df_fit, y_fit, fwd_fit):
    ot = df_fit["open_time"].values.astype(np.int64)
    age_days = (ot.max() - ot) / 86400000.0
    return 0.5 ** (age_days / HALF_LIFE_DAYS)
