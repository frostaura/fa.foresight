# ML audit — fa.foresight (BTC up/down → Polymarket) — Signal Ceiling (2026-05-30)

> **Independent report by one of three engineers reviewing this system in isolation.** I did not see
> the other two reviews. This is my own investigation and my own root-cause thesis.
>
> Scope: target = direction of a BTC candle's own body (close > open) at horizon=2 (5m default);
> unit of observation = one 5m candle decision; metric that matters = real out-of-sample directional
> hit-rate (and Brier, since a probability drives the Polymarket bet); data structure = single
> time-series (one symbol, multi-timeframe inputs).
> Method: full code read of ModelTrainer.cs, GradientBoostedTrees.cs, WalkForwardEvaluator.cs,
> BacktestRunner.cs, RegressionNodes.cs, Indicators.cs, MatrixBuilderNode.cs, LivePredictionService.cs,
> BuiltInModels.cs; PLUS an independent reconstruction of the exact v1-5m feature+label pipeline in
> Python on 8,000 freshly-fetched Binance BTCUSDT 5m candles (+15m/+1m off-tf), run through an honest
> walk-forward / forward-holdout / label-shuffle / gated-subset benchmark.
> Claims tagged (verified) = read in code/measured in data, (inferred) = reasoned.
> Objective: raise real out-of-sample directional hit-rate. Hardware ceiling: must run on a Raspberry Pi 5.

## Headline numbers
The honest, leakage-free out-of-sample number on the active feature set is **~0.50–0.52 directional
accuracy, AUC ~0.51–0.52** — statistically a coin flip with a sliver of edge. Measured by me
independently (verified):

| Setup (my reconstruction, 5-fold expanding walk-forward, 2,395 clean samples) | OOS acc | AUC | Brier | in-sample | overfit gap |
|---|---|---|---|---|---|
| Logistic regression (raw features) | 0.5223 | 0.505 | 0.257 | 0.538 | +0.015 |
| Logistic regression (standardized) | 0.5078 | 0.514 | 0.265 | 0.562 | +0.054 |
| GBT (their hyper-params: 150×depth-3, lr .04, leaf 200) | 0.4962 | 0.497 | 0.252 | 0.614 | **+0.118** |
| Single forward holdout (last 20%), LogReg | 0.5052 | 0.525 | 0.252 | — | — |
| **Label-shuffle control** | 0.4937 | 0.499 | — | — | — |

Trivial baseline to beat: majority/always-up = **0.507** (base rate up = 0.493). The models barely
clear it and the **GBT does not** — its +0.118 in-sample−OOS gap is pure memorization of noise.

The code's own embedded iteration log claims iter-7 "locked" at 53.2% (5m) and gated subsets reaching
55–56%. **That gated lift does not replicate on clean data** — my gated walk-forward moves 0.508 → 0.510
at most across bands 0.04–0.20. The headline 53–56% in the comments is a mix of (a) sampling noise on
shrinking confident subsets (their own iter-8 "60% on 72 bets, rejected" is the tell) and (b) odds/
staking effects in the betting backtest that are not directional accuracy. **The honest directional
number is ~0.51, not 0.53–0.56.**

Expected post-fix: with the label/horizon + decision-layer change set below, current 0.51 → **~0.53–0.54
honest, on the subset the system actually bets**, confidence Medium. No model swap reaches it; only
changing *what is predicted* and *which candles are bet* does.

## Hypothesis (the bet I make)
The dominant root cause is **signal ceiling under the current label/horizon/feature regime, not leakage
and not the model family.** The pipeline is genuinely leakage-free (I confirmed: label-shuffle collapses
to 0.494, forward holdout ≈ random-CV, off-tf is close-time-clamped, no global scaler). The feature set
(lagged returns + ATR-normalized TA + volume z + session + 15m regime + 1m subbar) carries **AUC ≈ 0.52**
— almost no rank information about a near-50/50 target. Therefore the biggest honest move is **not** a
fancier estimator (GBT already overfits this data); it is (1) predicting a target with more extractable
edge (horizon=1, and a magnitude-conditioned label that drops the coin-flip flat candles), (2) adding a
**calibration + decision layer** so the system bets only where probability and odds give positive
expected value, and (3) feeding genuinely new information (order-flow/cross-asset) rather than more
transforms of the same OHLCV.

## The Good (brief)
- **Leakage discipline is excellent and verified.** TrainingSliceProvider and BacktestSliceProvider are
  byte-for-byte mirror logic; off-tf candles are clamped by CLOSE-time (`clampedEnd - offIntervalMs`),
  so a still-forming higher-tf bar cannot leak. Candle i+1 is never a feature input. (verified)
- **Train–serve parity is real.** Live `PredictViaFlowAsync`, backtest `RunAsync`, and the trainer all
  go through the same `IFlowExecutor`, the same nodes, and the same `RemapTrainedState`. No separate
  online feature path to skew. (verified)
- **Label matches the venue.** `yDir = close(target) > open(target)` is exactly Polymarket's
  close-vs-open settlement, and resolution in `ResolveMaturedAsync` grades identically. (verified)
