"""Optimize the operating point for AVG cross-window hit-rate >=60%.
Walk the same 12 chaos windows, but with the stronger model (500 trees) and record
hit-rate at a grid of conviction gates. Also save per-window K=9 test preds so a
multi-horizon agreement pass can reuse them. Resumable: 3 windows/call. Arg: K (default 9)."""
import warnings; warnings.filterwarnings("ignore")
import os, sys, time, numpy as np, pandas as pd
from sklearn.isotonic import IsotonicRegression
import lightgbm as lgb

K = int(sys.argv[1]) if len(sys.argv) > 1 else 9
EMB, N_WIN, BURN = K + 60, 12, 0.35
COVS = [0.10, 0.05, 0.025, 0.01, 0.005]
feat = pd.read_pickle("btc_v5_features.pkl")
EXCL = {"y", "fwd_ret", "open_time", "close"}
cols = [c for c in feat.columns if c not in EXCL]
c = feat["close"].values
fwdK = np.full(len(c), np.nan); fwdK[:-K] = c[K:] / c[:-K] - 1
yK = (fwdK > 0).astype(float)
valid = feat[cols].notna().all(axis=1).values & ~np.isnan(fwdK)
idx = np.where(valid)[0]
X = feat[cols].values[idx]; y = yK[idx].astype(int)
n = len(idx); burn = int(n * BURN); wsz = (n - burn) // N_WIN

def gbm():
    return lgb.LGBMClassifier(n_estimators=500, learning_rate=0.02, num_leaves=63, max_depth=6,
        min_child_samples=150, subsample=0.8, subsample_freq=1, colsample_bytree=0.6,
        reg_lambda=8.0, reg_alpha=1.0, n_jobs=-1, verbose=-1)

RES = f"chaos_opt_K{K}.csv"
done = set(pd.read_csv(RES)["win"]) if os.path.exists(RES) else set()
t0 = time.time(); did = 0
for w in range(N_WIN):
    if w in done or did >= 3 or time.time() - t0 > 33:
        continue
    s = burn + w * wsz; e = (burn + (w + 1) * wsz) if w < N_WIN - 1 else n
    cut = s - EMB; fit_end = int(cut * 0.9)
    m = gbm(); m.fit(X[:fit_end], y[:fit_end])
    iso = IsotonicRegression(out_of_bounds="clip")
    iso.fit(m.predict_proba(X[fit_end + EMB:cut])[:, 1], y[fit_end + EMB:cut])
    confcal = np.abs(iso.transform(m.predict_proba(X[fit_end + EMB:cut])[:, 1]) - .5)
    p = iso.transform(m.predict_proba(X[s:e])[:, 1]); yt = y[s:e]; conf = np.abs(p - .5)
    np.savez(f"chaos_pred_K{K}_w{w}.npz", p=p, yt=yt)   # for agreement reuse
    rows = []
    for cq in COVS:
        thr = np.quantile(confcal, 1 - cq); sel = conf >= thr
        hit = ((p[sel] > .5).astype(int) == yt[sel]).mean() if sel.sum() >= 8 else np.nan
        rows.append(dict(win=w, cov=cq, n=int(sel.sum()), hit=round(hit, 4)))
    pd.DataFrame(rows).to_csv(RES, mode="a", header=not os.path.exists(RES), index=False)
    did += 1; print(f"win {w}: " + " ".join(f"{int(c*1000)/10}%={r['hit']}(n{r['n']})" for c, r in zip(COVS, rows)))
print(f"K={K}: processed {did}; total {len(done)+did}/{N_WIN}")
