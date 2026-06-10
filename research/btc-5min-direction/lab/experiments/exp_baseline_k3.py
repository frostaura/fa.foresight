"""Baseline reference experiment: same LightGBM, K=3 (15-min horizon).

Plugs into the frozen chaos_harness_v1.py. No custom hooks.
"""
import lightgbm

NAME = "baseline_k3_lgbm"
K = 3
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
        n_jobs=-1,
        verbose=-1,
    )
