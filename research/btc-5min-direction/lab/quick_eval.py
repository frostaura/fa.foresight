#!/usr/bin/env python3
"""Quick forward-split leakage evaluator (NOT the chaos harness — audit tool only).

Train LightGBM (300 trees) on the first 70% of valid rows, embargo 70 bars,
test on the last 30%. Label = sign of 9-bar forward return. Reports acc + AUC.

Modes:
  real     baseline run (expect AUC ~0.51-0.56; >0.60 = leakage alarm)
  shuffle  labels permuted on train+test -> must be ~0.50/0.50
  canary   adds temp feature = sign(fwd_ret) + noise -> must give acc > 0.95
  corr     pearson corr of every feature vs 9-bar fwd return; top 10 listed

Usage: python3 quick_eval.py [--mode real|shuffle|canary|corr] [--pkl PATH]
"""
import argparse
import os

import numpy as np
import pandas as pd
import lightgbm as lgb
from sklearn.metrics import roc_auc_score

LAB = os.path.dirname(os.path.abspath(__file__))
K = 9
EMBARGO = 70
SEED = 42


def load(pkl):
    df = pd.read_pickle(pkl)
    c = df["close"].values.astype(float)
    fwd = np.full(len(c), np.nan)
    fwd[:-K] = c[K:] / c[:-K] - 1
    y = (fwd > 0).astype(float)
    feats = [col for col in df.columns if col not in ("open_time", "close")]
    return df, feats, fwd, y


def run_model(X, y, feats, label):
    valid = ~np.isnan(X).any(axis=1) & ~np.isnan(y)
    idx = np.where(valid)[0]
    cut = int(len(idx) * 0.70)
    tr, te = idx[:cut], idx[cut + EMBARGO:]
    model = lgb.LGBMClassifier(
        n_estimators=300, learning_rate=0.05, num_leaves=63,
        min_child_samples=100, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.8, random_state=SEED, n_jobs=10, verbose=-1)
    model.fit(X[tr], y[tr].astype(int))
    p = model.predict_proba(X[te])[:, 1]
    acc = float((((p > 0.5).astype(int)) == y[te].astype(int)).mean())
    auc = float(roc_auc_score(y[te].astype(int), p))
    print(f"[{label}] n_train={len(tr)} n_test={len(te)} acc={acc:.4f} auc={auc:.4f}")
    imp = sorted(zip(feats, model.feature_importances_), key=lambda t: -t[1])[:10]
    print(f"[{label}] top-10 importance: " +
          ", ".join(f"{n}={v}" for n, v in imp))
    return acc, auc


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mode", default="real",
                    choices=["real", "shuffle", "canary", "corr"])
    ap.add_argument("--pkl", default=os.path.join(LAB, "features_v1.pkl"))
    args = ap.parse_args()

    df, feats, fwd, y = load(args.pkl)

    if args.mode == "corr":
        rows = []
        for col in feats:
            v = df[col].values
            m = ~np.isnan(v) & ~np.isnan(fwd)
            if m.sum() < 1000:
                continue
            r = np.corrcoef(v[m], fwd[m])[0, 1]
            rows.append((col, r))
        rows.sort(key=lambda t: -abs(t[1]))
        print(f"max |corr| with {K}-bar fwd return: {abs(rows[0][1]):.5f}")
        print("top 10:")
        for col, r in rows[:10]:
            print(f"  {col:32s} {r:+.5f}")
        assert abs(rows[0][1]) < 0.10, "CORRELATION ALARM: |corr| >= 0.10"
        print("PASS: all |corr| < 0.10")
        return

    X = df[feats].values.astype(np.float64)
    if args.mode == "real":
        run_model(X, y, feats, "REAL")
    elif args.mode == "shuffle":
        rng = np.random.default_rng(SEED)
        y_sh = y.copy()
        m = ~np.isnan(y_sh)
        y_sh[m] = rng.permutation(y_sh[m])
        run_model(X, y_sh, feats, "SHUFFLE")
    elif args.mode == "canary":
        rng = np.random.default_rng(SEED)
        planted = np.sign(fwd) + rng.normal(0, 0.3, len(fwd))
        Xc = np.column_stack([X, planted])
        run_model(Xc, y, feats + ["__PLANTED_FUTURE__"], "CANARY")


if __name__ == "__main__":
    main()
