#!/usr/bin/env python3
"""H14 OOD-veto internal sweep, stage 2: evaluate veto definitions on cache.

For each veto rule: harness-faithful selection (threshold = calib quantile of
score = |p-0.5| * penalty), pooled hit at cov 0.025 and 0.01 across 4
pseudo-windows. Plus diagnostic: hit of baseline confident trades inside the
veto region (want ~coin flip) vs outside.
"""
import os, pickle, itertools
import numpy as np
import pandas as pd

LAB = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
df = pd.read_pickle(os.path.join(LAB, "features_v1.pkl"))
with open(os.path.join(LAB, "sweep", "h14_cache.pkl"), "rb") as f:
    cache = pickle.load(f)

COL = {c: df[c].values for c in
       ["xa_dvol_vel_z", "xa_dvol_chg12h_z", "xa_dvol_chg24h_z",
        "xa_dvol_pct90d", "rv48_prank_30d", "range_prank_30d",
        "ti_z_48", "perp_ti_z_48", "vol_z_288", "vol_z_48", "doi_1b_z",
        "doi_4b_z", "trades_z_288", "ats_z_288", "body_z_48", "retn_1",
        "retn_12", "xa_vrp_z", "basis_z288", "vov_48"]}

def veto_mask(idx, rule):
    """True = VETO (penalty 0)."""
    kind = rule[0]
    if kind == "none":
        return np.zeros(len(idx), bool)
    if kind == "dvolvel":
        t = rule[1]; return np.abs(COL["xa_dvol_vel_z"][idx]) > t
    if kind == "rvp":
        q = rule[1]; return COL["rv48_prank_30d"][idx] > q
    if kind == "rangep":
        q = rule[1]; return COL["range_prank_30d"][idx] > q
    if kind == "flow6":
        t = rule[1]
        m = np.zeros(len(idx), bool)
        for c in ["ti_z_48", "perp_ti_z_48", "vol_z_288", "doi_1b_z"]:
            m |= np.abs(COL[c][idx]) > t
        return m
    if kind == "retn1":
        t = rule[1]; return np.abs(COL["retn_1"][idx]) > t
    if kind == "dvolpct":
        q = rule[1]; return COL["xa_dvol_pct90d"][idx] > q
    if kind == "combo":
        m = np.zeros(len(idx), bool)
        for sub in rule[1]:
            m |= veto_mask(idx, sub)
        return m
    raise ValueError(rule)

def evaluate(rule, cov):
    hits, ns, perwin = [], [], []
    for w in cache:
        pen_cal = (~veto_mask(w["cal_idx"], rule)).astype(float)
        pen_te = (~veto_mask(w["te"], rule)).astype(float)
        s_cal = np.abs(w["p_cal"] - 0.5) * pen_cal
        s_te = np.abs(w["p_te"] - 0.5) * pen_te
        thr = np.quantile(s_cal, 1 - cov)
        sel = s_te >= thr
        n = int(sel.sum())
        if n:
            cor = ((w["p_te"][sel] > 0.5).astype(int) == w["y_te"][sel])
            hits.append(cor.sum()); ns.append(n)
            perwin.append(cor.mean())
        else:
            perwin.append(np.nan)
    pooled = sum(hits) / sum(ns) if ns else np.nan
    return pooled, sum(ns), perwin

def diagnostic(rule, cov=0.025):
    """Among baseline-selected trades (no veto), hit inside vs outside veto."""
    hi, ni, ho, no_ = 0, 0, 0, 0
    for w in cache:
        s_cal = np.abs(w["p_cal"] - 0.5)
        s_te = np.abs(w["p_te"] - 0.5)
        thr = np.quantile(s_cal, 1 - cov)
        sel = s_te >= thr
        v = veto_mask(w["te"], rule)
        cor = ((w["p_te"] > 0.5).astype(int) == w["y_te"])
        hi += cor[sel & v].sum(); ni += int((sel & v).sum())
        ho += cor[sel & ~v].sum(); no_ += int((sel & ~v).sum())
    return (hi / ni if ni else np.nan, ni, ho / no_ if no_ else np.nan, no_)

rules = [("none",)]
for t in [2.0, 2.5, 3.0, 4.0, 5.0]:
    rules.append(("dvolvel", t))
for q in [0.97, 0.98, 0.99, 0.995]:
    rules.append(("rvp", q))
for q in [0.98, 0.99]:
    rules.append(("rangep", q))
for t in [4.0, 5.0, 6.0]:
    rules.append(("flow6", t))
for t in [4.0, 6.0]:
    rules.append(("retn1", t))
for q in [0.97, 0.99]:
    rules.append(("dvolpct", q))
rules.append(("combo", [("dvolvel", 3.0), ("rvp", 0.99), ("flow6", 6.0)]))
rules.append(("combo", [("dvolvel", 2.5), ("rvp", 0.98), ("flow6", 5.0)]))
rules.append(("combo", [("dvolvel", 3.0), ("rvp", 0.99)]))
rules.append(("combo", [("dvolvel", 4.0), ("rvp", 0.995), ("flow6", 6.0), ("retn1", 6.0)]))

base025, basen025, basepw = evaluate(("none",), 0.025)
print(f"BASE cov2.5: pooled={base025:.4f} n={basen025} perwin=" +
      " ".join(f"{h:.3f}" for h in basepw))
base01, basen01, basepw1 = evaluate(("none",), 0.01)
print(f"BASE cov1.0: pooled={base01:.4f} n={basen01} perwin=" +
      " ".join(f"{h:.3f}" for h in basepw1))
print()
print(f"{'rule':58s} {'vet%':>5s} {'inVeto':>14s} {'outVeto':>14s} "
      f"{'p@2.5':>7s} {'n':>5s} {'p@1.0':>7s} {'n':>5s}")
for rule in rules:
    # veto fraction over all test bars
    vfrac = np.mean(np.concatenate([veto_mask(w["te"], rule) for w in cache]))
    hi, ni, ho, no_ = diagnostic(rule)
    p25, n25, pw25 = evaluate(rule, 0.025)
    p10, n10, _ = evaluate(rule, 0.01)
    print(f"{str(rule):58s} {vfrac*100:5.1f} {hi:6.3f}/{ni:<6d} "
          f"{ho:6.3f}/{no_:<6d} {p25:7.4f} {n25:5d} {p10:7.4f} {n10:5d}  "
          + " ".join(f"{h:.3f}" for h in pw25))
