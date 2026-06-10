"""fp_bearfix_k4 — bear-window fix via falling-regime row upweighting (x2).

PRE-WINDOW DIAGNOSIS (fp_diag_bear*.py, 6 walk-forward folds Feb-Jul 2025, 5% cov,
NO data >= 2025-08-01 touched):
  * Error asymmetry is NOT "bad longs in bears": SHORT calls are the overconfident
    side everywhere (hit 55.0% vs stated 68.8%, worst in non-falling states), while
    falling-state longs hit 56.4% and the 20-min base rate is UP-skewed (52.5%)
    when 24h ret < -1% (mean reversion).
  * Selection-level fixes FAILED pre-window: short-penalty / crash-penalty score
    gates moved pooled hit by <= +0.1pp; long-only was worse.
  * Training-side mechanisms, tested head-to-head (5-seed bags):
      base 55.69% | crash x momentum interactions 55.00% | routed falling-regime
      specialist 53.63% | falling rows upweighted x2 -> 56.57%, crash-state trades
      57.6% vs 55.9% base. Weight sweep: w=1.5 -> 56.0%, w=2.0 -> 56.6%, w=3.0 -> 56.1%.
MECHANISM (one): sign-neutral regime emphasis — training rows with 24h log return
< -1% (causal feature ret_288) get sample_weight 2.0. Asymmetric CLASS weighting was
rejected by the diagnosis (it would fight the up-skewed falling base rate). Model:
baseline a3 K=4 LGBM config, 5-SEED BAG (101..105) per the seed-artifact audit.
PRIMARY_COV pre-registered at 0.05 before first harness run (exact-coverage v2 makes
2.5% undershoot the 2,500 pooled floor). Default |p-0.5| score; no score hook.
"""
import numpy as np
import lightgbm as lgb

NAME = "fp_bearfix_k4"
K = 4
PRIMARY_COV = 0.05
SEEDS = (101, 102, 103, 104, 105)
FALL_THR = -0.01
FALL_W = 2.0


class SeedBag:
    def fit(self, X, y, sample_weight=None):
        self.models = []
        for s in SEEDS:
            m = lgb.LGBMClassifier(
                n_estimators=350, learning_rate=0.03, num_leaves=47, max_depth=6,
                min_child_samples=150, subsample=0.8, subsample_freq=1,
                colsample_bytree=0.6, reg_lambda=8.0, random_state=s,
                n_jobs=3, verbose=-1)
            m.fit(X, y, sample_weight=sample_weight)
            self.models.append(m)
        return self

    def predict_proba(self, X):
        return np.mean([m.predict_proba(X) for m in self.models], axis=0)


def make_model():
    return SeedBag()


def sample_weight(df_fit, y_fit, fwd_fit):
    return np.where(df_fit["ret_288"].values < FALL_THR, FALL_W, 1.0)
