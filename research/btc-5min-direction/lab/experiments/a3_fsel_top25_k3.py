"""a3 feature selection: top-25 features by pre-window LGBM gain importance
(avg over 3 expanding splits, K=3). Baseline LGBM config, K=3, PRIMARY_COV=0.025.

Internal pre-window sweep winner (pooled 56.3% @cov2.5% vs 54.2% all-163).
FEATURES list is frozen from a3_fsel_rank.json (pre-window data only).
"""
import os, json
import lightgbm

NAME = "a3_fsel_top25_k3"
K = 3
PRIMARY_COV = 0.025

_LAB = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
_rank = json.load(open(os.path.join(_LAB, "a3_fsel_rank.json")))
FEATURES = [f for f, _ in _rank["rank_gain"][:25]]


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
