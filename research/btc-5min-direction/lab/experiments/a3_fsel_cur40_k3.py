"""a3 feature selection: hand-curated 40 features.

Built on PRE-WINDOW data only: top-60 by avg LGBM gain (3 expanding splits, K=3)
filtered to positive permutation AUC-drop on a purged pre-window validation tail
(32 features), plus 8 mechanism-plausible directional adds (taker-imbalance EMAs,
RSI, perp/spot gap, cross-asset 1h returns, efficiency ratio, SMA z).
List frozen in a3_fsel_curated.json. Baseline LGBM, K=3, PRIMARY_COV=0.025.
"""
import os, json
import lightgbm

NAME = "a3_fsel_cur40_k3"
K = 3
PRIMARY_COV = 0.025

_LAB = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FEATURES = json.load(open(os.path.join(_LAB, "a3_fsel_curated.json")))


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
