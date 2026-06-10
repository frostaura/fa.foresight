"""a3 ensemble agreement gating — design 2: trees-only calibrated mean + gate.

Same wrapper skeleton as a3_calmean_gate3_k3 but the operating signal is the
mean of the two CALIBRATED tree models (LGBM + XGB); the gate requires only
tree concordance. Logistic is dropped from signal and gate (pre-window sweep
showed it dilutes the mean). Higher selection counts than the all-3 design.
"""
import numpy as np
import lightgbm as lgb
import xgboost as xgb
from sklearn.isotonic import IsotonicRegression

NAME = "a3_caltrees_gate_k3"
K = 3
PRIMARY_COV = 0.025
EMB_INT = 63


class CalTreesAgreementEnsemble:
    EPS = 1e-4

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
        }
        self.isos = {}
        for nm, m in self.models.items():
            m.fit(Xf, yf)
            iso = IsotonicRegression(out_of_bounds="clip")
            iso.fit(m.predict_proba(Xc)[:, 1], yc)
            self.isos[nm] = iso
        return self

    def predict_proba(self, X):
        cal, raw = {}, {}
        for nm, m in self.models.items():
            raw[nm] = m.predict_proba(X)[:, 1]
            cal[nm] = self.isos[nm].transform(raw[nm])
        cm = (cal["lgb"] + cal["xgb"]) / 2.0
        agree = (cal["lgb"] > 0.5) == (cal["xgb"] > 0.5)
        rm = (raw["lgb"] + raw["xgb"]) / 2.0
        p = 0.5 + ((cm - 0.5) * agree + self.EPS * (rm - 0.5)) / (1.0 + self.EPS)
        p = np.clip(p, 1e-6, 1 - 1e-6)
        return np.column_stack([1.0 - p, p])


def make_model():
    return CalTreesAgreementEnsemble()
