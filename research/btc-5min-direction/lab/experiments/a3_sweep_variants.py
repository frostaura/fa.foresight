"""Offline variant analysis from cached per-model predictions (a3_cache_w*.npy)."""
import os
import numpy as np
from sklearn.isotonic import IsotonicRegression

LAB = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
COVS = [0.05, 0.025, 0.01]
cache = []
for w in range(4):
    cw = np.load(os.path.join(LAB, "experiments", f"a3_cache_w{w}.npy"),
                 allow_pickle=True)[0]
    cache.append(cw)

# sanity: per-model standalone hit on test (all rows), and agreement rates
print("per-model sanity (test all-rows hit / mean p / frac NaN):")
for cw in cache:
    line = f"win {cw['w']}: "
    for nm in ["lgb", "xgb", "log"]:
        p = cw["P_te"][nm]; y = cw["yte"]
        line += f"{nm} hit={((p>0.5).astype(int)==y).mean():.4f} std={p.std():.3f} nan={np.isnan(p).sum()}  "
    pl, px, pg = cw["P_te"]["lgb"], cw["P_te"]["xgb"], cw["P_te"]["log"]
    a3 = (((pl>0.5)==(px>0.5)) & ((px>0.5)==(pg>0.5))).mean()
    at = ((pl>0.5)==(px>0.5)).mean()
    line += f"agree3={a3:.3f} agreeTrees={at:.3f}"
    print(line)

def percal(P_cal, P_te, ycal):
    """per-model isotonic calibration on calib, applied to both."""
    out_c, out_t = {}, {}
    for nm in P_cal:
        iso = IsotonicRegression(out_of_bounds="clip")
        iso.fit(P_cal[nm], ycal)
        out_c[nm] = iso.transform(P_cal[nm])
        out_t[nm] = iso.transform(P_te[nm])
    return out_c, out_t

def shape(P, design, Pc=None):
    pl, px, pg = P["lgb"], P["xgb"], P["log"]
    pm = (pl + px + pg) / 3.0
    sl, sx, sg = pl > 0.5, px > 0.5, pg > 0.5
    all3 = (sl == sx) & (sx == sg)
    trees = sl == sx
    if design == "lgb_only":      return pl
    if design == "E_lgbgate3":    return 0.5 + (pl - 0.5) * all3
    if design == "F_lgbgateT":    return 0.5 + (pl - 0.5) * trees
    if design == "H_lgbgateX":    return 0.5 + (pl - 0.5) * trees  # alias
    if design == "I_calmean":     # mean of per-model calibrated probs
        cl, cx, cg = Pc["lgb"], Pc["xgb"], Pc["log"]
        return (cl + cx + cg) / 3.0
    if design == "J_calmean_gate3":
        cl, cx, cg = Pc["lgb"], Pc["xgb"], Pc["log"]
        cm = (cl + cx + cg) / 3.0
        a3c = ((cl > 0.5) == (cx > 0.5)) & ((cx > 0.5) == (cg > 0.5))
        return 0.5 + (cm - 0.5) * a3c
    if design == "K_caltreesmean_gate":
        cl, cx = Pc["lgb"], Pc["xgb"]
        cm = (cl + cx) / 2.0
        return 0.5 + (cm - 0.5) * ((cl > 0.5) == (cx > 0.5))
    if design == "L_lgbgate3_cal":
        cl, cx, cg = Pc["lgb"], Pc["xgb"], Pc["log"]
        a3c = ((cl > 0.5) == (cx > 0.5)) & ((cx > 0.5) == (cg > 0.5))
        return 0.5 + (cl - 0.5) * a3c
    raise ValueError(design)

designs = ["lgb_only", "E_lgbgate3", "F_lgbgateT", "I_calmean",
           "J_calmean_gate3", "K_caltreesmean_gate", "L_lgbgate3_cal"]
res = {d: {cov: [] for cov in COVS} for d in designs}
for cw in cache:
    Pc_cal, Pc_te = percal(cw["P_cal"], cw["P_te"], cw["ycal"])
    for d in designs:
        p_cal_raw = shape(cw["P_cal"], d, Pc_cal)
        p_te_raw = shape(cw["P_te"], d, Pc_te)
        iso = IsotonicRegression(out_of_bounds="clip")
        iso.fit(p_cal_raw, cw["ycal"])
        p_cal = iso.transform(p_cal_raw); p_te = iso.transform(p_te_raw)
        s_cal = np.abs(p_cal - 0.5); s_te = np.abs(p_te - 0.5)
        for cov in COVS:
            thr = np.quantile(s_cal, 1 - cov)
            sel = s_te >= thr
            n = int(sel.sum())
            hit = float((((p_te[sel] > 0.5).astype(int)) == cw["yte"][sel]).mean()) if n else float("nan")
            res[d][cov].append((n, hit))

print(f"\n{'design':22s} " + " ".join(f"{'cov'+str(cov):>18s}" for cov in COVS))
for d in designs:
    line = f"{d:22s} "
    for cov in COVS:
        rows = res[d][cov]
        ntot = sum(n for n, _ in rows)
        hits = sum(n * h for n, h in rows if n) / max(ntot, 1)
        wa = sum(1 for n, h in rows if n >= 10 and h > 0.5)
        line += f"  {hits:.4f}/{ntot:5d}/{wa}w "
    print(line)
print("\nper-window detail @cov0.025:")
for d in designs:
    print(f"{d:22s}", [(n, None if n == 0 else round(h, 3)) for n, h in res[d][0.025]])
