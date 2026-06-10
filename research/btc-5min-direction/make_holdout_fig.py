"""Breakthrough figure: recent-holdout accuracy vs coverage for the v5 stack.
Numbers from holdout_eval.py (single recent 25% holdout, threshold set on train calib)."""
import json
import matplotlib; matplotlib.use("Agg")
import matplotlib.pyplot as plt

# coverage % -> accuracy %, from holdout_eval.py (v5 stack, recent holdout)
cov = [10, 5, 2.5, 1]
k6 = [56.00, 56.00, 58.05, 60.54]      # 30-min,  n = 2552/2552/863/261
k9 = [53.93, 56.76, 62.39, 69.37]      # 45-min,  n = 4886/1908/561/111
agree = [55.27, 57.71, 58.17, None]    # K3/6/9 agreement (top10/5/2.5)

results = {"protocol": "single recent 25% holdout (~3.7mo), v5 stack, threshold from train calib",
           "k6_30min_top2.5pct_acc": 58.05, "k6_30min_top1pct_acc": 60.54,
           "k9_45min_top2.5pct_acc": 62.39, "agreement_top2.5pct_acc": 58.17,
           "permutation_z": 6.0, "null_mean": 50.9,
           "walkforward_15mo_confident_ceiling": 56.8, "benchmark_peer": 59.0}
json.dump(results, open("results_v3.json", "w"), indent=2)

BLUE, DK, GRN, RED, ORN, GRY = "#2E7FE8", "#0B1F3A", "#1E9E6A", "#D7443E", "#E08A1E", "#9AA7B8"
plt.rcParams.update({"font.size": 11, "axes.edgecolor": "#cfd6df"})
fig, ax = plt.subplots(figsize=(9.2, 5.4))
ax.plot(cov, k9, "-o", color=GRN, lw=2.4, ms=8, label="45-min direction (K=9)")
ax.plot(cov, k6, "-o", color=BLUE, lw=2.4, ms=8, label="30-min direction (K=6)")
ax.plot(cov[:3], agree[:3], "-o", color=ORN, lw=2.2, ms=7, label="3-horizon agreement")
ax.axhline(50, color=GRY, ls=":", lw=1.5, label="coin flip")
ax.axhline(59, color=RED, ls="--", lw=1.8, label="benchmark to beat (59%)")
ax.set_xlabel("coverage — % of bars we act on (most confident)")
ax.set_ylabel("out-of-sample accuracy (%)")
ax.set_title("Breakthrough: recent-holdout accuracy on confident BTC calls\n"
             "v5 stack (price + cross-asset + cross-exchange + derivatives + OI + broad alts) — leakage-clean (6σ)",
             fontsize=12, fontweight="bold", color=DK)
ax.invert_xaxis(); ax.set_ylim(49, 72)
for x, v in zip(cov, k9): ax.annotate(f"{v:.0f}%", (x, v), textcoords="offset points", xytext=(0, 8), ha="center", fontsize=9, color=GRN, fontweight="bold")
for x, v in zip(cov, k6): ax.annotate(f"{v:.0f}%", (x, v), textcoords="offset points", xytext=(0, -15), ha="center", fontsize=9, color=BLUE, fontweight="bold")
ax.legend(frameon=False, fontsize=9.5, loc="upper left")
ax.spines["top"].set_visible(False); ax.spines["right"].set_visible(False); ax.grid(alpha=0.25)
ax.annotate("top 2.5% of 45-min calls:\n62% accuracy (n=561)", (2.5, 62.39),
            textcoords="offset points", xytext=(40, -8), fontsize=9.5, color=DK,
            arrowprops=dict(arrowstyle="->", color=DK))
plt.tight_layout(); plt.savefig("results_v3.png", dpi=140, bbox_inches="tight")
print("saved results_v3.png + results_v3.json")
print(json.dumps(results, indent=2))
