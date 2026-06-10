"""Print a compact summary of a harness results JSON. Usage: a3_summarize.py NAME"""
import json, os, sys

LAB = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
name = sys.argv[1]
r = json.load(open(os.path.join(LAB, "results", f"{name}.json")))
print(name, "K", r["K"], "primary", r["primary_cov"], "n_eff", r["n_eff"])
for w in r["per_window"]:
    if w.get("skipped"):
        print(w); continue
    print(f"win {w['win']:2d} {w['start'][:10]} {w['regime']:4s} {w['move_pct']:+6.1f}% "
          f"@2.5%: {w['hit_0.025']:.3f}/{w['n_0.025']:4d}  @1%: {w['hit_0.01']:.3f}/{w['n_0.01']:4d}")
for cov, p in r["pooled"].items():
    print(f"cov {cov}: n={p['n']} hit={p['hit']} LB95={p['wilson_lb95']} "
          f"deflLB={p['wilson_lb_deflated']} wins>50={p['windows_above_50']}/"
          f"{p['windows_meeting_min']} ok={p['min_trades_ok']} ev={p['binary_ev_even_odds']}")
print("bust:", r["bust_test_primary"])
