"""syn_k4_grand — THE GRAND STACK: every positive a3 lever combined at K=4.

Levers (each individually positive on the ledger):
  * K=4 horizon                      (a3_k4_lgbm        59.12%)
  * three-family agreement ensemble  (a3_calmean_gate3  57.84%)
  * vol-bucket LGBM mixture          (a3_mix_vol5_b05   57.49%)
  * recency hl120 sample weights     (a3_recency_hl120  57.08%)

Architecture: the calmean agreement wrapper from a3_calmean_gate3_k3, with the
LGBM member replaced by the rv48_prank_30d 5-bucket RegimeMixture from
a3_mix_vol5_b05_k3 (global 350-tree LGBM blended 50/50 with 250-tree bucket
specialists, edges from fit data only). All three members (mixture / XGB /
logistic) receive exponential-decay recency weights, half-life 120 days,
delivered via the harness sample_weight hook and sliced to the inner core.

Inside fit: members train on the first 90% of the harness fit slice; each gets
a private isotonic on the purged (EMB=K+60) last 10%. predict_proba = equal-
weight mean of calibrated member probabilities, shrunk fully to 0.5 when the
three calibrated sides disagree; tiny raw-mean tie-breaker preserves strict
ordering inside isotonic plateaus without flipping sides. The harness then
applies its own isotonic + calib-quantile thresholds as usual.

PRIMARY_COV pre-registered at 0.025 before the first harness run.
"""
import os, json
import numpy as np
import lightgbm as lgb
import xgboost as xgb
from sklearn.linear_model import LogisticRegression
from sklearn.preprocessing import StandardScaler
from sklearn.pipeline import Pipeline
from sklearn.isotonic import IsotonicRegression

NAME = "syn_k4_grand"
K = 4
PRIMARY_COV = 0.025
EMB_INT = K + 60          # internal purge
HALF_LIFE_DAYS = 120.0

_LAB = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
_MANIFEST = json.load(open(os.path.join(_LAB, "features", "manifest.json")))
_IDX = {c: i for i, c in enumerate(_MANIFEST.keys())}
REGIME_CI = _IDX["rv48_prank_30d"]

BASE_PARAMS = dict(n_estimators=350, learning_rate=0.03, num_leaves=47, max_depth=6,
                   min_child_samples=150, subsample=0.8, subsample_freq=1,
                   colsample_bytree=0.6, reg_lambda=8.0, random_state=42,
                   n_jobs=3, verbose=-1)
BUCKET_PARAMS = dict(BASE_PARAMS, n_estimators=250)


class RegimeMixture:
    """vol5 bucket mixture from a3_mix_vol5_b05_k3, sample_weight-aware."""

    def __init__(self, ci=REGIME_CI, nb=5, min_bucket=5000, blend=0.5):
        self.ci, self.nb, self.min_bucket, self.blend = ci, nb, min_bucket, blend

    def _bid(self, X):
        return np.digitize(X[:, self.ci], self.edges_)

    def fit(self, X, y, sample_weight=None):
        y = np.asarray(y)
        self.edges_ = np.quantile(X[:, self.ci], np.linspace(0, 1, self.nb + 1)[1:-1])
        bid = self._bid(X)
        self.global_ = lgb.LGBMClassifier(**BASE_PARAMS)
        self.global_.fit(X, y, sample_weight=sample_weight)
        self.models_ = {}
        for b in np.unique(bid):
            m = bid == b
            if m.sum() >= self.min_bucket and len(np.unique(y[m])) == 2:
                mod = lgb.LGBMClassifier(**BUCKET_PARAMS)
                sw = None if sample_weight is None else sample_weight[m]
                mod.fit(X[m], y[m], sample_weight=sw)
                self.models_[int(b)] = mod
        return self

    def predict_proba(self, X):
        p = self.global_.predict_proba(X)[:, 1]
        bid = self._bid(X)
        for b, mod in self.models_.items():
            m = bid == b
            if m.any():
                p[m] = self.blend * p[m] + (1 - self.blend) * mod.predict_proba(X[m])[:, 1]
        return np.column_stack([1 - p, p])


class GrandStack:
    EPS = 1e-4  # tie-breaker weight; max |contribution| = 5e-5

    def fit(self, X, y, sample_weight=None):
        y = np.asarray(y)
        n = len(y)
        cut = int(n * 0.90)
        Xf, yf = X[:cut], y[:cut]
        Xc, yc = X[cut + EMB_INT:], y[cut + EMB_INT:]
        sw = None if sample_weight is None else np.asarray(sample_weight, float)[:cut]
        self.models = {}
        m = RegimeMixture()
        m.fit(Xf, yf, sample_weight=sw)
        self.models["mix"] = m
        m = xgb.XGBClassifier(n_estimators=350, learning_rate=0.03, max_depth=6,
                              min_child_weight=150, subsample=0.8, colsample_bytree=0.6,
                              reg_lambda=8.0, tree_method="hist", random_state=42,
                              n_jobs=3, eval_metric="logloss")
        m.fit(Xf, yf, sample_weight=sw)
        self.models["xgb"] = m
        m = Pipeline([("sc", StandardScaler()),
                      ("lr", LogisticRegression(C=0.5, max_iter=2000))])
        if sw is not None:
            m.fit(Xf, yf, lr__sample_weight=sw)
        else:
            m.fit(Xf, yf)
        self.models["log"] = m
        self.isos = {}
        for nm, mod in self.models.items():
            iso = IsotonicRegression(out_of_bounds="clip")
            iso.fit(mod.predict_proba(Xc)[:, 1], yc)
            self.isos[nm] = iso
        return self

    def predict_proba(self, X):
        cal, raw = {}, {}
        for nm, mod in self.models.items():
            raw[nm] = mod.predict_proba(X)[:, 1]
            cal[nm] = self.isos[nm].transform(raw[nm])
        cm = (cal["mix"] + cal["xgb"] + cal["log"]) / 3.0
        rm = (raw["mix"] + raw["xgb"] + raw["log"]) / 3.0
        sides = [cal[nm] > 0.5 for nm in ("mix", "xgb", "log")]
        agree = (sides[0] == sides[1]) & (sides[1] == sides[2])
        p = 0.5 + ((cm - 0.5) * agree + self.EPS * (rm - 0.5)) / (1.0 + self.EPS)
        p = np.clip(p, 1e-6, 1 - 1e-6)
        return np.column_stack([1.0 - p, p])


def make_model():
    return GrandStack()


def sample_weight(df_fit, y_fit, fwd_fit):
    ot = df_fit["open_time"].values.astype(np.int64)
    age_days = (ot.max() - ot) / 86400000.0
    return 0.5 ** (age_days / HALF_LIFE_DAYS)
