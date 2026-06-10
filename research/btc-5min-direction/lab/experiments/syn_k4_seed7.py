"""syn seed-robustness probe: IDENTICAL to a3_k4_lgbm except random_state=7.

Tests whether the K=4 lift (59.12% pooled @ cov 2.5%) survives a different
LGBM seed. Harness seed (SEED=42) is fixed by the frozen harness and is
untouched; only the model's internal RNG changes.
PRIMARY_COV pre-registered at 0.025 before first harness run. No hooks.
"""
import lightgbm

NAME = "syn_k4_seed7"
K = 4
PRIMARY_COV = 0.025


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
        random_state=7,
        n_jobs=3,
        verbose=-1,
    )
