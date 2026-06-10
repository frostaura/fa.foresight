# ML campaign 2026-06-10 — the 60% push

**Objective:** lift the BTC short-horizon direction model to a pooled, trade-weighted
hit-rate ≥60% across all chaos-test regimes, with zero busts under quarter-Kelly.
**Outcome: not met — honest ceiling found at ~58.3% within known regimes, and the
edge was falsified on a genuinely novel crash regime.** Everything below is
reproducible from `research/btc-5min-direction/lab/` (frozen harness, hash-frozen
dataset, 49-trial append-only ledger; headline numbers script-generated in
`lab/REPORT_HEADLINE.md`).

## What was run

A multi-fleet agent campaign (~50 subagents): data foundry (rebuilt 18-symbol spot +
perp + premium + funding + OI/positioning 5m metrics + Coinbase + Deribit DVOL,
Sep 2024 → Jun 2026, validated gap-free), independent research (literature,
blind-fresh-eyes hypotheses, methodology protocol), a 163-feature leakage-audited
stack, 12 parallel single-lever experiments, a synthesis round, a 3-lens adversarial
audit, and a final seed-honest round on a coverage-exact harness v2.

## Headline (champion `fp_bag_k4`: 5-seed-bagged LGBM, 20-min horizon, 5% coverage)

- **58.31% pooled** (n=4,831; Wilson LB95 57.14%; multiplicity-deflated LB 56.13%),
  **12/12 windows >50%**, zero busts (0/1000 bootstrap, quarter-Kelly).
- Beats the May-2026 predecessor band (57.7–58.9%) on strictly harder measurement:
  timestamp-pinned windows, exact coverage, seed-bagging, calibration-only thresholds.
- Regime profile: chop 60–65% (predecessor's weakness, now the strength — the new
  order-flow/derivatives features carry it), bear 54.7–58.5% (now the weakness).

## The two findings that matter more than the number

1. **Single-seed results at this SNR are coin flips on top of skill.** The campaign's
   loudest "breakthrough" (59.12% at K=4, seed 42) collapsed to 55.32% under seed 7.
   Seed-bagging (B=5) is now mandatory; the bagged truth (58.31%) survived audit.