- **Honest measurement scaffolding exists.** WalkForwardEvaluator does per-fold retrain + embargo
  (≥ horizon), reports Wilson CI, in-sample−OOS overfit gap, and folds-above-50% — and `PassesGuards`
  refuses a sub-1000-bet or CI-touching-0.5 result. The team built the right honesty gate. (verified)
- **GBT is competently regularized** (shallow depth, large min-leaf, row/col subsample, L2 shrink,
  seeded/deterministic) — the right defenses for a thin-edge target. (verified)

## Improvements — ranked by expected honest lift on the metric that matters

### 1. Magnitude-conditioned label + horizon=1 — current 0.508 → expected ~0.52–0.53 · impact: High · effort: S · confidence: 60%
The own-body H2 target is the flattest possible: half the candles have a near-zero body that is pure
noise. (verified) In my reconstruction, restricting to candles whose move ≥ median body lifts OOS acc
0.508 → **0.523** and horizon=1 alone beats H2 (0.514 vs 0.508 acc, 0.518 vs 0.514 AUC). Fix: train and
*decide* on horizon=1 (now that the decision is an instant deterministic compute — the code already
supports it), and add a "no-bet on predicted-flat" rule driven by a predicted-|move| head, not the
hardcoded 0.5 threshold. Acceptance gate: walk-forward OOS hit-rate on the **bet** subset > 0.52 with
Wilson CI lower bound > 0.50 on ≥ 1000 bets (their own PassesGuards). Pi-5: trivial — same LR/GBT,
no size change.

### 2. Probability calibration + EV decision layer (the real missing piece) — Brier 0.257 → ~0.249, turns hit-rate into profit · impact: High · effort: M · confidence: 55%
**There is no calibration anywhere** (no Platt/isotonic; raw sigmoid/GBT prob goes straight to a 0.5
threshold in both backtest and live). (verified) For a Polymarket bet the quantity that pays is
`p_up * payout_up − cost` vs the *quoted odds*, not accuracy at 0.5. Fix: fit isotonic/Platt on a
held-out in-fold slice, apply it identically at serving (the parity machinery already exists), and
replace the 0.5 threshold with an **EV gate against the venue's YES/NO price** (the entry quote is
already fetched in BacktestRunner). This is the cheapest genuine win: a worse-accuracy model that is
well-calibrated and only bets positive-EV candles makes more money than a 0.52 model betting everything.
Acceptance gate: reliability curve + Brier improvement on forward holdout AND positive cost-weighted
PnL on the OOS folds. Pi-5: trivial — isotonic is a step function, microseconds.

### 3. New information sources, not new transforms — expected +0.01–0.02 AUC if any source has edge · impact: Med-High · effort: M-L · confidence: 40%
Every current feature is a transform of the same BTCUSDT OHLCV; AUC ≈ 0.52 says that well is nearly dry.
(verified) The genuinely new, Pi-5-cheap sources: real **order-flow imbalance** (trade-level
buy/sell pressure — the `MicrostructureBar` plumbing already exists but the active v1 flow uses only a
1m-candle proxy), **funding rate / perp basis**, and **cross-asset** (ETH lead-lag, DXY). These are the
only things that can raise the ceiling; more EMAs cannot. Acceptance gate: drop-one-source ablation must
show the new source adds OOS AUC on a forward holdout, not just in-sample. Pi-5: feature fetch is I/O;
model unchanged. Note: live microstructure breaks backtestability — gate it behind a
backtestable proxy + forward-only validation.

### 4. (Disclosed, ranked low) Do NOT swap to a bigger model yet. GBT already overfits (gap +0.118) and ties LR on OOS. LSTM/TCN/N-BEATS on AUC-0.52 features will overfit harder.
A small sequence net (LSTM/GRU, int8 ONNX) is on the menu and **fits a Pi 5** (a 1–2 layer, 32–64-unit
GRU quantizes to <1 MB, sub-ms CPU inference), but it is the *wrong* lever here: there is no temporal
structure for it to find that the lagged-return features don't already expose, and my GBT result shows
the data punishes added capacity. Revisit only after levers 1–3 raise the feature AUC above ~0.55.
Pi-5: feasible (int8 GRU <1 MB, <1 ms) — but out on evidence, not on hardware.

## Pipeline & system optimizations (my change set)
- **Labeling:** move the production decision to horizon=1; add a second regression head predicting
  |move| (or 3-class up/flat/down) so "flat" candles are abstained on by signal, not by a fixed band.
- **Calibration:** insert an in-fold isotonic calibrator into the trainer's TrainedState; have the
  prediction node apply it; validate with reliability + Brier. (Currently absent — biggest structural gap.)
- **Decision layer:** replace the `pUp >= 0.5` side rule and the static ±band gate with an EV test
  `calibrated_p * (1/yesPrice − 1) − (1 − calibrated_p) > margin` using the already-fetched entry quote;
  size with the existing Kelly sizer off the *calibrated* edge.
