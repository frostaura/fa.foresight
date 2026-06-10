"""syn horizon-mapping: baseline LightGBM config, K=5 (25-min horizon).

Identical hyperparameters to baseline_k3_lgbm / a3_k4_lgbm / a3_k6_lgbm;
only K differs. Fills the K=4 -> K=6 gap to test whether the K=4 spike is
a smooth horizon peak or a fluke of window alignment.
PRIMARY_COV pre-registered at 0.025 before first harness run. No hooks.
"""
import lightgbm

NAME = "syn_k5_lgbm"
K = 5
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
