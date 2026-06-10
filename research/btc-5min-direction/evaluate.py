"""
Honest out-of-sample evaluation of the pooled walk-forward predictions.
Answers three questions:
  1. How accurate / well-calibrated is the model?
  2. Does accuracy improve when we only act on confident signals?
  3. THE BANKABILITY TEST: does any edge survive realistic trading costs?
"""
import numpy as np
import pandas as pd

d = pd.read_pickle("oos_predictions.pkl").reset_index(drop=True)
d["p_ens"] = 0.5 * (d["p_logit"] + d["p_gbm"])   # simple ensemble
y = d["y"].values
fwd = d["fwd_ret"].values                         # next-bar close-to-close return
models = ["logit", "gbm", "ens"]

print("=== HEADLINE OUT-OF-SAMPLE METRICS (n=%d, ~%.0f days) ===" % (len(d), len(d) * 5 / 1440))
from sklearn.metrics import roc_auc_score
for m in models:
    p = d[f"p_{m}"].values
    acc = ((p > 0.5).astype(int) == y).mean()
    print(f"  {m:5s}  acc={acc:.4f}  auc={roc_auc_score(y,p):.4f}  edge_vs_coinflip=+{acc-0.5:.4f}")

best = "gbm"
p = d[f"p_{best}"].values
print(f"\n--- using best single model: {best} ---")

# ---------- 1. Calibration ----------
print("\n=== CALIBRATION (are predicted probabilities truthful?) ===")
bins = np.linspace(0.45, 0.55, 6)
d["_p"] = p
cats = pd.cut(d["_p"], bins=[0, .48, .49, .495, .505, .51, .52, 1])
cal = d.groupby(cats, observed=True).agg(n=("y", "size"), predicted=("_p", "mean"), actual=("y", "mean"))
print(cal.to_string())

# ---------- 2. Confidence gating ----------
print("\n=== ACCURACY vs CONFIDENCE (does conviction help?) ===")
conf = np.abs(p - 0.5)
for q in [0.0, 0.5, 0.8, 0.9, 0.95, 0.99]:
    thr = np.quantile(conf, q)
    sel = conf >= thr
    pred = (p[sel] > 0.5).astype(int)
    acc = (pred == y[sel]).mean()
    print(f"  act on top {100*(1-q):4.0f}% most-confident  n={sel.sum():6d}  acc={acc:.4f}")

# ---------- 3. Cost-aware strategy ----------
print("\n=== BANKABILITY: net return after costs ===")
print("Rule: if p>0.5 go long next bar, else short; capture next-bar return minus round-trip cost.")
print("Gross edge per trade (no cost), all bars:")
sign = np.where(p > 0.5, 1.0, -1.0)
gross = sign * fwd
print(f"   mean gross = {gross.mean()*1e4:+.3f} bps/trade over {len(gross)} bars")

def strat(conf_q, cost_bps):
    thr = np.quantile(conf, conf_q)
    sel = conf >= thr
    s = np.where(p[sel] > 0.5, 1.0, -1.0)
    pnl = s * fwd[sel] - cost_bps / 1e4
    n = sel.sum()
    total = pnl.sum()
    return n, pnl.mean() * 1e4, total * 100  # n, net bps/trade, total % return

print("\n  net bps PER TRADE  (negative = loses money):")
header = "  conf_gate |" + "".join(f"  cost={c:>2}bps" for c in [0, 2, 6, 10, 14])
print(header)
for q, lbl in [(0.0, "all bars"), (0.9, "top 10%"), (0.95, "top 5%"), (0.99, "top 1%")]:
    cells = []
    for c in [0, 2, 6, 10, 14]:
        _, netbps, _ = strat(q, c)
        cells.append(f"  {netbps:+7.2f}")
    print(f"  {lbl:9s} |" + "".join(cells))

print("\n  TOTAL % return over the whole out-of-sample period:")
print(header)
for q, lbl in [(0.0, "all bars"), (0.9, "top 10%"), (0.95, "top 5%"), (0.99, "top 1%")]:
    cells = []
    for c in [0, 2, 6, 10, 14]:
        _, _, tot = strat(q, c)
        cells.append(f"  {tot:+7.1f}")
    print(f"  {lbl:9s} |" + "".join(cells))

# buy & hold benchmark over same window
bh = (d["close"].iloc[-1] / d["close"].iloc[0] - 1) * 100
print(f"\n  (context) buy & hold BTC over same window: {bh:+.1f}%")
