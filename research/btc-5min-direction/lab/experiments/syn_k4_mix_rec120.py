"""Synthesis: K=4 + vol-bucket regime mixture + recency decay (half-life 120d).

Same RegimeMixture wrapper as syn_k4_mix (replica of a3_mix_vol5_b05_k3, K=4),
plus a3_recency_hl120_k3's exponential sample decay w = 0.5 ** (age_days / 120).
The harness passes sample_weight into RegimeMixture.fit, which applies it to the
global model fit and, sliced per bucket (sample_weight[m]), to every bucket
specialist fit — i.e., the decay operates inside each bucket fit.

PRIMARY_COV pre-registered at 0.025 before first harness run.
"""
import os, json
import numpy as np
import lightgbm

NAME = "syn_k4_mix_rec120"
K = 4
PRIMARY_COV = 0.025
HALF_LIFE_DAYS = 120.0

_LAB = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
_MANIFEST = json.load(open(os.path.join(_LAB, "features", "manifest.json")))
_COLS = list(_MANIFEST.keys())
_IDX = {c: i for i, c in enumerate(_COLS)}

REGIME_SPECS = [(_IDX["rv48_prank_30d"], 5)]
MIN_BUCKET = 5000
BLEND = 0.5  # weight on global model where a bucket specialist exists

BASE_PARAMS = dict(n_estimators=350, learning_rate=0.03, num_leaves=47, max_depth=6,
                   min_child_samples=150, subsample=0.8, subsample_freq=1,
                   colsample_bytree=0.6, reg_lambda=8.0, random_state=42,
                   n_jobs=3, verbose=-1)
BUCKET_PARAMS = dict(BASE_PARAMS, n_estimators=250)


class RegimeMixture:
    def __init__(self, specs=REGIME_SPECS, min_bucket=MIN_BUCKET, blend=BLEND):
        self.specs = specs
        self.min_bucket = min_bucket
        self.blend = blend

    def _bucket_ids(self, X):
        bid = np.zeros(len(X), dtype=int)
        mult = 1
        for (ci, nb), edges in zip(self.specs, self.edges_):
            bid = bid + mult * np.digitize(X[:, ci], edges)
            mult *= nb
        return bid

    def fit(self, X, y, sample_weight=None):
        y = np.asarray(y)
        self.edges_ = [np.quantile(X[:, ci], np.linspace(0, 1, nb + 1)[1:-1])
                       for ci, nb in self.specs]
        bid = self._bucket_ids(X)
        self.global_ = lightgbm.LGBMClassifier(**BASE_PARAMS)
        if sample_weight is not None:
            self.global_.fit(X, y, sample_weight=sample_weight)
        else:
            self.global_.fit(X, y)
        self.models_ = {}
        for b in np.unique(bid):
            m = bid == b
            if m.sum() >= self.min_bucket and len(np.unique(y[m])) == 2:
                mod = lightgbm.LGBMClassifier(**BUCKET_PARAMS)
                if sample_weight is not None:
                    mod.fit(X[m], y[m], sample_weight=sample_weight[m])
                else:
                    mod.fit(X[m], y[m])
                self.models_[int(b)] = mod
        return self

    def predict_proba(self, X):
        p = self.global_.predict_proba(X)[:, 1]
        bid = self._bucket_ids(X)
        for b, mod in self.models_.items():
            m = bid == b
            if m.any():
                p[m] = self.blend * p[m] + (1 - self.blend) * mod.predict_proba(X[m])[:, 1]
        return np.column_stack([1 - p, p])


def make_model():
    return RegimeMixture()


def sample_weight(df_fit, y_fit, fwd_fit):
    ot = df_fit["open_time"].values.astype(np.int64)
    age_days = (ot.max() - ot) / 86400000.0
    return 0.5 ** (age_days / HALF_LIFE_DAYS)
