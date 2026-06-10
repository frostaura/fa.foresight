"""
Dispassionate independent re-derivation of the 5-min BTC directional edge.
Leakage-safe pipeline. Does NOT trust any prior report.
"""
import numpy as np
import pandas as pd
from sklearn.linear_model import LogisticRegression

RNG = np.random.default_rng(42)
CSV = "btc_5m.csv"
USE_ROWS = 80000  # most recent ~80k rows as permitted

# ----------------------------------------------------------------------
# Load
# ----------------------------------------------------------------------
df = pd.read_csv(CSV)
df = df.sort_values("open_time").reset_index(drop=True)
print(f"Total rows in file: {len(df)}")
df = df.tail(USE_ROWS).reset_index(drop=True)
print(f"Using most recent {len(df)} rows")
# time span
span_min = (df["open_time"].iloc[-1] - df["open_time"].iloc[0]) / 1000 / 60
print(f"Span: {span_min/60/24:.1f} days ({span_min/60/24/30.4:.1f} months)")

o = df["open"].to_numpy(float)
h = df["high"].to_numpy(float)
l = df["low"].to_numpy(float)
c = df["close"].to_numpy(float)
vol = df["volume"].to_numpy(float)
qv = df["quote_vol"].to_numpy(float)
trades = df["trades"].to_numpy(float)
taker_base = df["taker_base"].to_numpy(float)
open_time = df["open_time"].to_numpy(np.int64)

n = len(df)

# ----------------------------------------------------------------------
# TARGET: y[t] = 1 if close[t+1] > open[t+1] else 0  (NEXT candle's own body)
# Predict at time t using ONLY candles <= t.
# ----------------------------------------------------------------------
next_body_up = (c[1:] > o[1:]).astype(float)  # length n-1, indexed by t (uses t+1)
# y[t] valid for t in [0, n-2]

# ----------------------------------------------------------------------
# FEATURES — all backward-looking, computed at time t from candles <= t
# ----------------------------------------------------------------------
logc = np.log(c)
ret1 = np.zeros(n); ret1[1:] = logc[1:] - logc[:-1]  # ret1[t] = log(c[t]/c[t-1]) -> uses <= t

def roll_sum_ret(k):
    # sum of last k one-bar returns ending at t = log(c[t]/c[t-k])
    out = np.full(n, np.nan)
    out[k:] = logc[k:] - logc[:-k]
    return out

feat = {}
feat["ret1"] = ret1.copy()
feat["ret3"] = roll_sum_ret(3)
feat["ret5"] = roll_sum_ret(5)
feat["ret10"] = roll_sum_ret(10)
feat["ret20"] = roll_sum_ret(20)

# realized vol: std of ret1 over trailing windows (ending at t, uses <= t)
def roll_std(x, k):
    s = pd.Series(x)
    return s.rolling(k).std().to_numpy()
feat["rv10"] = roll_std(ret1, 10)
feat["rv20"] = roll_std(ret1, 20)
feat["rv50"] = roll_std(ret1, 50)

# ATR% : true range over trailing 14, normalized by close
prev_c = np.concatenate([[c[0]], c[:-1]])
tr = np.maximum(h - l, np.maximum(np.abs(h - prev_c), np.abs(l - prev_c)))
atr14 = pd.Series(tr).rolling(14).mean().to_numpy()
feat["atrpct"] = atr14 / c

# RSI(14) computed from ret1 (causal)
def rsi(close, k=14):
    delta = np.zeros(len(close)); delta[1:] = close[1:] - close[:-1]
    up = np.where(delta > 0, delta, 0.0)
    dn = np.where(delta < 0, -delta, 0.0)
    # Wilder smoothing via ewm alpha=1/k, causal
    ru = pd.Series(up).ewm(alpha=1/k, adjust=False).mean().to_numpy()
    rd = pd.Series(dn).ewm(alpha=1/k, adjust=False).mean().to_numpy()
    rs = ru / (rd + 1e-12)
    out = 100 - 100/(1+rs)
    out[:k] = np.nan
    return out / 100.0  # scale 0..1
feat["rsi14"] = rsi(c, 14)

# EMA spread: (ema_fast - ema_slow)/close, causal
def ema(x, span):
    return pd.Series(x).ewm(span=span, adjust=False).mean().to_numpy()
