# ML audit — fa.foresight (BTC up/down → Polymarket) — Served≠Validated (2026-05-30)

> **Independent report by one of three engineers reviewing this system in isolation.** I did not see
> the other two reviews. This is my own investigation and my own root-cause thesis.
>
> Scope: target = BTC up/down for a single Polymarket period; unit of observation = one closed candle's
> decision; horizon = `horizonSteps` candles ahead (default 2 → 10 min on 5m, graded by the TARGET
> candle's own body close>open); metric that matters = realised directional hit-rate of the **bets the
> live bot actually places**, and downstream P&L vs Polymarket odds. Data structure = single 24/7 time
> series, 1m/5m/15m, stationary TA features.
> Method: full code read of `ModelTrainer.cs`, `BacktestRunner.cs`, `WalkForwardEvaluator.cs`,
> `CalibrationRescaler.cs`, `LivePredictionService.cs`, `LiveSessionEngine.cs`, `PaperTradingService.cs`,
> `RegressionNodes.cs`, `GradientBoostedTrees.cs`, `Indicators.cs`, `BuiltInModels.cs` (v2 GBT / v6 LogReg).
> Empirical leg (honest_benchmark.py on reconstructed BTC data) was **attempted and blocked** — see
> Headline numbers. Claims tagged (verified) = read in code; (inferred) = reasoned.
> Objective: raise real out-of-sample directional hit-rate. Hardware ceiling: must run on a Raspberry Pi 5.

## Headline numbers

**I could not produce a fresh empirical number this pass — disclosed honestly.** The repo carries no
committed candle data (data is fetched live from Binance), and in this session (a) Binance REST egress
from the sandbox was blocked (`api.binance.com` returned empty / refused), (b) Coinbase's candle API was
reachable but caps at 300 rows/call, and (c) the isolated Linux workspace dead-locked on the first
long-running fetch process and never freed, so `honest_benchmark.py` could not be run. I therefore did
**not** trust any number I could not measure, and grounded the audit in the code.

The honest number the code itself states (verified, `RegressionNodes.cs:155`): v6 logistic backtest
**~53%** on the high-conviction subset, **~51%** live across all candles. v2 GBT is projected (code
comments, `BuiltInModels.cs:431`) at **56–58%**, untested out-of-sample at the time of writing. On a
near-50/50 BTC short-horizon target these are **plausible-but-fragile real numbers** (a true 53–56%
directional edge on 5m BTC is at the upper edge of what TA-only features realistically deliver; treat
56–58% projections as optimistic until a clean WF run confirms them).

The number that matters — **the hit-rate of the bets the live bot actually places** — is currently
**unmeasured and not equal to the WalkForward number**, because the live/paper path decides the bet
*side* on the **calibrated** probability while WalkForward and the backtest decide on the **raw**
probability (see thesis). So the bankable honest figure today is: *unknown, and provably not the WF
figure.* Expected after my top lever (make the served path the validated path): the WF number becomes
the honest served number — call it **~0.51–0.53 → a trustworthy ~0.51–0.53 you can actually bank**, with
the upside that you can then legitimately chase the GBT/feature levers below toward ~0.55.

## Hypothesis (the bet I make)

**The dominant problem is not signal — it is that the validated model and the served model are different
models.** Validation (`WalkForwardEvaluator` → `BacktestRunner.RunAsync`) grades and sides on `pUpRaw`.
Live (`LiveSessionEngine.cs:422-431`) and paper (`PaperTradingService.cs:468`) run `pUp` through
`CalibrationRescaler.RescaleAsync` and then take the **side** from `StakingEngine.DecideSide(calibratedPUp)`.
The rescaler is a piecewise-linear interpolation of *empirical bucket hit-rates* — it is **not constrained
to be monotonic and not constrained to keep the same side of 0.5**. So a raw pUp of 0.48 (a DOWN call WF
scored) can be lifted above 0.50 by the rescaler and served as an UP bet (and vice-versa). The instant the
calibrator crosses the decision boundary for even a minority of candles, the live bet set diverges from the
WF bet set, and **every honest number you computed describes a model you are not running.** Until that gap
is closed you cannot bank *any* accuracy figure, and you cannot attribute live underperformance to signal,
regime, or strategy — the measurement is severed from the deployment. Fixing it is nearly free and is the
precondition for every other lever paying off.

## The Good (brief)
- **Anti-look-ahead is genuinely careful and consistent across train/backtest/replay** (verified):
  `TrainingSliceProvider` and `BacktestSliceProvider` are line-for-line mirrors, target-tf capped at the
  anchor open, off-tf capped by *close*-time (`clampedEnd - offIntervalMs`), candle i+1 never an input.
- **Features are stationary by construction** (verified, `Indicators.cs`/`norm_pack`/`momentum_pack`):
  returns, ATR-normalised distances, z-scores, sin/cos time. No raw price levels fed to the linear model;
  no global scaler fit before the split (there is no scaler at all — fine for these features).
- **Label matches the venue** (verified): `yDir = close(target) > open(target)`, identical in trainer,
  backtest, replay, and live resolution — the Polymarket close-vs-open settlement canon. No label-derived
  features; the target candle's OHLC never enters the feature slice.
- **WalkForward is the right shape** (verified): expanding-window, re-trains per fold, embargo ≥ horizon
  (`EmbargoCandles=3`, `max(3,horizon)`), Wilson CI, in-sample−OOS overfit gap, folds-above-50% regime
  check, and an honest `PassesGuards` gate (CI lower bound > 0.5, ≥1000 bets, majority of folds > 50%).
- **GBT is conservatively regularised** (verified): depth 3, `min_samples_leaf` 200–250, row/col subsample
  0.7, L2 1–2, shrinkage 0.03–0.04 — the right knobs for a thin-edge target; seeded/deterministic.
- **Brier score is tracked** alongside hit-rate (verified, `BacktestRunner` + WF `MeanBrier`) — a proper
  scoring rule is present even though the headline is accuracy.

## Improvements — ranked by expected honest lift on the metric that matters

### 1. Validate the SERVED model: side-decision must use the same probability live and in WF — current → expected: unknown → bankable ~0.51–0.53 · impact: High · effort: S · confidence: 80%
The calibrator changes the bet side; WF/backtest never apply it. **Fix (pick one, ranked):**
(a) **cheapest & safest** — calibrate *probabilities for sizing/odds only, never for the side*; take the
side from raw pUp in live/paper exactly as WF does (`DecideSide(pUpRaw)`), feed `calibratedPUp` only into
`StakingInputs`/Kelly. This makes WF's side-accuracy literally the served side-accuracy. (b) If calibration
*must* be able to flip sides, then **apply the identical calibrator inside `BacktestRunner`/WF** (fit on a
held-out in-fold slice, applied at scoring) so the validated hit-rate is the calibrated-side hit-rate — and
**constrain the calibrator to be monotonic** (isotonic regression, which `CalibrationRescaler` approximates
but does not enforce) so it can only cross 0.5 when the data genuinely says the raw 0.5 boundary is
mis-located. Acceptance gate: run WF and a forward paper week; the served per-candle side must match the WF
replay side on ≥99% of candles (option a → 100% by construction), and served hit-rate must land inside the
WF Wilson CI. Pi-5 feasibility: trivial — both paths are scalar arithmetic, no model added.

### 2. The calibrator leaks the present into the past at serving and is fit on its own live outputs — current → expected: removes a hidden self-fulfilling bias · impact: Med-High · effort: S · confidence: 65%
(verified, `CalibrationRescaler.GetOrBuildAsync`) The map is built from the **most recent 200 resolved
predictions** for the tenant/interval and applied to the *current* prediction. At a given live decision this
is fine (those 200 are all in the past), but two hazards: (i) the map is **rebuilt on a 2-min TTL from a
rolling window that includes backfilled replay rows** (`BackfillHistoryAsync` writes `ResolvedAt` rows that
the calibrator then consumes) — so the calibrator is partly fit on the model's own leakage-free *historical
replay*, mixing two populations; (ii) because the side flips on this map (lever 1), the calibrator is in a
**feedback loop with its own past bets**. Fix: fit calibration only on genuinely-live resolved rows (the
`PromptTraceJson.backfilled` flag already distinguishes them — filter it out), and rebuild on a fixed cadence
not a 2-min TTL. Acceptance gate: calibration map identical whether or not a backfill ran. Pi-5: trivial.

### 3. Horizon=2 throws away the freshest, most-predictive candle for a now-instant decision — current → expected: ~+1–2pp if i+1 carries signal · impact: Med · effort: S · confidence: 55%
(verified, `ModelTrainer.cs:88-93`, `BacktestRunner.cs:150-156`) The default horizon of 2 was chosen to skip
candle i+1 "forming while a slow (LLM) decision is made." The MVP decision is now an **instant deterministic
compute at candle close** (CLAUDE.md; comments concede horizon=1 "viable now"). Predicting i+2 from features
ending at i discards candle i+1 entirely — the single freshest 5-min bar, which on short-horizon BTC is the
most autocorrelated with the target. Fix: default the built-in flows to **horizon=1** and A/B against
horizon=2 in WF. Acceptance gate: WF OOS hit-rate at h=1 vs h=2 on identical windows, Wilson-CI separated.
Pi-5: trivial (no model change).

### 4. Decision-layer/objective mismatch: optimising raw direction accuracy on a near-coinflip, not edge-vs-odds — current → expected: hit-rate flat, P&L up · impact: Med · effort: M · confidence: 60%
(verified) Headline of record is `HitRate` on a ~50/50 target. But the thing that *pays* is `pUp` vs the
Polymarket YES/NO price — a 51% model can be profitable if it is well-calibrated and odds are mispriced, and a
54% model can lose if it bets into bad odds. The system already computes Brier and already sizes with
edge-aware Kelly (`KellyMath`, `StakingEngine`), so the machinery exists — it is just not the *selection*
metric. Fix: make WF report and gate on **cost-weighted forward P&L / regret vs realised odds** as the
primary, hit-rate/Brier as secondary. This is the cheapest *economic* win and is the honest way to declare
victory on a thin-edge instrument. Acceptance gate: forward paper P&L positive over a rolling window at a
hit-rate that may be < 0.53. Pi-5: trivial.

### 5. Then, and only then, modeling levers (ensembling + small sequence net) — current → expected: ~0.52 → ~0.55 · impact: Med · effort: M · confidence: 45%
Once 1–4 make the served number trustworthy: (a) **Ensemble** the existing LogReg + GBT by averaging
*calibrated* probabilities (out-of-fold stack, never in-fold) — `MajorityVoteNode` exists but votes on sign;
average probabilities instead. Reliable small lift, de-correlated members. Pi-5: trivial (two tiny models).
(b) **Small Temporal CNN or GRU** (≤2 layers, ≤32 units, int8 ONNX) over the last ~30 candles of stationary
features to capture sequence structure the flat per-candle matrix misses. Validate rolling-origin, past-only
inputs, normalisation re-fit per fold. Pi-5 feasibility: a sub-100k-param int8 GRU/TCN is <2 MB and <5 ms CPU
inference on a Pi 5 — fits; a TFT/N-BEATS is overkill here and only marginally Pi-feasible, so hold. Rank LAST
because on a thin-edge TA target the deep net's overfit risk is high and the expected lift is the smallest per
unit effort until measurement (levers 1–2) is fixed.

(Every validity-breaker I found is in the audit table below, including those I rank low.)

## Pipeline & system optimizations (my change set)
- **Serving:** route the bet *side* off raw pUp (or apply the WF-validated calibrator in WF too); reserve
  calibration for sizing only. Enforce isotonic/monotone calibration.
- **Calibration data hygiene:** exclude `backfilled=true` rows; fixed rebuild cadence; per-interval only.
- **Labels/horizon:** flip built-in flows to horizon=1; keep h=2 as an A/B arm.
- **Evaluation:** add forward cost-weighted P&L vs odds as the primary WF gate; keep hit-rate + Brier +
  Wilson CI + overfit gap as guards.
- **Features (post-measurement):** the matrix is solid; if anything, prune correlated columns
  (`ema_spread_atr` vs `px_vs_ema26_atr`; the four `*_x_mom` cross terms) via in-fold permutation importance
  so importances read true — do NOT select on the full set pre-split.
- **Model:** calibrated-probability average of LogReg+GBT first; small int8 GRU/TCN only if the ensemble
  plateaus.

## Leakage, future-bias, train–serve-skew & overfitting audit (mandatory)
| Stage | Verdict | Finding | Fix |
|---|---|---|---|
| Data ingestion / quality | PASS | Live Binance klines; anchor is last *closed* candle, never the forming bar (`LivePredictionService.cs:114`); off-tf clamped by close-time. No restated history (crypto OHLC immutable). | — |
| Feature engineering | PASS | All features stationary (returns/ATR-norm/z/sin-cos); no scaler fit pre-split (none used); warmup gating via `ready` sidecar; temporal features read only `TargetOpenTime` (known at decision). | Optional: prune correlated cols via in-fold permutation importance. |
| Labeling | PASS | `yDir = close(target) > open(target)`, identical train/backtest/replay/live; target OHLC never a feature input; horizon embargo correct. | Consider horizon=1 (signal, not leakage). |
| Splitting / validation | PASS | Expanding-window WF, per-fold retrain, embargo ≥ horizon, Wilson CI, folds>50% check, in-sample−OOS gap, honest `PassesGuards`. Trainer's internal 5-bucket WF is a reasonable secondary. | — |
| Training | PASS | Deterministic seeds; IRLS ridge-escalation guards separation; GBT conservatively regularised; final fit on full window after WF measures the procedure. | — |
| Calibration / uncertainty | **FAIL** | Calibrator (a) **changes the bet side** but is applied live/paper only, never in WF/backtest → served model ≠ validated model; (b) **not constrained monotonic** (piecewise-linear over empirical bucket hits, can cross 0.5); (c) rolling 200-row window **mixes backfilled replay rows with live rows** on a 2-min TTL → partial self-fit/feedback. | Side off raw pUp (or calibrate in WF too); enforce isotonic; exclude `backfilled=true`; fixed rebuild cadence. |
| Evaluation / metric honesty | WEAK | Headline = raw-side hit-rate on a ~50/50 target; the paying quantity is edge-vs-odds P&L. Brier tracked but not the selection metric. Code-comment numbers (53/51%) are plausible; GBT 56–58% is an untested projection. | Promote forward cost-weighted P&L vs odds to primary WF gate. |
| Train–serve skew | **FAIL** | Same root as calibration: live/paper `DecideSide(calibratedPUp)` vs WF/backtest `DecideSide(pUpRaw)`. Feature computation itself is parity-clean (shared slice providers). | Unify the side-decision input across paths (lever 1). |

## Discriminating experiment (does my hypothesis win?)
Cheapest, future-bias-free, no new model: take one trained model and one historical window. Run
`ReplayDirectionsAsync` (raw-side calls) and separately apply the current `CalibrationRescaler` map to each
`pUp` and re-derive the side via `DecideSide`. Count the fraction of candles where the **side flips**.
- **Confirms thesis** if the flip-rate is materially > 0% (even ~3–8% means the live bet set differs from the
  validated set and no WF number is bankable as-served).
- **Refutes/demotes thesis** if the flip-rate is ~0% across realistic windows (calibrator stays one side of
  0.5) — then the served≠validated gap is cosmetic and the dominant lever moves to horizon (lever 3) or the
  decision-layer/odds objective (lever 4), and signal/model levers (5) get their turn.
This is a single offline loop over existing code; it needs no live trading and no fresh data beyond one
backtest window.

## Verdict, expected lift & confidence
**Confidence this is the dominant cause: ~55%.** It is the highest-leverage *certain* defect: the served
model is provably a different model from the validated one whenever the calibrator crosses 0.5, so the entire
honesty apparatus (which is otherwise excellent) is measuring the wrong artifact. Fixing it is ~free and is
the precondition for trusting any accuracy number and for attributing live results. Current honest served
number = **unknown (≠ WF)**; after lever 1 the WF number (**~0.51–0.53**, raw-side, leakage-free) becomes the
honest *served* number, and levers 3–5 can then legitimately push toward **~0.55** on a clean forward holdout.
The single piece of evidence that would **kill** the thesis: a near-0% side-flip rate from the discriminating
experiment — in which case I would re-rank horizon (lever 3) as dominant. My one next experiment: the
side-flip-rate count above. (I was unable to run `honest_benchmark.py` — Binance egress blocked and the
workspace process-locked — so the empirical hit-rate remains the code-stated ~51–53%, explicitly unverified
by me this pass.)

## Changelog
- 2026-05-30 (pass 1, agent a2 "served-not-validated"): Verified the full lifecycle by code read.
  Anti-look-ahead, stationary features, label-venue match, WF machinery, and GBT regularisation all PASS —
  genuinely strong. Found the dominant defect: **calibration changes the bet side live/paper but is absent
  from WF/backtest**, so served≠validated (FAIL on Calibration and Train–serve skew); calibrator is also
  non-monotonic and partly self-fit on backfilled rows. Could NOT run honest_benchmark (Binance blocked +
  workspace locked) — honest number remains code-stated ~51–53%, unverified by me. Next run: execute the
  side-flip-rate experiment, then a real WF on reconstructed Coinbase data once the sandbox frees, and A/B
  horizon=1 vs 2.
