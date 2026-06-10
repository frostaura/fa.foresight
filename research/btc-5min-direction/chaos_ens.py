"""Multi-horizon ENSEMBLE chaos test: per window fit K=6,9,12, average the calibrated
probabilities (sharper confidence ranking), evaluate vs the 45-min outcome at a coverage
grid. Target = AVG cross-window hit-rate >= 60%. Resumable: 2 windows/call."""
import warnings; warnings.filterwarnings("ignore")
import os, time, numpy as np, pandas as pd
from sklearn.isotonic import IsotonicRegression
import lightgbm as lgb

KS = [6, 9, 12]; EMB, N_WIN, BURN = 12 + 60, 12, 0.35
COVS = [0.10, 0.05, 0.025, 0.01]
feat = pd.read_pickle("btc_v5_features.pkl")
EXCL = {"y", "fwd_ret", "open_time", "close"}
cols = [c for c in feat.columns if c not in EXCL]
c = feat["close"].values
# common valid index: needs all horizons defined (drop last max(KS) bars)
fwd = {K: np.r_[c[K:] / c[:-K] - 1, np.full(K, np.nan)] for K in KS}
base_valid = feat[cols].notna().all(axis=1).values
for K in KS: base_valid = base_valid & ~np.isnan(fwd[K])
idx = np.where(base_valid)[0]
X = feat[cols].values[idx]
Y = {K: (fwd[K][idx] > 0).astype(int) for K in KS}
n = len(idx); burn = int(n * BURN); wsz = (n - burn) // N_WIN

def gbm():
    return lgb.LGBMClassifier(n_estimators=400, learning_rate=0.03, num_leaves=63, max_depth=6,
        min_child_samples=150, subsample=0.8, subsample_freq=1, colsample_bytree=0.6,
        reg_lambda=8.0, reg_alpha=1.0, n_jobs=-1, verbose=-1)

RES = "chaos_ens.csv"
done = set(pd.read_csv(RES)["win"]) if os.path.exists(RES) else set()
t0 = time.time(); did = 0
for w in range(N_WIN):
    if w in done or did >= 2 or time.time() - t0 > 32:
        continue
    s = burn + w * wsz; e = (burn + (w + 1) * wsz) if w < N_WIN - 1 else n
    cut = s - EMB; fit_end = int(cut * 0.9); cal = slice(fit_end + EMB, cut)
    pe_cal = np.zeros(cal.stop - cal.start); pe_te = np.zeros(e - s)
    for K in KS:
        m = gbm(); m.fit(X[:fit_end], Y[K][:fit_end])
        iso = IsotonicRegression(out_of_bounds="clip"); iso.fit(m.predict_proba(X[cal])[:, 1], Y[K][cal])
        pe_cal += iso.transform(m.predict_proba(X[cal])[:, 1]) / len(KS)
        pe_te += iso.transform(m.predict_proba(X[s:e])[:, 1]) / len(KS)
    yt = Y[9][s:e]; conf = np.abs(pe_te - .5); confcal = np.abs(pe_cal - .5)
    rows = []
    for cq in COVS:
        thr = np.quantile(confcal, 1 - cq); sel = conf >= thr
        hit = ((pe_te[sel] > .5).astype(int) == yt[sel]).mean() if sel.sum() >= 8 else np.nan
        rows.append(dict(win=w, cov=cq, n=int(sel.sum()), hit=round(hit, 4)))
    pd.DataFrame(rows).to_csv(RES, mode="a", header=not os.path.exists(RES), index=False)
    did += 1; print(f"win {w}: " + " ".join(f"{int(cv*1000)/10}%={r['hit']}(n{r['n']})" for cv, r in zip(COVS, rows)))
print(f"ENSEMBLE: processed {did}; total {len(done)+did}/{N_WIN}")