feat["ema_spread"] = (ema(c, 12) - ema(c, 26)) / c
feat["ema_spread_long"] = (ema(c, 26) - ema(c, 100)) / c

# range/body geometry of the CURRENT candle t (fully observed at t close)
rng_hl = (h - l)
body = (c - o)
upper_wick = h - np.maximum(o, c)
lower_wick = np.minimum(o, c) - l
feat["body_frac"] = body / (rng_hl + 1e-12)
feat["upper_wick_frac"] = upper_wick / (rng_hl + 1e-12)
feat["lower_wick_frac"] = lower_wick / (rng_hl + 1e-12)
feat["body_sign"] = np.sign(body)

# signed taker-flow imbalance: taker_base is taker BUY base vol; sell = vol - taker_base
taker_sell = vol - taker_base
flow_imb = (taker_base - taker_sell) / (vol + 1e-12)  # in [-1,1]
feat["flow_imb"] = flow_imb
# trailing mean of flow imbalance
feat["flow_imb5"] = pd.Series(flow_imb).rolling(5).mean().to_numpy()
feat["flow_imb20"] = pd.Series(flow_imb).rolling(20).mean().to_numpy()

# volume z-score over trailing 50 (causal: use shift so stats end at t)
logvol = np.log(vol + 1.0)
vmean = pd.Series(logvol).rolling(50).mean().to_numpy()
vstd = pd.Series(logvol).rolling(50).std().to_numpy()
feat["vol_z"] = (logvol - vmean) / (vstd + 1e-12)

# avg trade size z
avg_trade = qv / (trades + 1e-9)
lats = np.log(avg_trade + 1.0)
feat["avgtrade_z"] = (lats - pd.Series(lats).rolling(50).mean().to_numpy()) / (pd.Series(lats).rolling(50).std().to_numpy() + 1e-12)

