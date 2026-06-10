"""a3 objective shaping — soft triple-barrier sample weights + class rebalance.

w = clip(r / median(r), 0.2, 5),  r = |fwd_K| / (rv_cc_12 * sqrt(K))
then rescaled so sum(w | up) == sum(w | down) in the fit slice.

Chosen on PRE-WINDOW internal sweep (results/sweep_a3_objshape_internal.json):
the only weighted config >= unweighted base at cov 0.025 (0.5481 vs 0.5465,
n=3304 vs 2494 over 6 pre-windows). All aggressive weight variants hurt.
Baseline LGBM, K=3, PRIMARY_COV=0.025.
"""
import numpy as np
import lightgbm

NAME = "a3_w_cbvn12_k3"
K = 3
PRIMARY_COV = 0.025
SQ = np.sqrt(K)


def sample_weight(df_fit, y_fit, fwd_fit):
    rv = df_fit["rv_cc_12"].values.astype(float)
    r = np.abs(fwd_fit) / (rv * SQ + 1e-12)
    w = np.clip(r / np.median(r), 0.2, 5)
    y = np.asarray(y_fit)
    su, sd = w[y == 1].sum(), w[y == 0].sum()
    tgt = (su + sd) / 2.0
    w = w.copy()
    w[y == 1] *= tgt / su
    w[y == 0] *= tgt / sd
    return w


def make_model():
    return lightgbm.LGBMClassifier(
        n_estimators=350, learning_rate=0.03, num_leaves=47, max_depth=6,
        min_child_samples=150, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.6, reg_lambda=8.0, random_state=42,
        n_jobs=3, verbose=-1)
