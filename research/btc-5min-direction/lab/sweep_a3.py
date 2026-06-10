#!/usr/bin/env python3
"""Internal pre-window sweep for a3 (model family + hyperparams). NOT the harness.

Mimics chaos_harness_v1 mechanics on PRE-WINDOW data only (open_time < 2025-08-01):
  * 6 internal test windows of 25 days each ending 2025-08-01
  * train = all valid rows strictly before window_start - EMB bars (EMB = K+60)
  * fit = first 90%, calib = last 10% purged EMB, isotonic on calib,
    thresholds = calib score quantiles, K=3.
Reports pooled hit at cov 0.05/0.025/0.01 + per-window hits at 0.025.
"""
import warnings; warnings.filterwarnings("ignore")
import sys, os, time, json
import numpy as np
import pandas as pd
from sklearn.isotonic import IsotonicRegression
from sklearn.linear_model import LogisticRegression
from sklearn.preprocessing import StandardScaler
import lightgbm as lgb
import xgboost as xgb

LAB = os.path.dirname(os.path.abspath(__file__))
PRE_END = 1754006400000
K = 3
EMB = K + 60
DAY = 86400000
N_WIN = 6
WIN_MS = 25 * DAY
COVS = [0.05, 0.025, 0.01]
SEED = 42


class Blend:
    """w_lgbm * LGBM + (1-w) * logistic (standardized inside)."""
    def __init__(self, lgbm_params, w=0.6, C=0.05):
        self.lgbm = lgb.LGBMClassifier(**lgbm_params)
        self.w = w
        self.scaler = StandardScaler()
        self.logit = LogisticRegression(C=C, max_iter=2000, n_jobs=3)

    def fit(self, X, y, sample_weight=None):
        self.lgbm.fit(X, y)
        Xs = self.scaler.fit_transform(X)
        self.logit.fit(Xs, y)
        return self

    def predict_proba(self, X):
        p1 = self.lgbm.predict_proba(X)[:, 1]
        p2 = self.logit.predict_proba(self.scaler.transform(X))[:, 1]
        p = self.w * p1 + (1 - self.w) * p2
        return np.column_stack([1 - p, p])


def lgbm_cfg(**kw):
    base = dict(random_state=SEED, n_jobs=3, verbose=-1)
    base.update(kw)
    return base


