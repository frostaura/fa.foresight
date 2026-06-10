"""CHAOS TEST: walk the model across 12 consecutive windows spanning every regime BTC
saw (bull -> top -> correction -> chop). Re-fit on all prior data before each window
(deployment-style), gate on confidence threshold set from TRAIN calibration only, and
record per-window hit-rate + profit. Resumable: run repeatedly until all 12 done.
K=9 (45-min direction, the 62% operating point)."""
import warnings; warnings.filterwarnings("ignore")
import os, time, numpy as np, pandas as pd
from sklearn.isotonic import IsotonicRegression
import lightgbm as lgb

K, EMB, N_WIN, BURN = 9, 9 + 60, 12, 0.35
COST_BPS, FEE_BIN = 10.0, 0.02
feat = pd.read_pickle("btc_v5_features.pkl")
EXCL = {"y", "fwd_ret", "open_time", "close"}
cols = [c for c in feat.columns if c not in EXCL]
c = feat["close"].values; ot = feat["open_time"].values
fwdK = np.full(len(c), np.nan); fwdK[:-K] = c[K:] / c[:-K] - 1
yK = (fwdK > 0).astype(float)
valid = feat[cols].notna().all(axis=1).values & ~np.isnan(fwdK)
idx = np.where(valid)[0]
X = feat[cols].values[idx]; y = yK[idx].astype(int); fw = fwdK[idx]; ts = ot[idx]; px = c[idx]
n = len(idx); burn = int(n * BURN); wsz = (n - burn) // N_WIN

def gbm():
    return lgb.LGBMClassifier(n_estimators=350, learning_rate=0.03, num_leaves=47, max_depth=6,
        min_child_samples=150, subsample=0.8, subsample_freq=1, colsample_bytree=0.6,
        reg_lambda=8.0, n_jobs=-1, verbose=-1)

RES, TRADES = "chaos_results.csv", "chaos_trades.csv"
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
    p = iso.transform(m.predict_proba(X[s:e])[:, 1]); yt = y[s:e]; fwt = fw[s:e]; conf = np.abs(p - .5)
    btc_move = (px[e - 1] / px[s] - 1) * 100
    rvol = np.std(np.diff(px[s:e]) / px[s:e - 1]) * 1e4
    regime = ("bull" if btc_move > 4 else "bear" if btc_move < -4 else "chop")

    def strat(cq):
        thr = np.quantile(confcal, 1 - cq); sel = conf >= thr
        if sel.sum() < 10: return (0, np.nan, np.nan, np.nan)
        hit = ((p[sel] > .5).astype(int) == yt[sel]).mean()
        side = np.where(p[sel] > .5, 1, -1)
        return (int(sel.sum()), hit, (side * fwt[sel]).mean() * 1e4 - COST_BPS, (2 * hit - 1) - FEE_BIN)
    n10, h10, spot10, bin10 = strat(0.10); n25, h25, _, bin25 = strat(0.025)
    row = dict(win=w, start=str(pd.to_datetime(ts[s], unit="ms").date()),
               end=str(pd.to_datetime(ts[e - 1], unit="ms").date()), regime=regime,
               btc_move_pct=round(btc_move, 1), rvol_bps=round(rvol, 1),
               n_top10=n10, hit_top10=round(h10, 4), spot_bps_top10=round(spot10, 2),
               binEV_top10=round(bin10, 4), n_top2_5=n25, hit_top2_5=round(h25, 4), binEV_top2_5=round(bin25, 4))
    pd.DataFrame([row]).to_csv(RES, mode="a", header=not os.path.exists(RES), index=False)
    # per-trade win/loss for the top-10% set (for chronological Kelly bankroll)
    thr = np.quantile(confcal, 0.90); sel = conf >= thr
    tr = pd.DataFrame({"win": w, "ts": ts[s:e][sel], "correct": ((p[sel] > .5).astype(int) == yt[sel]).astype(int)})
    tr.to_csv(TRADES, mode="a", header=not os.path.exists(TRADES), index=False)
    did += 1; print(f"win {w} {row['start']}..{row['end']} {regime} move={btc_move:+.1f}% hit10={h10:.3f} hit2.5={h25:.3f}")
print(f"processed {did} this run; total {len(done)+did}/{N_WIN}")