# time-of-day sin/cos (UTC). open_time ms.
sec_of_day = (open_time // 1000) % 86400
ang = 2*np.pi*sec_of_day/86400.0
feat["tod_sin"] = np.sin(ang)
feat["tod_cos"] = np.cos(ang)

feat_names = list(feat.keys())
X_full = np.column_stack([feat[k] for k in feat_names])  # shape (n, F)

# ----------------------------------------------------------------------
# Align: drop rows with NaN features, and restrict t to [0, n-2] (need t+1)
# ----------------------------------------------------------------------
valid_t = np.arange(n-1)  # t can be 0..n-2
Xt = X_full[valid_t]
yt = next_body_up  # length n-1
ot = open_time[valid_t]

mask = ~np.isnan(Xt).any(axis=1)
Xt = Xt[mask]; yt = yt[mask]; ot = ot[mask]
print(f"Rows after feature warmup/NaN drop: {len(Xt)}")
print(f"Base rate (y=1 / next-body-up): {yt.mean():.4f}")

# ----------------------------------------------------------------------
# Chronological split 70/30 with embargo >= 5 candles
# ----------------------------------------------------------------------
N = len(Xt)
split = int(N*0.70)
EMBARGO = 5
train_idx = np.arange(0, split)
test_idx = np.arange(split + EMBARGO, N)
Xtr, ytr = Xt[train_idx], yt[train_idx]
Xte, yte = Xt[test_idx], yt[test_idx]
ote = ot[test_idx]
print(f"Train: {len(Xtr)}  Test: {len(Xte)}  (embargo {EMBARGO})")

# Winsorize using TRAIN percentiles (clip extremes) then standardize using TRAIN stats only
lo = np.percentile(Xtr, 0.5, axis=0)
hi = np.percentile(Xtr, 99.5, axis=0)
Xtr_c = np.clip(Xtr, lo, hi)
Xte_c = np.clip(Xte, lo, hi)
mu = Xtr_c.mean(axis=0); sd = Xtr_c.std(axis=0) + 1e-12
Xtr_s = (Xtr_c - mu)/sd
Xte_s = (Xte_c - mu)/sd
# safety: replace any residual non-finite
Xtr_s = np.nan_to_num(Xtr_s, nan=0.0, posinf=0.0, neginf=0.0)
Xte_s = np.nan_to_num(Xte_s, nan=0.0, posinf=0.0, neginf=0.0)

# ----------------------------------------------------------------------
# Fit L2 logistic regression
# ----------------------------------------------------------------------
def fit_predict(Xtr_s, ytr, Xte_s):
    clf = LogisticRegression(C=1.0, penalty="l2", max_iter=2000, solver="lbfgs")
    clf.fit(Xtr_s, ytr)
    p = clf.predict_proba(Xte_s)[:,1]
    return p, clf

pUp, clf = fit_predict(Xtr_s, ytr, Xte_s)

# ----------------------------------------------------------------------
# 1. oosAccuracyAllCandle
# ----------------------------------------------------------------------
pred = (pUp >= 0.5).astype(float)
acc_all = (pred == yte).mean()
print(f"\n=== oosAccuracyAllCandle: {acc_all:.4f} ===")
print(f"Test base rate y=1: {yte.mean():.4f}  (majority-class acc = {max(yte.mean(),1-yte.mean()):.4f})")

# ----------------------------------------------------------------------
# 2. labelShuffleNull — shuffle TRAIN labels, refit, eval on real test labels
# ----------------------------------------------------------------------
null_accs = []
for s in range(5):
    rng = np.random.default_rng(100+s)
    ytr_sh = ytr.copy(); rng.shuffle(ytr_sh)
    pN, _ = fit_predict(Xtr_s, ytr_sh, Xte_s)
    predN = (pN >= 0.5).astype(float)
    null_accs.append((predN == yte).mean())
null_acc = float(np.mean(null_accs))
print(f"\n=== labelShuffleNull (mean of 5, train labels shuffled): {null_acc:.4f} ===")
print(f"   individual: {[f'{a:.4f}' for a in null_accs]}")

# Also a stricter null: shuffle the test target alignment too (pure noise check)
rng = np.random.default_rng(7)
yte_sh = yte.copy(); rng.shuffle(yte_sh)
null_acc2 = (pred == yte_sh).mean()
print(f"   (sanity) real preds vs shuffled TEST labels: {null_acc2:.4f}")

# ----------------------------------------------------------------------
# 3. byConfidence
# ----------------------------------------------------------------------
winProb = np.maximum(pUp, 1-pUp)          # chosen-side confidence
chosen = (pUp >= 0.5).astype(float)        # 1=bet up, 0=bet down
win = (chosen == yte).astype(float)        # did chosen side win

def ev(rw, fee):
    return rw*((1-fee)/fee) - (1-rw)

thresholds = [0.52, 0.55, 0.58, 0.60, 0.62]
print("\n=== byConfidence ===")
print(f"{'thr':>5} {'cover':>7} {'nBets':>7} {'realWin':>8} {'ev@0.55':>9} {'ev@0.52':>9}")
byconf = []
for thr in thresholds:
    m = winProb >= thr
    cov = m.mean()
    nb = int(m.sum())
    rw = win[m].mean() if nb>0 else float('nan')
    e55 = ev(rw, 0.55) if nb>0 else float('nan')
    e52 = ev(rw, 0.52) if nb>0 else float('nan')
    byconf.append((thr, cov, nb, rw, e55, e52))
    print(f"{thr:>5.2f} {cov:>7.4f} {nb:>7d} {rw:>8.4f} {e55:>9.4f} {e52:>9.4f}")

# Choose best threshold by ev@0.55 among those with reasonable coverage (>=1% bets)
candidates = [b for b in byconf if b[2] >= max(50, int(0.005*len(yte)))]
if candidates:
    best = max(candidates, key=lambda b: b[4])
else:
    best = max(byconf, key=lambda b: b[4])
best_thr = best[0]
print(f"\nBest threshold (by ev@0.55, coverage-gated): {best_thr}  ev@0.55={best[4]:.4f} nBets={best[2]}")

# ----------------------------------------------------------------------
# 4. crossRegime — split TEST into 10 consecutive equal windows
# ----------------------------------------------------------------------
NW = 10
print(f"\n=== crossRegime at thr={best_thr} ({NW} windows) ===")
print(f"{'window':>8} {'nBets':>7} {'realWin':>8} {'ev@0.55':>9}")
bounds = np.linspace(0, len(yte), NW+1).astype(int)
cross = []
for w in range(NW):
    a,b = bounds[w], bounds[w+1]
    wp = winProb[a:b]; wn = win[a:b]
    m = wp >= best_thr
    nb = int(m.sum())
    rw = wn[m].mean() if nb>0 else float('nan')
    e55 = ev(rw,0.55) if nb>0 else float('nan')
    cross.append((w, nb, rw, e55))
    label = f"W{w+1}"
    print(f"{label:>8} {nb:>7d} {rw:>8.4f} {e55:>9.4f}")

# Robustness summary
valid_cross = [x for x in cross if x[1]>0]
pos_windows = sum(1 for x in valid_cross if x[3] > 0)
print(f"\nWindows with positive ev@0.55: {pos_windows}/{len(valid_cross)}")
rws = [x[2] for x in valid_cross]
print(f"Per-window realizedWin: min={np.nanmin(rws):.4f} median={np.nanmedian(rws):.4f} max={np.nanmax(rws):.4f}")
print(f"Breakeven win-rate @0.55 fee: {0.55:.4f}  @0.52 fee: {0.52:.4f}")

# Pooled win-rate at best thr for reference
mb = winProb >= best_thr
print(f"Pooled realizedWin @ best thr: {win[mb].mean():.4f} over {int(mb.sum())} bets")

# ----------------------------------------------------------------------
# Extra: feature importance (coef magnitude) for caveats
# ----------------------------------------------------------------------
coefs = sorted(zip(feat_names, clf.coef_[0]), key=lambda kv: -abs(kv[1]))
print("\nTop coefficients (standardized):")
for nm, cf in coefs[:8]:
    print(f"  {nm:>16} {cf:+.4f}")

# ----------------------------------------------------------------------
# Cross-check: GBT family (does a nonlinear model find more?)
# ----------------------------------------------------------------------
from sklearn.ensemble import HistGradientBoostingClassifier
gbt = HistGradientBoostingClassifier(max_depth=3, learning_rate=0.05,
                                     max_iter=300, l2_regularization=1.0,
                                     early_stopping=True, validation_fraction=0.15,
                                     random_state=0)
gbt.fit(Xtr_s, ytr)
pG = gbt.predict_proba(Xte_s)[:,1]
accG = ((pG>=0.5).astype(float)==yte).mean()
wpG = np.maximum(pG,1-pG); chG=(pG>=0.5).astype(float); winG=(chG==yte).astype(float)
print(f"\n[GBT cross-check] oosAcc={accG:.4f}")
for thr in [0.52,0.55,0.58]:
    m=wpG>=thr; nb=int(m.sum()); rw=winG[m].mean() if nb else float('nan')
    print(f"  GBT thr={thr} cover={m.mean():.4f} nBets={nb} realWin={rw:.4f} ev@0.55={ev(rw,0.55):.4f}")

# ----------------------------------------------------------------------
# Robustness: vary the train/test split point to confirm edge isn't a fluke of one cut
# ----------------------------------------------------------------------
print("\n[Split-stability] confident-slice (thr=0.55) realWin across alternate 60/70/80 cuts:")
for frac in [0.60, 0.70, 0.80]:
    sp = int(N*frac)
    tri = np.arange(0, sp); tei = np.arange(sp+EMBARGO, N)
    Xa, ya = Xt[tri], yt[tri]; Xb, yb = Xt[tei], yt[tei]
    loa = np.percentile(Xa,0.5,axis=0); hia=np.percentile(Xa,99.5,axis=0)
    Xa_c=np.clip(Xa,loa,hia); Xb_c=np.clip(Xb,loa,hia)
    m_=Xa_c.mean(0); s_=Xa_c.std(0)+1e-12
    Xa_s=np.nan_to_num((Xa_c-m_)/s_); Xb_s=np.nan_to_num((Xb_c-m_)/s_)
    cl=LogisticRegression(C=1.0,max_iter=2000).fit(Xa_s,ya)
    pb=cl.predict_proba(Xb_s)[:,1]; wpb=np.maximum(pb,1-pb); wb=((pb>=0.5).astype(float)==yb).astype(float)
    mm=wpb>=0.55; nb=int(mm.sum()); rw=wb[mm].mean() if nb else float('nan')
    print(f"  cut={frac:.2f} testN={len(yb)} nBets={nb} realWin={rw:.4f} ev@0.55={ev(rw,0.55):+.4f}")
