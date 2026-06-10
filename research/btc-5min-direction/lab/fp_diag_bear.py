"""fp_diag_bear — PRE-WINDOW ONLY diagnosis of bear-regime errors (no harness run).

Question: in falling-market stretches before 2025-08-01, is the K=4 model's error
asymmetric (bad longs vs bad shorts)? And is stated confidence inflated there?

Method: 6 walk-forward monthly folds (2025-02-01 .. 2025-08-01), each trained on all
valid pre-fold rows minus EMB embargo, mimicking the harness geometry (90/10 fit/calib,
isotonic, calib-quantile gate at 5%). 2-seed bagged LGBM (baseline a3 config).
Breakdowns: falling-state flag (causal: ret_288 / sma_z_288) x predicted side.
"""
import warnings; warnings.filterwarnings("ignore")
import numpy as np, pandas as pd, lightgbm, json, os

LAB = os.path.dirname(os.path.abspath(__file__))
K = 4; EMB = K + 60; COV = 0.05
df = pd.read_pickle(os.path.join(LAB, "features_v1.pkl"))
drop = {"open_time", "close", "y", "fwd_ret"}
cols = [c for c in df.columns if c not in drop]
c = df["close"].values.astype(float); ot = df["open_time"].values.astype(np.int64)
fwdK = np.full(len(c), np.nan); fwdK[:-K] = c[K:] / c[:-K] - 1
yK = (fwdK > 0).astype(float)
valid = df[cols].notna().all(axis=1).values & ~np.isnan(fwdK)
X = df[cols].values.astype(np.float64)

PRE_END = 1754006400000
folds = pd.date_range("2025-02-01", "2025-08-01", freq="MS").astype(np.int64) // 10**6
ret288 = df["ret_288"].values
smaz288 = df["sma_z_288"].values
dmi = df["dmi_14"].values

def mk(seed):
    return lightgbm.LGBMClassifier(n_estimators=350, learning_rate=0.03, num_leaves=47,
        max_depth=6, min_child_samples=150, subsample=0.8, subsample_freq=1,
        colsample_bytree=0.6, reg_lambda=8.0, random_state=seed, n_jobs=6, verbose=-1)

from sklearn.isotonic import IsotonicRegression
rows = []
for i in range(len(folds) - 1):
    ws, we = folds[i], folds[i + 1]
    te = np.where(valid & (ot >= ws) & (ot < we))[0]
    tr = np.where(valid & (ot < ws - EMB * 300000))[0]
    cut = int(len(tr) * 0.9)
    fit_idx, cal_idx = tr[:cut], tr[cut:][EMB:]
    ps_te, ps_cal = [], []
    for seed in (101, 102):
        m = mk(seed); m.fit(X[fit_idx], yK[fit_idx].astype(int))
        ps_cal.append(m.predict_proba(X[cal_idx])[:, 1])
        ps_te.append(m.predict_proba(X[te])[:, 1])
    p_cal_raw = np.mean(ps_cal, axis=0); p_te_raw = np.mean(ps_te, axis=0)
    iso = IsotonicRegression(out_of_bounds="clip"); iso.fit(p_cal_raw, yK[cal_idx].astype(int))
    p_cal = iso.transform(p_cal_raw); p_te = iso.transform(p_te_raw)
    s_cal = np.abs(p_cal - 0.5) + 1e-7 * np.abs(p_cal_raw - 0.5)
    s_te = np.abs(p_te - 0.5) + 1e-7 * np.abs(p_te_raw - 0.5)
    thr = np.quantile(s_cal, 1 - COV)
    for j in np.where(s_te >= thr)[0]:
        t = te[j]
        rows.append(dict(fold=i, t=int(t), p=float(p_te[j]), long=int(p_te[j] > 0.5),
                         y=int(yK[t]), ret288=float(ret288[t]), smaz=float(smaz288[t]),
                         dmi=float(dmi[t])))
    print(f"fold {i} {pd.to_datetime(ws,unit='ms').date()} n_sel={int((s_te>=thr).sum())} "
          f"hit={np.mean([(r['p']>0.5)==r['y'] for r in rows if r['fold']==i]):.3f}", flush=True)

R = pd.DataFrame(rows)
R["correct"] = ((R["p"] > 0.5).astype(int) == R["y"]).astype(int)
R["conf"] = np.maximum(R["p"], 1 - R["p"])
R["fall"] = (R["ret288"] < -0.01).astype(int)        # 24h ret < -1%
R["crash"] = (R["ret288"] < -0.025).astype(int)      # 24h ret < -2.5%

def block(g, label):
    print(f"\n== {label}: n={len(g)} hit={g.correct.mean():.4f} "
          f"stated={g.conf.mean():.4f} gap={g.conf.mean()-g.correct.mean():+.4f}")
    for side, gg in g.groupby("long"):
        nm = "LONG " if side else "SHORT"
        print(f"   {nm} n={len(gg):5d} hit={gg.correct.mean():.4f} stated={gg.conf.mean():.4f} "
              f"gap={gg.conf.mean()-gg.correct.mean():+.4f}")

block(R, "ALL selected (5% cov, pre-window folds)")
block(R[R.fall == 1], "FALLING (24h ret < -1%)")
block(R[R.crash == 1], "CRASH (24h ret < -2.5%)")
block(R[R.fall == 0], "NOT falling")
block(R[R.smaz < -1.0], "smaz288 < -1")
block(R[R.dmi < -0.2], "dmi_14 < -0.2")
# base rate context
for nm, m_ in [("fall", (ret288 < -0.01)), ("crash", (ret288 < -0.025))]:
    mm = m_ & valid & (ot >= folds[0]) & (ot < PRE_END)
    print(f"\nbase up-rate when {nm} (test months): {yK[mm].mean():.4f}  n_bars={mm.sum()}")
R.to_pickle(os.path.join(LAB, "fp_diag_bear_trades.pkl"))
print("\nsaved fp_diag_bear_trades.pkl")
