"""PRE-WINDOW sweep for the K=4 grand stack. NOT a harness run.

Replicates chaos_harness_v1 geometry on 6 pseudo-windows ending 2025-08-01
(all data < 1754006400000), K=4. Caches per-member calib/test predictions,
then evaluates stack designs offline through harness-style outer isotonic +
calib-quantile thresholds.

Members (recency hl120 weights unless noted):
  lgb_full_nw : plain LGBM fit on FULL fit slice, no weights (= a3_k4_lgbm control)
  lgb_full_w  : plain LGBM fit on FULL fit slice, recency weights
  mix_full_w  : vol5 mixture fit on FULL fit slice, recency weights
  lgb_in_w / mix_in_w / xgb_in_w / log_in_w : members fit on inner 90% of the
      fit slice with private isotonic on the purged last 10% (calmean path).

Designs:
  ctrl_k4        = lgb_full_nw                      (ledger winner replica)
  ctrl_k4_rec    = lgb_full_w
  mix_rec        = mix_full_w
  gate3_rec      = agreement-gated calmean of {lgb_in_w, xgb_in_w, log_in_w}
  grand          = agreement-gated calmean of {mix_in_w, xgb_in_w, log_in_w}
  grand_nogate   = plain calmean of {mix_in_w, xgb_in_w, log_in_w}
"""
import os, time, pickle, warnings
warnings.filterwarnings("ignore")
import numpy as np
import pandas as pd
import lightgbm as lgb
import xgboost as xgb
from sklearn.linear_model import LogisticRegression
from sklearn.preprocessing import StandardScaler
from sklearn.pipeline import Pipeline
from sklearn.isotonic import IsotonicRegression

LAB = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
K = 4
EMB = K + 60
PRE_END = 1754006400000
WLEN = 2188800000
N_WIN = 6
COVS = [0.05, 0.025, 0.01]
EPS = 1e-4
HL = 120.0
CACHE = os.path.join(LAB, "experiments", "_syn_pre_k4_cache.pkl")

BASE_PARAMS = dict(n_estimators=350, learning_rate=0.03, num_leaves=47, max_depth=6,
                   min_child_samples=150, subsample=0.8, subsample_freq=1,
                   colsample_bytree=0.6, reg_lambda=8.0, random_state=42,
                   n_jobs=3, verbose=-1)
BUCKET_PARAMS = dict(BASE_PARAMS, n_estimators=250)


def mk_lgb():
    return lgb.LGBMClassifier(**BASE_PARAMS)


def mk_xgb():
    return xgb.XGBClassifier(n_estimators=350, learning_rate=0.03, max_depth=6,
                             min_child_weight=150, subsample=0.8, colsample_bytree=0.6,
                             reg_lambda=8.0, tree_method="hist", random_state=42,
                             n_jobs=3, eval_metric="logloss")


def mk_log():
    return Pipeline([("sc", StandardScaler()),
                     ("lr", LogisticRegression(C=0.5, max_iter=2000))])


class RegimeMixture:
    def __init__(self, ci, nb=5, min_bucket=5000, blend=0.5):
        self.ci, self.nb, self.min_bucket, self.blend = ci, nb, min_bucket, blend

    def _bid(self, X):
        return np.digitize(X[:, self.ci], self.edges_)

    def fit(self, X, y, sample_weight=None):
        y = np.asarray(y)
        self.edges_ = np.quantile(X[:, self.ci], np.linspace(0, 1, self.nb + 1)[1:-1])
        bid = self._bid(X)
        self.global_ = lgb.LGBMClassifier(**BASE_PARAMS)
        self.global_.fit(X, y, sample_weight=sample_weight)
        self.models_ = {}
        for b in np.unique(bid):
            m = bid == b
            if m.sum() >= self.min_bucket and len(np.unique(y[m])) == 2:
                mod = lgb.LGBMClassifier(**BUCKET_PARAMS)
                sw = None if sample_weight is None else sample_weight[m]
                mod.fit(X[m], y[m], sample_weight=sw)
                self.models_[int(b)] = mod
        return self

    def predict_proba(self, X):
        p = self.global_.predict_proba(X)[:, 1]
        bid = self._bid(X)
        for b, mod in self.models_.items():
            m = bid == b
            if m.any():
                p[m] = self.blend * p[m] + (1 - self.blend) * mod.predict_proba(X[m])[:, 1]
        return np.column_stack([1 - p, p])


