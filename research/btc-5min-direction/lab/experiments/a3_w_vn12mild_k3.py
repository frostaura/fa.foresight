"""a3 objective shaping — MILD soft triple-barrier sample weights (no rebalance).

w = clip(r / median(r), 0.7, 2),  r = |fwd_K| / (rv_cc_12 * sqrt(K))

Chosen on PRE-WINDOW internal sweeps (results/sweep_a3_objshape*_internal.json):
mild clipping was the best non-rebalanced weighted config (0.5423 @cov0.025
n=4359; best-of-family 0.5403 @cov0.05 vs base 0.5330). Aggressive clips
(0.2,5)/(0.1,10), |fwd|-only weights, and recency x magnitude were all worse.
Baseline LGBM, K=3, PRIMARY_COV=0.025.
"""
import numpy as np
import lightgbm

NAME = "a3_w_vn12mild_k3"
K = 3
PRIMARY_COV = 0.025
SQ = np.sqrt(K)


def sample_weight(df_fit, y_fit, fwd_fit):
    rv = df_fit["rv_cc_12"].values.astype(float)
    r = np.abs(fwd_fit) / (rv * SQ + 1e-12)
    return np.clip(r / np.median(r), 0.7, 2)


def make_model():
    return lightgbm.LGBMClassifier(
        n_estimators=350, learning_rate=0.03, num_leaves=47, max_depth=6,
        min_child_samples=150, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.6, reg_lambda=8.0, random_state=42,
        n_jobs=3, verbose=-1)