- **Validation reporting:** make the WalkForwardEvaluator's OOS directional hit-rate (not the betting
  hit-rate, which is confounded by odds/staking) the number of record stamped on the model, and surface
  AUC + Brier alongside it. The embedded comment numbers (53–56%) should be deleted or re-derived — they
  are misleading as written.
- **Objective/loss:** keep logistic loss but add class weighting only if a magnitude-filtered label
  creates imbalance; otherwise leave it.

## Leakage, future-bias, train–serve-skew & overfitting audit (mandatory)
| Stage | Verdict | Finding | Fix |
|---|---|---|---|
| Data ingestion / quality | PASS | Binance klines; anchor is last CLOSED candle, forming bar excluded; off-tf clamped by close-time. No restated history. (verified) | — |
| Feature engineering | PASS | All features stationary (returns/ratios/z-scores); no global scaler fit pre-split (models use raw features); no shift(-n)/centered windows; off-tf close-time-gated. (verified) | Optional: standardize inside the fold if calibration needs it (doesn't change leak status). |
| Labeling | WEAK | `close>open` is correct vs venue but the flattest target — half the candles are coin-flip bodies, capping AUC ≈ 0.52. (verified) | Horizon=1 + magnitude-conditioned/3-class label (lever 1). |
| Splitting / validation | PASS | Expanding-window WF with embargo ≥ horizon; test touched once per fold; Wilson CI + overfit gap reported. Trainer's internal WF also clean. (verified) | Make OOS *directional* acc (not betting hit-rate) the stamped number. |
| Training | PASS | Per-fold refit; seeded GBT; ridge-escalating IRLS; final model on full window after WF measures the procedure. (verified) | — |
| Calibration / uncertainty | **FAIL** | No calibration step exists anywhere; raw probability → hardcoded 0.5 threshold, in backtest AND live. For a probability-driven bet this is the key structural gap. (verified) | In-fold isotonic/Platt, applied at serving (lever 2). |
| Evaluation / metric honesty | WEAK | Accuracy-at-0.5 on a near-50/50 target is the wrong headline; betting hit-rate confounds odds/staking with directional skill; embedded 53–56% comments don't replicate (sampling noise on confident subsets). (verified) | Report AUC + Brier + cost-weighted PnL; treat directional hit-rate with CI as the metric; delete/redo the comment numbers. |
| Train–serve skew | PASS | Identical executor/nodes/remap across train, backtest, live; same close-vs-open grading. (verified) | Keep calibrator on the same shared path when added. |

## Discriminating experiment (does my hypothesis win?)
**Cheapest test:** on a fresh forward holdout, compute OOS AUC of the active feature set against the
own-body H2 label, then against (a) horizon=1 and (b) the magnitude-filtered label. My thesis predicts
**all three stay in AUC ~0.51–0.53** (signal ceiling), with the magnitude/horizon-1 variants modestly
higher — and that **adding one new information source (order-flow imbalance) is the only thing that moves
AUC past ~0.54**. Confirm: if a model swap or more TA features fail to beat AUC 0.53 but a calibrated EV
decision layer turns the same 0.52 model PnL-positive, my thesis (signal ceiling, fix via label+decision,
not machinery) is correct. Refute: if any pure model/feature-transform change pushes honest OOS AUC > 0.56
on a forward holdout, the cause is machinery/feature-engineering, not a signal ceiling.

## Verdict, expected lift & confidence
**Verdict (confidence ~60% it's the dominant cause):** the system is honestly built and honestly
~0.51 OOS; the plateau is a genuine signal ceiling at this label/horizon/feature regime, and the single
biggest *bankable* improvement is not accuracy at all but a **calibration + EV decision layer** on a
**horizon-1, magnitude-conditioned label**, which converts a 0.52 coin into positive expected value on
the candles it chooses to bet. Current honest 0.51 → expected **~0.53–0.54 on the bet subset**, with the
real money coming from betting fewer, positive-EV candles rather than from raw accuracy.
Single piece of evidence that would kill the thesis: any pure model-family/feature change reaching honest
OOS AUC > 0.56 on a forward holdout. One next experiment: implement in-fold isotonic + EV gate and measure
cost-weighted OOS PnL vs the current bet-every-candle baseline.

## Changelog
- 2026-05-30 (pass 1, a1): Independently reconstructed the v1-5m pipeline on 8k fresh Binance 5m candles;
  measured honest OOS acc ~0.50–0.52, AUC ~0.52, label-shuffle 0.494 (no leak), GBT overfit gap +0.118.
  Confirmed leakage-free + train-serve parity by code read. Found NO calibration step (FAIL) and that the
  embedded 53–56% iteration numbers do not replicate (sampling noise / odds confound). Thesis: signal
  ceiling; fix via horizon-1 + magnitude-conditioned label + isotonic calibration + EV decision layer,
  not a model swap. Re-test next run: does an EV decision layer on a calibrated 0.52 model produce
  positive cost-weighted OOS PnL, and does order-flow imbalance lift OOS AUC past 0.54.
