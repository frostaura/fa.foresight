"""a3 recency-weighting experiment: exponential decay sample weights, half-life 60 days.

Baseline LGBM config (exp_baseline_k3), K=3, PRIMARY_COV=0.025 (pre-registered).
w = 0.5 ** (age_days / 60), age relative to newest fit row.
"""
import numpy as np
import lightgbm

NAME = "a3_recency_hl60_k3"
K = 3
PRIMARY_COV = 0.025
HALF_LIFE_DAYS = 60.0


def make_model():
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
        random_state=42,
        n_jobs=3,
        verbose=-1,
    )


def sample_weight(df_fit, y_fit, fwd_fit):
    ot = df_fit["open_time"].values.astype(np.int64)
    age_days = (ot.max() - ot) / 86400000.0
    return 0.5 ** (age_days / HALF_LIFE_DAYS)
