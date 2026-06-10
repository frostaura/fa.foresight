"""
Walk-forward (out-of-sample) evaluation for BTC 5-min direction.

Design choices that keep the test honest:
  * Time-ordered. Never shuffle. Train is always strictly BEFORE test.
  * Expanding window with 8 sequential test blocks (TimeSeriesSplit).
  * EMBARGO gap of 60 bars (5 hours) between train end and test start, so
    no rolling-feature window in the test set overlaps any training label.
  * Scaler fit on TRAIN ONLY, then applied to test (no leakage of test stats).
  * Identical folds for every model + the naive baselines -> apples to apples.
"""
import warnings; warnings.filterwarnings("ignore")
import numpy as np
import pandas as pd
from sklearn.model_selection import TimeSeriesSplit
from sklearn.preprocessing import StandardScaler
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import roc_auc_score
import lightgbm as lgb
from features import feature_columns

EMBARGO = 60          # bars (5 hours) purged between train and each test block
N_SPLITS = 8


def load():
    feat = pd.read_pickle("btc_5m_features.pkl").dropna().reset_index(drop=True)
    cols = feature_columns(feat)
    X = feat[cols].values.astype(np.float64)
    y = feat["y"].values.astype(int)
    fwd = feat["fwd_ret"].values
    return feat, X, y, fwd, cols


def fit_predict(model_name, Xtr, ytr, Xte):
    if model_name == "logit":
        sc = StandardScaler().fit(Xtr)
        m = LogisticRegression(max_iter=2000, C=0.5)
        m.fit(sc.transform(Xtr), ytr)
        return m.predict_proba(sc.transform(Xte))[:, 1]
    if model_name == "gbm":
        m = lgb.LGBMClassifier(
            n_estimators=400, learning_rate=0.02, num_leaves=31,
            max_depth=5, min_child_samples=200, subsample=0.8,
            subsample_freq=1, colsample_bytree=0.7,
            reg_lambda=5.0, reg_alpha=1.0, n_jobs=-1, verbose=-1,
        )
        m.fit(Xtr, ytr)
        return m.predict_proba(Xte)[:, 1]
    raise ValueError(model_name)


def run():
    feat, X, y, fwd, cols = load()
    n = len(y)
    tscv = TimeSeriesSplit(n_splits=N_SPLITS, gap=EMBARGO)
    models = ["logit", "gbm"]

    # store OOS predictions aligned to original index
    oos = {m: np.full(n, np.nan) for m in models}
    oos_idx = np.full(n, False)

    rows = []
    for fold, (tr, te) in enumerate(tscv.split(X)):
        # apply embargo also at the *front* edge already handled by gap;
        # TimeSeriesSplit gap removes `gap` samples between train and test.
        ytr, yte = y[tr], y[te]
        base_up = yte.mean()
        rec = {"fold": fold, "train_n": len(tr), "test_n": len(te),
               "test_up_rate": base_up}
        for m in models:
            p = fit_predict(m, X[tr], ytr, X[te])
            oos[m][te] = p
            pred = (p > 0.5).astype(int)
            rec[f"{m}_acc"] = (pred == yte).mean()
            # AUC needs both classes present
            rec[f"{m}_auc"] = roc_auc_score(yte, p) if len(np.unique(yte)) > 1 else np.nan
        oos_idx[te] = True
        rows.append(rec)

    folds = pd.DataFrame(rows)
    pd.set_option("display.width", 160)
    print("=== PER-FOLD (out-of-sample) ===")
    show = folds.copy()
    for c in show.columns:
        if c not in ("fold", "train_n", "test_n"):
            show[c] = show[c].map(lambda v: f"{v:.4f}")
    print(show.to_string(index=False))

    # Pool all OOS predictions (concatenation of the 8 disjoint test blocks)
    mask = oos_idx
    ypool = y[mask]
    print("\n=== POOLED OUT-OF-SAMPLE (all test blocks combined, n=%d) ===" % mask.sum())
    print("pooled UP rate (majority baseline): %.4f" % max(ypool.mean(), 1 - ypool.mean()))
    res = {}
    for m in models:
        p = oos[m][mask]
        pred = (p > 0.5).astype(int)
        acc = (pred == ypool).mean()
        auc = roc_auc_score(ypool, p)
        res[m] = (acc, auc)
        print(f"  {m:6s}  acc={acc:.4f}   auc={auc:.4f}")

    # persist OOS for downstream evaluation + audit
    outdf = feat.loc[mask, ["open_time", "close", "y", "fwd_ret"]].copy()
    for m in models:
        outdf[f"p_{m}"] = oos[m][mask]
    outdf.to_pickle("oos_predictions.pkl")
    folds.to_csv("walkforward_folds.csv", index=False)
    print("\nsaved oos_predictions.pkl + walkforward_folds.csv")
    return res


if __name__ == "__main__":
    run()
