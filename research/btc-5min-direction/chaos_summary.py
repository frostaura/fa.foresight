"""Summarize the chaos test: avg hit-rate across windows (THE target metric), regime
breakdown, profit, and a chronological quarter-Kelly bankroll path + bust check."""
import numpy as np, pandas as pd
R = pd.read_csv("chaos_results.csv").sort_values("win").reset_index(drop=True)
pd.set_option("display.width", 200)
print("=== PER-WINDOW (K=9, 45-min) ===")
print(R[["win","start","end","regime","btc_move_pct","rvol_bps","n_top10","hit_top10","spot_bps_top10","binEV_top10","n_top2_5","hit_top2_5","binEV_top2_5"]].to_string(index=False))

print("\n=== AVG HIT-RATE ACROSS ALL CHAOS WINDOWS (the target, >=60%) ===")
for c in ["hit_top10","hit_top2_5"]:
    v = R[c].dropna()
    print(f"  {c:12s} mean={v.mean():.4f}  min={v.min():.4f}  max={v.max():.4f}  windows>=60%={int((v>=0.60).sum())}/{len(v)}  windows>50%={int((v>0.5).sum())}/{len(v)}")

print("\n=== BY REGIME (hit_top2_5) ===")
print(R.groupby("regime")["hit_top2_5"].agg(["mean","min","max","count"]).round(4).to_string())

print("\n=== PROFIT ===")
print(f"  windows with positive binary EV (top2.5%): {int((R['binEV_top2_5']>0).sum())}/{len(R)}")
print(f"  mean binary EV/bet top2.5% @2% fee: {R['binEV_top2_5'].mean():+.4f}")
print(f"  spot net bps/trade top10% (mean across windows): {R['spot_bps_top10'].mean():+.2f}  (spot still loss-making)")

# chronological quarter-Kelly bankroll on the top-10% trades, even-odds binary, 2% fee
T = pd.read_csv("chaos_trades.csv").sort_values("ts").reset_index(drop=True)
hit = T["correct"].mean(); edge = 2*hit - 1
kelly = max(0.0, edge); frac = 0.25*kelly      # quarter-Kelly
bank = 1.0; peak = 1.0; maxdd = 0.0; busts = 0
path = []
for correct in T["correct"].values:
    stake = frac * bank
    bank += stake*(1-0.02) if correct else -stake*(1+0.02)
    peak = max(peak, bank); maxdd = max(maxdd, (peak-bank)/peak)
    if bank <= 0.30*1.0: busts += 1     # "bust" = drawdown to <30% of start
    path.append(bank)
print("\n=== KELLY BANKROLL (top-10% trades, even-odds binary, quarter-Kelly, 2% fee) ===")
print(f"  trades={len(T)}  overall hit={hit:.4f}  edge={edge:.4f}  bet_frac={frac:.3f}")
print(f"  final bankroll={bank:.2f}x  max_drawdown={maxdd*100:.1f}%  bust_events(<30%)={busts}")
pd.DataFrame({"ts":T["ts"],"bankroll":path}).to_csv("chaos_bankroll.csv", index=False)
print("saved chaos_bankroll.csv")
