# Campaign report — BTC short-horizon direction, 2026-06-10

## Headline (script-generated from ledger + results; Rule 8)

**Champion `fp_bag_k4`** (5-seed-bagged LGBM, K=4 = 20-min horizon, pre-registered coverage 0.05, harness chaos_harness_v2, dataset 513dd7f65b25e0a6):

- Pooled trade-weighted hit: **58.31%** on n=4831 (plain Wilson LB95 57.14%, deflated LB 56.13%, N_eff=45)
- Windows above 50%: 12/12; all windows meet the 30-trade minimum: True
- Binary even-odds EV per bet (fee 0.02): +0.1462
- Bust test (quarter-Kelly, 2% cap, 10% exposure): chrono bust=False, bootstrap busts 0/1000, chrono max DD 41%
- Label-overlap caveat: K=4 labels on 5m bars give effective n ≈ raw/1.9 (audit lens 2); the LB95 reads ≈0.5pp looser under that discount and the edge survives.

**Campaign gates (pooled ≥60.0 / LB95 ≥58.5 / deflated LB ≥57.0): NOT MET.** No configuration in 49 ledger trials met them. The 60% target is not certified.

## One-shot holdout (June 1–10 2026, BTC -16.4%, Rule 5 — falsification)

- **48.64% on 294 trades** (Wilson LB95 43.9%, binary EV -0.047). Verdict: **FALSIFIED as an all-weather edge.**
- By day (n, hit): 06-01: 20/0.40, 06-02: 83/0.43, 06-03: 49/0.51, 06-04: 43/0.51, 06-05: 8/0.50, 06-06: 27/0.63, 06-07: 34/0.47, 06-08: 2/1.00, 06-09: 28/0.46
- Failure concentrates in the first 48h of the fresh crash leg (Jun 1–2: 0.40–0.43); days 3+ of the leg recover to ≈0.47–0.63. The model fails on *novel* regime entry, consistent with the audit's bear-overconfidence finding.

## Champion per-window detail (cov 0.05)

| win | start | regime | BTC move | n | hit |
|---|---|---|---|---|---|
| 0 | 2025-08-01 | bear | -4.7% | 373 | 0.5845 |
| 1 | 2025-08-26 | bull | +5.2% | 471 | 0.5478 |
| 2 | 2025-09-20 | bear | -4.6% | 678 | 0.5634 |
| 3 | 2025-10-16 | chop | -3.9% | 269 | 0.6022 |
| 4 | 2025-11-10 | bear | -15.1% | 348 | 0.5747 |
| 5 | 2025-12-05 | chop | -1.8% | 294 | 0.6293 |
| 6 | 2025-12-31 | chop | +0.3% | 429 | 0.6131 |
| 7 | 2026-01-25 | bear | -25.2% | 452 | 0.5597 |
| 8 | 2026-02-19 | bull | +13.3% | 405 | 0.5827 |
| 9 | 2026-03-17 | chop | -2.6% | 292 | 0.6473 |
| 10 | 2026-04-11 | bull | +12.4% | 403 | 0.6030 |
| 11 | 2026-05-06 | bear | -10.0% | 417 | 0.5468 |

## Ledger summary

- 49 trials, append-only, two harness versions (v1; v2 after the audit found v1's isotonic-plateau coverage inflation — v2 gates exactly).
- Top trials by hit at cov 0.05: fp_bag_k4 58.3%; fp_basebag_k4 58.3%; fp_bag_k4_rec120 58.3%; fp_mondrian_k4 57.7%; fp_blend_k4_rec120 57.6%; fp_mondrian_k4_shrunk 57.6%; fp_bag_k5 57.6%; fp_blend_k4 57.5%
