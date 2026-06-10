"""syn: K=4 horizon + calibrated-mean agreement-gated 3-model ensemble.

Combines the two strongest ledger results: a3_k4_lgbm (K=4 horizon winner,
59.12%) and a3_calmean_gate3_k3's wrapper (57.84%). The wrapper is replicated
exactly — LGBM (baseline config) + XGBoost (hist, similar size) + logistic
(standardized, C=0.5) fit on the first 90% of fit rows, per-model isotonic
calibration on the last 10% (purged K+60 bars), equal-weight mean of the three
CALIBRATED probabilities shrunk fully to 0.5 when the three calibrated sides
disagree, tiny raw-mean tie-breaker for strict ordering inside isotonic
plateaus. Only K changes: 3 -> 4 (EMB_INT 63 -> 64).
PRIMARY_COV pre-registered at 0.025 before first harness run.
"""
import numpy as np
import lightgbm as lgb
import xgboost as xgb
from sklearn.linear_model import LogisticRegression
from sklearn.preprocessing import StandardScaler
from sklearn.pipeline import Pipeline
from sklearn.isotonic import IsotonicRegression

NAME = "syn_k4_agree"
K = 4
PRIMARY_COV = 0.025
EMB_INT = 64  # K + 60, internal purge


class CalMeanAgreementEnsemble:
    EPS = 1e-4  # tie-breaker weight; max |contribution| = 0.5*EPS = 5e-5

    def fit(self, X, y, sample_weight=None):
        n = len(y)
        cut = int(n * 0.90)
        Xf, yf = X[:cut], y[:cut]
        Xc, yc = X[cut + EMB_INT:], y[cut + EMB_INT:]
        self.models = {
            "lgb": lgb.LGBMClassifier(
                n_estimators=350, learning_rate=0.03, num_leaves=47, max_depth=6,
                min_child_samples=150, subsample=0.8, subsample_freq=1,
                colsample_bytree=0.6, reg_lambda=8.0, random_state=42,
                n_jobs=3, verbose=-1),
            "xgb": xgb.XGBClassifier(
                n_estimators=350, learning_rate=0.03, max_depth=6,
                min_child_weight=150, subsample=0.8, colsample_bytree=0.6,
                reg_lambda=8.0, tree_method="hist", random_state=42,
                n_jobs=3, eval_metric="logloss"),
            "log": Pipeline([
                ("sc", StandardScaler()),
                ("lr", LogisticRegression(C=0.5, max_iter=2000))]),
        }
        self.isos = {}
        for nm, m in self.models.items():
            m.fit(Xf, yf)
            iso = IsotonicRegression(out_of_bounds="clip")
            iso.fit(m.predict_proba(Xc)[:, 1], yc)
            self.isos[nm] = iso
        return self

    def predict_proba(self, X):
        cal = {}
        raw = {}
        for nm, m in self.models.items():
            raw[nm] = m.predict_proba(X)[:, 1]
            cal[nm] = self.isos[nm].transform(raw[nm])
        cm = (cal["lgb"] + cal["xgb"] + cal["log"]) / 3.0
        sides = [cal[nm] > 0.5 for nm in ("lgb", "xgb", "log")]
        agree = (sides[0] == sides[1]) & (sides[1] == sides[2])
        rm = (raw["lgb"] + raw["xgb"] + raw["log"]) / 3.0
        p = 0.5 + ((cm - 0.5) * agree + self.EPS * (rm - 0.5)) / (1.0 + self.EPS)
        p = np.clip(p, 1e-6, 1 - 1e-6)
        return np.column_stack([1.0 - p, p])


def make_model():
    return CalMeanAgreementEnsemble()
