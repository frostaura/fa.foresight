"""a3 magnitude-conditioned training — HARD mask variant.

Train only on rows whose |K-bar forward return| >= THETA * rv_cc_48 * sqrt(K).
Measurement (calib slice, thresholds, test rows) untouched — harness guarantees it.
Baseline LGBM (same params as baseline_k3_lgbm), K=3, PRIMARY_COV=0.025.
THETA chosen on PRE-WINDOW internal sweep (see results/a3_internal_sweep.json).
"""
import numpy as np
import lightgbm

NAME = "a3_magcond_hard_t05"
K = 3
PRIMARY_COV = 0.025
THETA = 0.5
SQ = np.sqrt(K)


def train_mask(df_fit, y_fit, fwd_fit):
    rv = df_fit["rv_cc_48"].values.astype(float)
    return np.abs(fwd_fit) >= THETA * rv * SQ


def make_model():
    return lightgbm.LGBMClassifier(
        n_estimators=350, learning_rate=0.03, num_leaves=47, max_depth=6,
        min_child_samples=150, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.6, reg_lambda=8.0, random_state=42,
        n_jobs=3, verbose=-1)