2. **The edge is not all-weather — it fails on regime *entry*.** The one-shot locked
   holdout (June 1–10, BTC −16.4%, a crash unlike anything in training) scored
   **48.6% on 294 trades**, with the damage in the first 48h of the new leg
   (40–43%), recovering once the regime aged. Within-sample bear windows already
   showed 8–14pp calibration overconfidence (audit lens 3). A deployed system MUST
   carry a novelty circuit breaker: no betting while the current regime is
   out-of-distribution relative to training. (An OOD veto tested *within* known
   regimes cost edge and was rejected — its value is specifically at regime breaks;
   that distinction is the next campaign's pre-registered hypothesis.)

## Falsified honestly (do not re-litigate without new data)

Event-only gating, magnitude-masked labels, feature pruning, meta-labeling on this
stack, regime-mixture wrappers, K≤2 / K≥6 horizons, ensemble-agreement gating at
K=4, bear-row upweighting (the bear weakness is short-side *calibration*
overconfidence, not ranking; after 24h falls the 20-min base rate skews UP).

## What deployment-grade looks like given these numbers

A regime-flat 57–58% with calibrated probabilities is strongly +EV on binary
even-odds markets (EV ≈ +0.13–0.15/bet before venue spread) and bust-clean at
quarter-Kelly — but ONLY behind: (a) a regime-novelty kill-switch, (b) per-regime
calibration (Mondrian — implemented, modest gain, keeps Kelly honest in bear),
(c) realized venue prices instead of the synthetic 0.55 fee (resolution doc).

## Next campaign (pre-registered direction)

1. Novelty circuit breaker validated on regime-break replay (Jun 2026, Feb 2026,
   Oct 2025 entries) — the single highest-value item.
2. Orthogonal data the current stack lacks: order-book depth/imbalance snapshots,
   Deribit options flow/skew (not just DVOL), stablecoin mint events.
3. The 60% bar remains the target *at the venue-relevant coverage*; reaching it
   credibly most likely requires the new data, not more modeling on the current 163.

## Artifacts

`lab/PROTOCOL.md` (campaign law) · `lab/chaos_harness_v2.py` (frozen evaluator) ·
`lab/ledger.jsonl` (49 trials) · `lab/REPORT_HEADLINE.md` (script-generated numbers) ·
`lab/results/*.json` (every run incl. `HOLDOUT_fp_bag_k4.json`) ·
`lab/features/build_*.py` (deterministic, `--end`-extendable feature builders) ·
audit transcripts in the session's workflow logs.

## Productionized (2026-06-10)

The campaign recipe is now in the product, behind the existing deterministic-flow
machinery (trainer → backtest → chaos → paper → live all share one path).

**What shipped where:**

- **Models** — `Foresight | 15m | v3-bag` (`ModelIds.ForesightFifteenMinV3Bag`,
  `…0008`) and `Foresight | 5m | v3-bag` (`…0009`, exact A/B sibling of v2).
  Definitions in `Infrastructure/Persistence/BuiltInModels.cs`
  (`BuildForesight15mV3BagFlow` / `BuildForesight5mV3BagFlow`), seeded/refreshed by
  `DatabaseInitializer`. 1h interval support added (`SupportedSymbols.Intervals`)
  for the 15m model's HTF regime pack.
- **Trainer** — `Application/Models/ModelTrainer.cs` consumes the new `model.gbt`
  params (`bags`, `seed`, `coverage`) and writes four ADDITIVE TrainedState fields
  (absent ⇒ legacy behavior unchanged): `modelGbtBag` (B=5 consecutive-seed
  ensemble; bag-mean is both the WF metric and the served probability),
  `calibration` (isotonic/PAVA fit on embargoed out-of-fold predictions —
  `Application/Models/IsotonicCalibration.cs`), `confidenceGate` (threshold =
  (1−coverage) quantile of |pCal−0.5| on the calibrated OOF slice), and `oodGuard`
  (per-feature train stats; ≥3 features beyond 8σ ⇒ veto).
- **Serving** — `Application/Flow/Nodes/RegressionNodes.cs` (GBT node): bag-mean →
  isotonic → OOD veto → confidence gate. **Abstention canon: emit pUp = 0.5** — the
  single choke point every engine (backtest, chaos, paper, live) already treats as
  no-bet, which is what keeps backtest == chaos == live.
- **Sizing/placement** — `kelly-q2`
  (`Domain/Trading/StakingStrategy.cs::QuarterKellyTwoPercentCappedStakingStrategy`):
  quarter-Kelly on realised edge, 2%-of-bankroll hard cap, whole-dollar floor.
  Session placement (paper + live) gained an EV gate
  (`TradingGuardrailOptions.EvGateMargin`) and a concurrent-exposure cap
  (`MaxTotalExposurePctBankroll`). `CalibrationRescaler` demoted to telemetry-only:
  decisions run off the served (in-node calibrated) pUp; the rescaler value is
  persisted in bet `NotesJson` for comparison.

**Label mapping (the venue decision):** the research champion is K=4 (20-min
cumulative direction on 5m bars), but Polymarket has no 20-min instrument. The
shipped 15m model predicts the venue-native 15m candle body (close vs open) — the
closest tradeable analog of research K=3. So the product label is deliberately NOT
the certified research label; expect the product number to sit below 58.3% on
horizon mismatch alone.

**Data gaps that keep product hit-rate below research:** the product feature set is
klines-only (spot OHLCV across 5m/15m/1h + sub-bar pressure). The research stack's
strongest levers — perp OI/positioning, funding, Coinbase spot (US-flow proxy),
Deribit DVOL, and 18-symbol breadth — have no live ingestion path yet, so none of
them are in the shipped matrix.

**In-product validation (2026-06-10, honest):** walk-forward of the shipped 15m
v3-bag flow through the actual product trainer/backtester on real Binance candles
(`Tests/Flow/V3BagProductValidationExperiment.cs`, 4 folds, A/B twin with
coverage=0 to isolate the gate). 60-day window (includes the June crash):
ungated pooled OOS **50.91%** (CI 49.45–52.36, n=4,526; folds 52.0/53.2/50.9/47.5);
gated (coverage=0.05) pooled **51.30%** (CI 47.47–55.11, n=655) at **85.5%
abstention**, with gated per-fold hit DEGRADING into the crash regime
(52.2% → 58.7% → 43.8% → 36.2%) — the product reproduces the campaign's
regime-entry falsification, not its 58.3% headline. 22-day post-crash window:
ungated 51.56% (n=1,598), gated 51.21% (n=248, 84.5% abstention). Below the
hoped-for 52–57% band: with klines-only features and weeks (not the research's
~1.5 years) of training data, the product model currently has NO statistically
demonstrable edge — the venue-grade edge lives in the missing
derivatives/order-flow data and in regime-aware gating, exactly as pre-registered
above. The plumbing (bagging, calibration, gate, OOD veto, abstention canon) is
validated end-to-end; the signal is not yet.
