"""a3 horizon-refinement: baseline LightGBM config, K=4 (20-min horizon).

Identical hyperparameters to baseline_k3_lgbm; only K differs.
PRIMARY_COV pre-registered at 0.025 before first harness run.
"""
import lightgbm

NAME = "a3_k4_lgbm"
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
        random_state=42,
        n_jobs=3,
        verbose=-1,
    )
