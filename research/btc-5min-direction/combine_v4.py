"""
Combine 15/30/45-min models (K=3,6,9): agreement + confidence gating.
Then the RETURN-OPTIMIZATION: sweep the operating point and find the coverage/
accuracy that maximizes return -- the answer to "is the optimal 60%? 63%? 70%?".
Truth = the 30-min (K=6) outcome; direction = sign(p_K6 - 0.5).
"""
import sys, numpy as np, pandas as pd
SUF = sys.argv[1] if len(sys.argv) > 1 else ""   # "" -> v2 files, "_v3" -> cross-exchange files

d3 = pd.read_pickle(f"oos_v4_K3{SUF}.pkl").rename(columns={"p": "p3", "y": "y3", "fwdK": "f3"})
d6 = pd.read_pickle(f"oos_v4_K6{SUF}.pkl").rename(columns={"p": "p6", "y": "y6", "fwdK": "f6"})
d9 = pd.read_pickle(f"oos_v4_K9{SUF}.pkl").rename(columns={"p": "p9", "y": "y9", "fwdK": "f9"})
d = d3[["open_time", "p3"]].merge(d6[["open_time", "p6", "y6", "f6"]], on="open_time") \
                            .merge(d9[["open_time", "p9"]], on="open_time")
n = len(d)
p3, p6, p9 = d.p3.values, d.p6.values, d.p9.values
y = d.y6.values.astype(int); fwd = d.f6.values
print(f"aligned bars={n}  mean|30min move|={np.abs(fwd).mean()*1e4:.1f}bps  up_rate={y.mean():.4f}")

s3, s6, s9 = np.sign(p3 - .5), np.sign(p6 - .5), np.sign(p9 - .5)
agree = (s3 == s6) & (s6 == s9)
side = s6
conf = np.minimum.reduce([np.abs(p3 - .5), np.abs(p6 - .5), np.abs(p9 - .5)])  # all must be confident
correct = ((side > 0).astype(int) == y)

print(f"\nAGREEMENT (all 3 horizons agree): coverage={agree.mean():.3f}  "
      f"accuracy={correct[agree].mean():.4f}")

print("\n=== accuracy & RETURN vs operating threshold (agreement + confidence gate) ===")
print("ret models: BINARY even-odds (payoff +1/-1, fee f per bet); SPOT (capture 30-min move, cost c bps round-trip)")
hdr = f"  {'coverage':>8} {'n':>6} {'acc':>7} | {'EVbin@1%':>9} {'EVbin@2%':>9} | {'net_bps@10':>10} {'tot%spot@10':>11}"
print(hdr)
grid = np.quantile(conf[agree], np.linspace(0, 0.98, 18))
rows = []
for thr in grid:
    sel = agree & (conf >= thr)
    nsel = int(sel.sum())
    if nsel < 50: continue
    acc = correct[sel].mean()
    edge = 2 * acc - 1
    # binary even-odds EV per bet net of fee f (fraction of stake)
    ev1 = edge - 0.01; ev2 = edge - 0.02
    # spot: signed 30-min return minus round-trip cost c bps
    sgn = side[sel]
    gross = sgn * fwd[sel]
    net_bps10 = (gross.mean() - 10e-4) * 1e4
    tot_spot10 = (gross - 10e-4).sum() * 100
    cov = nsel / n
    rows.append((cov, nsel, acc, edge, ev1, ev2, net_bps10, tot_spot10))
    print(f"  {100*cov:7.2f}% {nsel:6d} {acc:7.4f} | {ev1:+9.4f} {ev2:+9.4f} | {net_bps10:+10.2f} {tot_spot10:+11.1f}")

R = pd.DataFrame(rows, columns=["cov", "n", "acc", "edge", "ev1", "ev2", "net_bps10", "tot_spot10"])
# total binary growth (Kelly, even-odds): per bet ~0.5*edge^2 ; total ~ n*0.5*edge^2
R["kelly_growth"] = R["n"] * 0.5 * R["edge"] ** 2
R["ev2_total"] = R["n"] * R["ev2"]
print("\n=== OPTIMAL OPERATING POINTS ===")
def summarize(label, row, note=""):
    print("  %-16s: acc=%.4f at coverage %.2f%% (n=%d) %s" %
          (label, row["acc"], 100 * row["cov"], int(row["n"]), note))
summarize("max ACCURACY", R.loc[R.acc.idxmax()])
summarize("max KELLY growth", R.loc[R.kelly_growth.idxmax()], "<- best compounding")
summarize("max TOTAL EV(2%)", R.loc[R.ev2_total.idxmax()], "<- most total profit")
R.to_pickle("combine_frontier.pkl")
print("\nsaved combine_frontier.pkl")
