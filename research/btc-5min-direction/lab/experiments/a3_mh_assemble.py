#!/usr/bin/env python3
"""Assemble ensemble variants from the member cache; harness-faithful eval.

For each (member-subset, combiner): combine raw member probs -> ensemble prob,
then isotonic-calibrate on calib slice (as the harness does), score = |p-0.5|,
thresholds = calib quantiles, evaluate test. Pooled over 6 pre-windows.
"""
import os, pickle, json
import numpy as np
from sklearn.isotonic import IsotonicRegression

LAB = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
COVS = [0.05, 0.025, 0.01]

def ecdf_map(ref, x):
    ref = np.sort(ref)
    return np.searchsorted(ref, x, side="right") / (len(ref) + 1.0)

def combine(preds, y_inner, names, how):
    """returns (p_cal, p_te) combined from raw member probs."""
    P_in = np.column_stack([preds[n]["inner"] for n in names])
    P_ca = np.column_stack([preds[n]["cal"] for n in names])
    P_te = np.column_stack([preds[n]["te"] for n in names])
    if how == "mean":
        return P_ca.mean(1), P_te.mean(1)
    if how == "conf":
        def cw(P):
            w = np.abs(P - 0.5) + 1e-6
            return (w * P).sum(1) / w.sum(1)
        return cw(P_ca), cw(P_te)
    if how == "logit":
        def lp(P):
            Pc = np.clip(P, 1e-4, 1 - 1e-4)
            z = np.log(Pc / (1 - Pc)).mean(1)
            return 1 / (1 + np.exp(-z))
        return lp(P_ca), lp(P_te)
    if how == "ecdf":
        ca = np.column_stack([ecdf_map(P_in[:, j], P_ca[:, j]) for j in range(P_in.shape[1])])
        te = np.column_stack([ecdf_map(P_in[:, j], P_te[:, j]) for j in range(P_in.shape[1])])
        return ca.mean(1), te.mean(1)
    if how == "inniso":
        cas, tes = [], []
        for j in range(P_in.shape[1]):
            iso = IsotonicRegression(out_of_bounds="clip", y_min=0.02, y_max=0.98)
            iso.fit(P_in[:, j], y_inner)
            cas.append(iso.transform(P_ca[:, j])); tes.append(iso.transform(P_te[:, j]))
        return np.column_stack(cas).mean(1), np.column_stack(tes).mean(1)
    raise ValueError(how)

def evaluate(cache, names, how):
    res = {cov: [0, 0] for cov in COVS}   # cov -> [hits, n]
    perwin = {cov: [] for cov in COVS}
    for w in cache["windows"]:
        p_ca_raw, p_te_raw = combine(w["preds"], w["y_inner"], names, how)
        iso = IsotonicRegression(out_of_bounds="clip")
        iso.fit(p_ca_raw, w["y_cal"])
        p_ca, p_te = iso.transform(p_ca_raw), iso.transform(p_te_raw)
        s_ca, s_te = np.abs(p_ca - 0.5), np.abs(p_te - 0.5)
        for cov in COVS:
            thr = np.quantile(s_ca, 1 - cov)
            sel = s_te >= thr
            n = int(sel.sum())
            if n:
                cor = ((p_te[sel] > 0.5).astype(int) == w["y_te"][sel]).astype(int)
                res[cov][0] += int(cor.sum()); res[cov][1] += n
                perwin[cov].append((w["win"], round(float(cor.mean()), 4), n))
            else:
                perwin[cov].append((w["win"], None, 0))
    return res, perwin

def main():
    with open(os.path.join(LAB, "experiments", "a3_mh_member_cache.pkl"), "rb") as f:
        cache = pickle.load(f)
    subsets = {
        "full_alone":  ["full"],
        "deep_alone":  ["deep"],
        "fast_alone":  ["fast"],
        "slow_alone":  ["slow"],
        "logit_alone": ["logit"],
        "shallow_alone": ["shallow"],
        "S1_speed":    ["fast", "full", "slow"],
        "S2_capacity": ["shallow", "deep", "logit"],
        "S3_all6":     ["fast", "full", "slow", "shallow", "deep", "logit"],
        "S4_speed+deep": ["fast", "full", "slow", "deep"],
        "S5_full+deep+logit": ["full", "deep", "logit"],
        "S6_speed+logit": ["fast", "full", "slow", "logit"],
    }
    hows = ["mean", "conf", "logit", "ecdf", "inniso"]
    out = {}
    for sname, names in subsets.items():
        use_hows = ["mean"] if len(names) == 1 else hows
        for how in use_hows:
            res, perwin = evaluate(cache, names, how)
            key = f"{sname}|{how}"
            out[key] = {str(cov): {"hit": round(res[cov][0] / max(res[cov][1], 1), 4),
                                   "n": res[cov][1],
                                   "wins_above": sum(1 for _, h, n in perwin[cov] if n >= 10 and h is not None and h > 0.5),
                                   "perwin": perwin[cov]} for cov in COVS}
            r25, r1 = out[key]["0.025"], out[key]["0.01"]
            print(f"{key:28s} cov2.5%: {r25['hit']:.4f}/{r25['n']:5d} w>{r25['wins_above']}/6 | "
                  f"cov1%: {r1['hit']:.4f}/{r1['n']:5d} w>{r1['wins_above']}/6", flush=True)
    with open(os.path.join(LAB, "experiments", "a3_mh_assemble_results.json"), "w") as f:
        json.dump(out, f, indent=1)

if __name__ == "__main__":
    main()