def recency_w(ot_fit):
    age_days = (ot_fit.max() - ot_fit) / 86400000.0
    return 0.5 ** (age_days / HL)


def fit_member(name, Xf, yf, sw):
    if name == "lgb":
        m = mk_lgb(); m.fit(Xf, yf, sample_weight=sw)
    elif name == "xgb":
        m = mk_xgb(); m.fit(Xf, yf, sample_weight=sw)
    elif name == "log":
        m = mk_log()
        if sw is not None:
            m.fit(Xf, yf, lr__sample_weight=sw)
        else:
            m.fit(Xf, yf)
    else:
        raise ValueError(name)
    return m


def main():
    df = pd.read_pickle(os.path.join(LAB, "features_v1.pkl"))
    df = df[df["open_time"] < PRE_END].reset_index(drop=True)
    feats = [c for c in df.columns if c not in ("open_time", "close", "y", "fwd_ret")]
    CI = feats.index("rv48_prank_30d")
    c = df["close"].values.astype(float)
    ot = df["open_time"].values.astype(np.int64)
    fwd = np.full(len(c), np.nan); fwd[:-K] = c[K:] / c[:-K] - 1
    y = (fwd > 0).astype(float)
    valid = df[feats].notna().all(axis=1).values & ~np.isnan(fwd)
    X = df[feats].values.astype(np.float64)

    bounds = [PRE_END - (N_WIN - i) * WLEN for i in range(N_WIN + 1)]
    cache = []
    for w in range(N_WIN):
        ws, we = bounds[w], bounds[w + 1]
        te = np.where(valid & (ot >= ws) & (ot < we))[0]
        tr = np.where(valid & (ot < ws - EMB * 300000))[0]
        if len(te) == 0 or len(tr) < 5000:
            print(f"win {w} skipped (n_tr={len(tr)})", flush=True); continue
        cut = int(len(tr) * 0.90)
        fit_idx, cal_idx = tr[:cut], tr[cut:][EMB:]   # harness outer split
        yfit = y[fit_idx].astype(int)
        sw_full = recency_w(ot[fit_idx])
        # inner split (wrapper path)
        n = len(fit_idx)
        icut = int(n * 0.90)
        core, inner = fit_idx[:icut], fit_idx[icut + EMB:]
        ycore = y[core].astype(int); yinner = y[inner].astype(int)
        sw_core = sw_full[:icut]
        t0 = time.time()
        P_cal, P_te = {}, {}

        def add(name, model):
            P_cal[name] = model.predict_proba(X[cal_idx])[:, 1]
            P_te[name] = model.predict_proba(X[te])[:, 1]
            print(f"win {w} {name} {time.time()-t0:.0f}s", flush=True)

        m = mk_lgb(); m.fit(X[fit_idx], yfit); add("lgb_full_nw", m)
        m = mk_lgb(); m.fit(X[fit_idx], yfit, sample_weight=sw_full); add("lgb_full_w", m)
        m = RegimeMixture(CI); m.fit(X[fit_idx], yfit, sample_weight=sw_full)
        print(f"  mix_full buckets={len(m.models_)}", flush=True); add("mix_full_w", m)
        # inner members with private isotonic
        for name in ("lgb", "xgb", "log"):
            mm = fit_member(name, X[core], ycore, sw_core)
            iso = IsotonicRegression(out_of_bounds="clip")
            iso.fit(mm.predict_proba(X[inner])[:, 1], yinner)
            P_cal[f"{name}_in_w"] = iso.transform(mm.predict_proba(X[cal_idx])[:, 1])
            P_cal[f"{name}_in_w_raw"] = mm.predict_proba(X[cal_idx])[:, 1]
            P_te[f"{name}_in_w"] = iso.transform(mm.predict_proba(X[te])[:, 1])
            P_te[f"{name}_in_w_raw"] = mm.predict_proba(X[te])[:, 1]
            print(f"win {w} {name}_in_w {time.time()-t0:.0f}s", flush=True)
        mm = RegimeMixture(CI); mm.fit(X[core], ycore, sample_weight=sw_core)
        iso = IsotonicRegression(out_of_bounds="clip")
        iso.fit(mm.predict_proba(X[inner])[:, 1], yinner)
        P_cal["mix_in_w"] = iso.transform(mm.predict_proba(X[cal_idx])[:, 1])
        P_cal["mix_in_w_raw"] = mm.predict_proba(X[cal_idx])[:, 1]
        P_te["mix_in_w"] = iso.transform(mm.predict_proba(X[te])[:, 1])
        P_te["mix_in_w_raw"] = mm.predict_proba(X[te])[:, 1]
        print(f"win {w} mix_in_w buckets={len(mm.models_)} {time.time()-t0:.0f}s", flush=True)

        move = (c[te[-1]] / c[te[0]] - 1) * 100
        cache.append(dict(w=w, ycal=y[cal_idx].astype(int), yte=y[te].astype(int),
                          P_cal=P_cal, P_te=P_te, move=move,
                          n_tr=len(tr), n_te=len(te)))
        with open(CACHE, "wb") as f:
            pickle.dump(cache, f)
    print("cached ->", CACHE, flush=True)

    def gate_mean(P, members, gated=True):
        cals = [P[m] for m in members]
        raws = [P[m + "_raw"] for m in members]
        cm = np.mean(cals, axis=0)
        rm = np.mean(raws, axis=0)
        sides = [cc > 0.5 for cc in cals]
        agree = np.ones(len(cm), dtype=bool)
        for s in sides[1:]:
            agree &= (s == sides[0])
        g = agree if gated else np.ones(len(cm), dtype=bool)
        p = 0.5 + ((cm - 0.5) * g + EPS * (rm - 0.5)) / (1.0 + EPS)
        return np.clip(p, 1e-6, 1 - 1e-6)

    def design(P, d):
        if d == "ctrl_k4":      return P["lgb_full_nw"]
        if d == "ctrl_k4_rec":  return P["lgb_full_w"]
        if d == "mix_rec":      return P["mix_full_w"]
        if d == "gate3_rec":    return gate_mean(P, ["lgb_in_w", "xgb_in_w", "log_in_w"])
        if d == "grand":        return gate_mean(P, ["mix_in_w", "xgb_in_w", "log_in_w"])
        if d == "grand_nogate": return gate_mean(P, ["mix_in_w", "xgb_in_w", "log_in_w"], gated=False)
        raise ValueError(d)

    designs = ["ctrl_k4", "ctrl_k4_rec", "mix_rec", "gate3_rec", "grand", "grand_nogate"]
    res = {d: {cov: [] for cov in COVS} for d in designs}
    for cw in cache:
        for d in designs:
            p_cal_raw = design(cw["P_cal"], d)
            p_te_raw = design(cw["P_te"], d)
            iso = IsotonicRegression(out_of_bounds="clip")
            iso.fit(p_cal_raw, cw["ycal"])
            p_cal = iso.transform(p_cal_raw); p_te = iso.transform(p_te_raw)
            s_cal = np.abs(p_cal - 0.5); s_te = np.abs(p_te - 0.5)
            for cov in COVS:
                thr = np.quantile(s_cal, 1 - cov)
                sel = s_te >= thr
                nn = int(sel.sum())
                hit = float((((p_te[sel] > 0.5).astype(int)) == cw["yte"][sel]).mean()) if nn else float("nan")
                res[d][cov].append((nn, hit))
    print(f"\n{'design':14s} " + " ".join(f"{'cov'+str(cov):>18s}" for cov in COVS))
    for d in designs:
        line = f"{d:14s} "
        for cov in COVS:
            rows = res[d][cov]
            ntot = sum(nn for nn, _ in rows)
            hits = sum(nn * h for nn, h in rows if nn) / max(ntot, 1)
            wa = sum(1 for nn, h in rows if nn >= 10 and h > 0.5)
            line += f"  {hits:.4f}/{ntot:5d}/{wa}w "
        print(line)
    print("\nper-window detail @cov0.025:")
    for d in designs:
        print(f"{d:14s}", [(nn, None if nn == 0 else round(h, 3)) for nn, h in res[d][0.025]])


if __name__ == "__main__":
    main()
