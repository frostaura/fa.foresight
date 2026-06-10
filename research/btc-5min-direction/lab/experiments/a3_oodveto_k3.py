"""H14 OOD kill-switch / crash veto — tuned on pre-window data.

Pre-window finding (6 pseudo-windows Mar-Jul 2025, incl. April crash leg):
broad OOD vetoes (DVOL velocity z, realized-vol percentile, range percentile)
FALSIFY the hypothesis — confident calls inside those regions hit 52-61%,
i.e. better than average, not coin flips. The only sub-50% pockets among
confident calls were extreme flow z-scores (|z|>5 on taker imbalance /
perp taker imbalance / 24h volume z / 1-bar dOI z; hit 0.459, n=37) and
extreme 1-bar vol-normalized return (|retn_1|>4; hit 0.481, n=27).

This module pre-registers the union of those two micro-vetoes only
(~0.9% of bars vetoed). Pre-window effect: pooled @2.5% 0.5480 -> 0.5498.
Expected harness effect: small; this run is honest confirmation, not a
breakthrough claim.

score: conf = |p-0.5| * penalty, penalty=0 when vetoed (threshold from
calib distribution of the same score, per harness).
"""
import numpy as np
import lightgbm

NAME = "a3_oodveto_k3"
K = 3
PRIMARY_COV = 0.025

FLOW_COLS = ["ti_z_48", "perp_ti_z_48", "vol_z_288", "doi_1b_z"]
FLOW_T = 5.0
RETN1_T = 4.0


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


def score(p_calibrated, df_rows):
    veto = np.zeros(len(df_rows), dtype=bool)
    for c in FLOW_COLS:
        veto |= np.abs(df_rows[c].values) > FLOW_T
    veto |= np.abs(df_rows["retn_1"].values) > RETN1_T
    pen = np.where(veto, 0.0, 1.0)
    return np.abs(np.asarray(p_calibrated) - 0.5) * pen
