"""a3_event_gate_u1 — event-class gating (H1/H3/H4/H7 forced-flow mechanisms).

Bet only when a forced-flow mechanism is plausibly active. Gate = union of:
  * cascade proxy:   cascade_signed != 0  OR  cascade_decel == 1
  * OI flush:        oi_flush_flag == 1
  * premium disloc:  |prem_z96| > 2
  * pre-funding:     prefund_extreme != 0  (<=60min to funding & |pred_funding_z|>1.5)
  * coinbase US:     |xa_cb_prem_z7d| > 2  AND  sess_us_cash == 1

Union frequency ~12.3% overall, min 8.7% in any chaos window (measured pre-run),
so PRIMARY_COV=0.05 cannot starve windows. Non-event bars get score -1 (hard
exclusion that cannot degenerate into select-all as long as calib event
frequency > coverage, which holds with wide margin).

Model identical to baseline_k3_lgbm (the gate is the only lever). K=3.
Pre-registered PRIMARY_COV = 0.05 before first harness run.
"""
import numpy as np
import lightgbm

NAME = "a3_event_gate_u1"
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
    cb_us = (d["xa_cb_prem_z7d"].abs() > 2) & (d["sess_us_cash"] == 1)
    return (cascade | oi_flush | prem | prefund | cb_us).values


def score(p_calibrated, df_rows):
    act = _event_active(df_rows)
    return np.where(act, np.abs(np.asarray(p_calibrated) - 0.5), -1.0)
