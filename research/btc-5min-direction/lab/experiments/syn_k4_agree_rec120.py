"""syn: K=4 + agreement-gated calmean ensemble + recency hl120 sample weights.

Identical to syn_k4_agree (a3_calmean_gate3_k3 wrapper at K=4) plus the
a3_recency_hl120_k3 sample_weight hook: w = 0.5 ** (age_days / 120), age
relative to the newest fit row. The harness passes the weights into fit();
the wrapper slices them alongside X/y so the three sub-models (LGBM, XGB,
logistic) are fit recency-weighted on the first 90% of fit rows. The internal
per-model isotonic calibrations stay unweighted (calibration measures recent
data anyway — it is the last 10%).
PRIMARY_COV pre-registered at 0.025 before first harness run.
"""
import numpy as np
import lightgbm as lgb
import xgboost as xgb
from sklearn.linear_model import LogisticRegression
from sklearn.preprocessing import StandardScaler
from sklearn.pipeline import Pipeline
from sklearn.isotonic import IsotonicRegression

NAME = "syn_k4_agree_rec120"
K = 4
PRIMARY_COV = 0.025
EMB_INT = 64  # K + 60, internal purge
HALF_LIFE_DAYS = 120.0


class CalMeanAgreementEnsemble:
    EPS = 1e-4  # tie-breaker weight; max |contribution| = 0.5*EPS = 5e-5

    def fit(self, X, y, sample_weight=None):
        n = len(y)
        cut = int(n * 0.90)
        Xf, yf = X[:cut], y[:cut]
        wf = None if sample_weight is None else np.asarray(sample_weight, dtype=float)[:cut]
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
            if wf is None:
                m.fit(Xf, yf)
            elif nm == "log":
                m.fit(Xf, yf, lr__sample_weight=wf)
            else:
                m.fit(Xf, yf, sample_weight=wf)
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


def sample_weight(df_fit, y_fit, fwd_fit):
    ot = df_fit["open_time"].values.astype(np.int64)
    age_days = (ot.max() - ot) / 86400000.0
    return 0.5 ** (age_days / HALF_LIFE_DAYS)