CONFIGS = {
    "bl_ref": lambda: lgb.LGBMClassifier(**lgbm_cfg(n_estimators=350, learning_rate=0.03,
        num_leaves=47, max_depth=6, min_child_samples=150, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.6, reg_lambda=8.0)),
    "lgbm_shallow": lambda: lgb.LGBMClassifier(**lgbm_cfg(n_estimators=500, learning_rate=0.03,
        num_leaves=15, min_child_samples=200, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.5, reg_lambda=5.0)),
    "lgbm_shallow_slow": lambda: lgb.LGBMClassifier(**lgbm_cfg(n_estimators=800, learning_rate=0.02,
        num_leaves=15, min_child_samples=100, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.5, reg_lambda=5.0)),
    "lgbm_mid_slow": lambda: lgb.LGBMClassifier(**lgbm_cfg(n_estimators=600, learning_rate=0.02,
        num_leaves=31, min_child_samples=150, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.6, reg_lambda=8.0)),
    "lgbm_deep": lambda: lgb.LGBMClassifier(**lgbm_cfg(n_estimators=500, learning_rate=0.02,
        num_leaves=63, min_child_samples=300, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.6, reg_lambda=10.0)),
    "lgbm_deeper": lambda: lgb.LGBMClassifier(**lgbm_cfg(n_estimators=800, learning_rate=0.01,
        num_leaves=127, min_child_samples=300, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.5, reg_lambda=20.0)),
    "lgbm_fast31": lambda: lgb.LGBMClassifier(**lgbm_cfg(n_estimators=300, learning_rate=0.05,
        num_leaves=31, min_child_samples=100, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.6, reg_lambda=8.0)),
    "lgbm_63_cs04": lambda: lgb.LGBMClassifier(**lgbm_cfg(n_estimators=400, learning_rate=0.03,
        num_leaves=63, min_child_samples=150, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.4, reg_lambda=8.0)),
    "bl_cs04": lambda: lgb.LGBMClassifier(**lgbm_cfg(n_estimators=350, learning_rate=0.03,
        num_leaves=47, max_depth=6, min_child_samples=150, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.4, reg_lambda=8.0)),
    "xgb_d6": lambda: xgb.XGBClassifier(tree_method="hist", n_estimators=500, learning_rate=0.03,
        max_depth=6, min_child_weight=50, subsample=0.8, colsample_bytree=0.6,
        reg_lambda=8.0, n_jobs=3, random_state=SEED, eval_metric="logloss"),
    "xgb_d4": lambda: xgb.XGBClassifier(tree_method="hist", n_estimators=400, learning_rate=0.05,
        max_depth=4, min_child_weight=50, subsample=0.8, colsample_bytree=0.6,
        reg_lambda=5.0, n_jobs=3, random_state=SEED, eval_metric="logloss"),
    "xgb_d8": lambda: xgb.XGBClassifier(tree_method="hist", n_estimators=600, learning_rate=0.02,
        max_depth=8, min_child_weight=100, subsample=0.8, colsample_bytree=0.5,
        reg_lambda=20.0, n_jobs=3, random_state=SEED, eval_metric="logloss"),
    "blend_bl_60_40": lambda: Blend(lgbm_cfg(n_estimators=350, learning_rate=0.03,
        num_leaves=47, max_depth=6, min_child_samples=150, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.6, reg_lambda=8.0), w=0.6, C=0.05),
    "blend_mid_60_40": lambda: Blend(lgbm_cfg(n_estimators=600, learning_rate=0.02,
        num_leaves=31, min_child_samples=150, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.6, reg_lambda=8.0), w=0.6, C=0.05),
    # ---- round 2: refinements around lgbm_deeper / shallow_slow ----
    "deeper_cs04": lambda: lgb.LGBMClassifier(**lgbm_cfg(n_estimators=800, learning_rate=0.01,
        num_leaves=127, min_child_samples=300, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.4, reg_lambda=20.0)),
    "deep63_slow": lambda: lgb.LGBMClassifier(**lgbm_cfg(n_estimators=800, learning_rate=0.01,
        num_leaves=63, min_child_samples=300, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.5, reg_lambda=20.0)),
    "ens_deep_shal": lambda: AvgEns([
        lgb.LGBMClassifier(**lgbm_cfg(n_estimators=800, learning_rate=0.01,
            num_leaves=127, min_child_samples=300, subsample=0.8, subsample_freq=1,
            colsample_bytree=0.5, reg_lambda=20.0)),
        lgb.LGBMClassifier(**lgbm_cfg(n_estimators=800, learning_rate=0.02,
            num_leaves=15, min_child_samples=100, subsample=0.8, subsample_freq=1,
            colsample_bytree=0.5, reg_lambda=5.0))]),
    "deeper_bag2": lambda: AvgEns([
        lgb.LGBMClassifier(**lgbm_cfg(n_estimators=800, learning_rate=0.01,
            num_leaves=127, min_child_samples=300, subsample=0.8, subsample_freq=1,
            colsample_bytree=0.5, reg_lambda=20.0)),
        lgb.LGBMClassifier(**lgbm_cfg(n_estimators=800, learning_rate=0.01,
            num_leaves=127, min_child_samples=300, subsample=0.8, subsample_freq=1,
            colsample_bytree=0.5, reg_lambda=20.0, random_state=1337))]),
    # ---- round 3: ensemble refinements ----
    "ens3_dms": lambda: AvgEns([
        lgb.LGBMClassifier(**lgbm_cfg(n_estimators=800, learning_rate=0.01,
            num_leaves=127, min_child_samples=300, subsample=0.8, subsample_freq=1,
            colsample_bytree=0.5, reg_lambda=20.0)),
        lgb.LGBMClassifier(**lgbm_cfg(n_estimators=800, learning_rate=0.01,
            num_leaves=63, min_child_samples=300, subsample=0.8, subsample_freq=1,
            colsample_bytree=0.5, reg_lambda=20.0)),
        lgb.LGBMClassifier(**lgbm_cfg(n_estimators=800, learning_rate=0.02,
            num_leaves=15, min_child_samples=100, subsample=0.8, subsample_freq=1,
            colsample_bytree=0.5, reg_lambda=5.0))]),
    "ens_ds_bag4": lambda: AvgEns([
        lgb.LGBMClassifier(**lgbm_cfg(n_estimators=800, learning_rate=0.01,
            num_leaves=127, min_child_samples=300, subsample=0.8, subsample_freq=1,
            colsample_bytree=0.5, reg_lambda=20.0)),
        lgb.LGBMClassifier(**lgbm_cfg(n_estimators=800, learning_rate=0.01,
            num_leaves=127, min_child_samples=300, subsample=0.8, subsample_freq=1,
            colsample_bytree=0.5, reg_lambda=20.0, random_state=1337)),
        lgb.LGBMClassifier(**lgbm_cfg(n_estimators=800, learning_rate=0.02,
            num_leaves=15, min_child_samples=100, subsample=0.8, subsample_freq=1,
            colsample_bytree=0.5, reg_lambda=5.0)),
        lgb.LGBMClassifier(**lgbm_cfg(n_estimators=800, learning_rate=0.02,
            num_leaves=15, min_child_samples=100, subsample=0.8, subsample_freq=1,
            colsample_bytree=0.5, reg_lambda=5.0, random_state=1337))]),
}


