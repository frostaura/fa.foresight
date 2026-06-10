"""a3_event_gate_u2b — event-class gating, refined union (U1 + post-funding).

Same as a3_event_gate_u1 plus the post-funding leg:
  * post-funding:    minutes-since-settlement <= 60 (mins_to_funding >= 420)
                     AND |funding_last_z| > 1.5
(in pre-window per-class analysis the post-funding leg hit 0.538 within gated
trades; oi_spike_flat hit 0.496 and is deliberately excluded).

Union frequency ~14.0% overall, min 9.6% in any chaos window (measured pre-run).
Model identical to baseline_k3_lgbm. K=3. Pre-registered PRIMARY_COV = 0.05
before first harness run.
"""
import numpy as np
import lightgbm

NAME = "a3_event_gate_u2b"
K = 3
PRIMARY_COV = 0.05


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


def _event_active(d):
    cascade = (d["cascade_signed"].fillna(0) != 0) | (d["cascade_decel"].fillna(0) == 1)
    oi_flush = d["oi_flush_flag"].fillna(0) == 1
    prem = d["prem_z96"].abs() > 2
    prefund = d["prefund_extreme"].fillna(0) != 0
    postfund = (d["mins_to_funding"] >= 420) & (d["funding_last_z"].abs() > 1.5)
    cb_us = (d["xa_cb_prem_z7d"].abs() > 2) & (d["sess_us_cash"] == 1)
    return (cascade | oi_flush | prem | prefund | postfund | cb_us).values


def score(p_calibrated, df_rows):
    act = _event_active(df_rows)
    return np.where(act, np.abs(np.asarray(p_calibrated) - 0.5), -1.0)
