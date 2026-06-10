# Campaign protocol — June 2026 "beat 60%" push

Binding rules for every agent in this campaign. Derived from the methodology audit
(8 rules), the prior chaos study, and the a1/a2/a3 ML audits. The harness
(`chaos_harness_v1.py`) enforces most of this mechanically — the rest is law.

## The target
Pooled, trade-weighted hit-rate **≥ 60.0%** at a pre-registered operating coverage,
across the 12 timestamp-pinned chaos windows (2025-08-01 → 2026-06-01), with:
- plain one-sided 95% Wilson lower bound **≥ 58.5%**,
- multiplicity-deflated Wilson LB (z at 0.05/N_eff, N_eff = ledger rows, floor 45) **≥ 57.0%**,
- **≥ 10 of 12** windows above 50% at the operating point,
- **≥ 30 trades in every window** and **≥ 2,500 pooled** at the operating point,
- bust test: chrono bust = false AND bootstrap bust rate = 0/1000 at quarter-Kelly.

## Hard rules
1. **Frozen harness only.** All chaos numbers come from `chaos_harness_v1.py`,
   unmodified, against the hash-frozen `features_v1.pkl`. A result from any other
   evaluator does not exist.
2. **Locked holdout.** June 1–10 2026 is physically absent from the dataset. Its only
   use: ONE run, by the final verifier, on the single nominated champion. Its role is
   falsification (a real edge must not collapse in the unseen crash leg), not estimation.
3. **Pre-registration.** Each experiment declares exactly one PRIMARY_COV before its
   first harness run. Reporting a different coverage afterward = a new ledger trial.
4. **Thresholds from calibration slices only.** The harness does this; do not bypass.
   Banned: gate-quantile shopping, seed shopping, window-subset reporting,
   test-window threshold computation, "best window" headlines, simple averages.
5. **No future data.** Every feature at bar t uses only data timestamped ≤ t's close.
   Slow sources (1h DVOL, 8h funding, metrics) join as last-record-≤-t with a shift.
6. **Trial ledger.** The harness appends every evaluation to `ledger.jsonl`. Never
   delete or rewrite it. N_eff for deflation = ledger row count (floor 45).
7. **Feature scripts are deterministic and rerunnable** with `--end` so the verifier
   can extend them over the holdout for the one-shot champion run.
8. **Headline = pooled trade-weighted hit + both LBs + N_eff.** Nothing else.

## Window boundaries (UTC, pinned)
2025-08-01 / 08-26 / 09-20 / 10-16 / 11-10 / 12-05 / 12-31 / 2026-01-25 / 02-19 /
03-17 / 04-11 / 05-06 / 06-01 — 12 equal ~25.3-day windows.

## Prior honest benchmark (to beat, not to re-discover)
57.7% simple avg / 58.9% trade-weighted at top-0.5% (n=1,782), 11/12 windows > 50%,
weakest in low-vol chop (~48–49%). Anything whose deflated LB does not clear this band
is noise re-branded.