class AvgEns:
    """Equal-weight probability average of submodels."""
    def __init__(self, models):
        self.models = models

    def fit(self, X, y, sample_weight=None):
        for m in self.models:
            m.fit(X, y)
        return self

    def predict_proba(self, X):
        p = np.mean([m.predict_proba(X)[:, 1] for m in self.models], axis=0)
        return np.column_stack([1 - p, p])


def main():
    only = sys.argv[1:] if len(sys.argv) > 1 else None
    df = pd.read_pickle(os.path.join(LAB, "features_v1.pkl"))
    df = df[df["open_time"] < PRE_END].reset_index(drop=True)
    cols = [c for c in df.columns if c not in ("open_time", "close")]
    c = df["close"].values.astype(float)
    ot = df["open_time"].values.astype(np.int64)
    fwdK = np.full(len(c), np.nan); fwdK[:-K] = c[K:] / c[:-K] - 1
    yK = (fwdK > 0).astype(float)
    valid = df[cols].notna().all(axis=1).values & ~np.isnan(fwdK)
    X = df[cols].values.astype(np.float64)
    bounds = [PRE_END - (N_WIN - i) * WIN_MS for i in range(N_WIN + 1)]

    results = {}
    for name, factory in CONFIGS.items():
        if only and name not in only:
            continue
        t0 = time.time()
        trades = {cov: [] for cov in COVS}
        win_hits = []
        for w in range(N_WIN):
            ws, we = bounds[w], bounds[w + 1]
            te = np.where(valid & (ot >= ws) & (ot < we))[0]
            tr = np.where(valid & (ot < ws - EMB * 300000))[0]
            cut = int(len(tr) * 0.9)
            fit_idx, cal_idx = tr[:cut], tr[cut:][EMB:]
            model = factory()
            model.fit(X[fit_idx], yK[fit_idx].astype(int))
            p_cal_raw = model.predict_proba(X[cal_idx])[:, 1]
            iso = IsotonicRegression(out_of_bounds="clip")
            iso.fit(p_cal_raw, yK[cal_idx].astype(int))
            p_cal = iso.transform(p_cal_raw)
            p_te = iso.transform(model.predict_proba(X[te])[:, 1])
            s_cal = np.abs(p_cal - 0.5); s_te = np.abs(p_te - 0.5)
            yte = yK[te].astype(int)
            whit = {}
            for cov in COVS:
                thr = np.quantile(s_cal, 1 - cov)
                sel = s_te >= thr
                if sel.sum():
                    correct = ((p_te[sel] > 0.5).astype(int) == yte[sel]).astype(int)
                    trades[cov].extend(correct.tolist())
                    whit[cov] = (float(correct.mean()), int(sel.sum()))
                else:
                    whit[cov] = (float("nan"), 0)
            move = (c[te[-1]] / c[te[0]] - 1) * 100
            win_hits.append((w, round(move, 1), whit))
        pooled = {cov: (round(float(np.mean(t)), 4), len(t)) if t else (None, 0)
                  for cov, t in trades.items()}
        elapsed = time.time() - t0
        results[name] = dict(pooled=pooled, windows=win_hits, sec=round(elapsed))
        wstr = " ".join(f"w{w}({mv:+.0f}%):{h[0.025][0]:.3f}/{h[0.025][1]}" for w, mv, h in win_hits)
        print(f"{name:18s} pooled c5={pooled[0.05][0]}/{pooled[0.05][1]} "
              f"c2.5={pooled[0.025][0]}/{pooled[0.025][1]} c1={pooled[0.01][0]}/{pooled[0.01][1]} "
              f"[{elapsed:.0f}s]\n    {wstr}", flush=True)
    with open(os.path.join(LAB, "sweep_a3_results.json"), "a") as f:
        f.write(json.dumps(results, default=str) + "\n")


if __name__ == "__main__":
    main()
