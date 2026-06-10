"""Chaos-test figure: per-window hit-rate by regime + pooled hit-rate vs coverage."""
import numpy as np, pandas as pd
import matplotlib; matplotlib.use("Agg")
import matplotlib.pyplot as plt

opt = pd.read_csv("chaos_opt_K9.csv")
res = pd.read_csv("chaos_results.csv")[["win", "regime", "btc_move_pct", "binEV_top2_5"]]
w25 = opt[opt["cov"] == 0.025].merge(res, on="win").sort_values("win")

# pooled hit-rate vs coverage (trade-weighted)
pooled = []
for cov, g in opt.groupby("cov"):
    g = g.dropna(subset=["hit"]); pooled.append((cov*100, (g.hit*g.n).sum()/g.n.sum(), g.n.sum()))
P = pd.DataFrame(pooled, columns=["cov", "pooled_hit", "trades"]).sort_values("cov")

BLUE, DK, GRN, RED, ORN, GRY = "#2E7FE8", "#0B1F3A", "#1E9E6A", "#D7443E", "#E08A1E", "#9AA7B8"
regc = {"bull": GRN, "bear": RED, "chop": GRY}
plt.rcParams.update({"font.size": 11, "axes.edgecolor": "#cfd6df"})
fig, ax = plt.subplots(1, 2, figsize=(15, 5))
fig.suptitle("Chaos test: BTC 45-min direction model across 12 windows (every regime, Aug 2025–May 2026)",
             fontsize=13.5, fontweight="bold", color=DK, y=1.02)

# Panel 1: per-window hit (top 2.5%) colored by regime
bars = ax[0].bar(range(12), w25.hit*100, color=[regc[r] for r in w25.regime])
ax[0].axhline(50, color=GRY, ls=":", lw=1.4)
ax[0].axhline(60, color=DK, ls="--", lw=1.7, label="60% target")
ax[0].axhline(w25.hit.mean()*100, color=BLUE, ls="-", lw=2, label=f"avg = {w25.hit.mean()*100:.1f}%")
ax[0].set_xticks(range(12)); ax[0].set_xticklabels([f"W{w}\n{m:+.0f}%" for w, m in zip(w25.win, w25.btc_move_pct)], fontsize=8)
ax[0].set_ylabel("hit-rate, top-2.5% confidence (%)"); ax[0].set_ylim(45, 66)
ax[0].set_title("1. Hit-rate per window (bar = BTC move; green bull / red bear / grey chop)", fontsize=10.5, fontweight="bold", color=DK)
ax[0].legend(frameon=False, fontsize=9, loc="upper left")
from matplotlib.patches import Patch
ax[0].legend(handles=[Patch(color=GRN, label="bull"), Patch(color=RED, label="bear"), Patch(color=GRY, label="chop"),
                      plt.Line2D([],[],color=BLUE,lw=2,label=f"avg {w25.hit.mean()*100:.1f}%"),
                      plt.Line2D([],[],color=DK,ls='--',label="60% target")], frameon=False, fontsize=8.5, loc="lower left", ncol=2)

# Panel 2: pooled hit-rate vs coverage
ax[1].plot(P["cov"], P["pooled_hit"]*100, "-o", color=BLUE, lw=2.4, ms=8)
ax[1].axhline(60, color=DK, ls="--", lw=1.7, label="60% target")
ax[1].axhline(50, color=GRY, ls=":", lw=1.4)
for _, r in P.iterrows():
    ph = float(r["pooled_hit"]) * 100; cv = float(r["cov"]); tr = int(r["trades"])
    ax[1].annotate("%.1f%%\n(%d tr)" % (ph, tr), (cv, ph), textcoords="offset points", xytext=(0, 9), ha="center", fontsize=8.5, color=DK)
ax[1].set_xlabel("coverage — % of bars traded (most confident)"); ax[1].set_ylabel("pooled hit-rate across all windows (%)")
ax[1].set_xscale("log"); ax[1].set_xticks([0.5,1,2.5,5,10]); ax[1].set_xticklabels(["0.5","1","2.5","5","10"])
ax[1].invert_xaxis(); ax[1].set_ylim(52, 62)
ax[1].set_title("2. Honest pooled hit-rate vs conviction: peaks ~59%, short of 60%", fontsize=10.5, fontweight="bold", color=DK)
ax[1].legend(frameon=False, fontsize=9)
for a in ax:
    a.spines["top"].set_visible(False); a.spines["right"].set_visible(False); a.grid(axis="y", alpha=0.25)
plt.tight_layout(); plt.savefig("results_chaos.png", dpi=140, bbox_inches="tight")
print("saved results_chaos.png")
print("avg per-window top2.5% =", round(w25.hit.mean(),4), "| pooled by coverage:")
print(P.to_string(index=False))
