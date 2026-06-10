"""a3 lever: model family + hyperparameters. Candidate 1: deep-but-slow LGBM
(num_leaves=127, lr=0.01, 800 trees, heavy regularization).

Selected from a 17-config internal expanding walk-forward sweep on PRE-WINDOW data
only (6x25-day windows ending 2025-08-01, harness-faithful mechanics): pooled
0.5635 @cov2.5 / 0.5673 @cov1 / 0.5611 @cov5, 6/6 internal windows >50%
(min 0.527), vs baseline-ref 0.5446 @cov2.5 on the same splits.

K=3, PRIMARY_COV=0.025 pre-registered before first harness run.
"""
import lightgbm

NAME = "a3_hp1_lgbm_deep127_slow"
K = 3
PRIMARY_COV = 0.025


def make_model():
    return lightgbm.LGBMClassifier(
        n_estimators=800,
        learning_rate=0.01,
        num_leaves=127,
        min_child_samples=300,
        subsample=0.8,
        subsample_freq=1,
        colsample_bytree=0.5,
        reg_lambda=20.0,
        random_state=42,
        n_jobs=3,
        verbose=-1,
    )
