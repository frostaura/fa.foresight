# ML audit — fa.foresight (BTC up/down → Polymarket bot) — Wrong Objective (2026-05-30)

> **Independent report by one of three engineers reviewing this system in isolation.** I did not see
> the other two reviews. This is my own investigation and my own root-cause thesis.
>
> Scope: target = a single BTC candle's own-body direction (close>open) at horizon=2 on a chosen
> interval (1m/5m/15m); unit of observation = one closed candle's decision edge; metric the code
> reports = directional hit-rate; **metric that actually matters = profit/edge vs the Polymarket
> entry price**, since the bot stakes real money. Data structure = a single 24/7 time series, replayed
> causally. Method: full code read of BuiltInModels, ModelTrainer, WalkForwardEvaluator, BacktestRunner,
> Indicators, RegressionNodes, GradientBoostedTrees, CalibrationRescaler, LivePredictionService,
> StakingEngine, VenuePriceStore, MarketAlignmentEvaluator, HorizonSweepExperiment, Foresight5mV1PipelineTests.
> No training data is committed (klines are fetched from Binance at runtime and cached in Postgres), so
> honest_benchmark.py could not be run; I rely on the codebase's OWN logged measurements + deterministic
> EV math. Claims tagged (verified)=read in code, (inferred)=reasoned.
> Objective: raise real out-of-sample performance on the paid metric. Hardware ceiling: Raspberry Pi 5.

## Headline numbers

- **Honest directional accuracy is ~52–53% OOS at 5m, with no edge over coin-flip.** This is not my
  estimate — it is the project's own walk-forward measurement, stated verbatim in
  `HorizonSweepExperiment.cs:54` ("5m baseline from prior work: ~52-53% OOS, no exploitable edge over
  coin-flip") and the `BuiltInModels.cs` iteration log (iter-7 LOCKED: 1m 53.23%, 5m 53.16%, 15m 55.84%;
  iter-8 60%+ explicitly rejected as overfit on 72 bets). (verified)
- **The metric that is actually paid is edge over the entry price, and at the configured price that
  edge is NEGATIVE.** `VenuePriceStore.cs` prices BOTH sides at a fixed `EffectivePrice = 0.55`
  (`DefaultEffectivePrice = 0.55m`, used for YES and NO). Break-even win-rate when you pay 0.55 to win
  1.00 is exactly **55%**. A 53% model betting every candle at 0.55 has expected value per $1 staked of
  `0.53·(1/0.55) − 1 ≈ −3.6%` — it bleeds on every trade. (verified — price + odds math)
- **So the honest "number that matters" today is a losing strategy, not a 53% winner.** The accuracy
  metric the iteration loop optimizes is the wrong scoreboard: 53% looks like "almost a coin-flip,
  slightly positive," but against a 0.55 fill it is a guaranteed slow loss.
- **Current → expected on the paid metric:** current edge ≈ **−2 to −4% per trade** (53% @ 0.55) →
  expected **≈ break-even to small-positive** once the system (a) only bets when calibrated
  `sideProb > price + costs`, and (b) is graded/selected on edge, not accuracy. The accuracy number
  barely moves (53% → ~53–55%); the *profitability* moves from negative to ~0/positive by **betting far
  fewer, higher-conviction candles**. Confidence the objective-mismatch is the dominant lever: **62%**.

## Hypothesis (the bet I make)

The pipeline is honest and clean — there is no material leakage, the label is correct, and the ~53%
is real. The dominant problem is an **objective / decision mismatch**: the model and the entire
iteration loop are optimized and selected on **directional accuracy**, but the system is paid on
**edge versus a ~0.55 entry price**. On a near-50/50 target, accuracy and profit are different
scoreboards — 53% accuracy is *below* the 55% break-even, so chasing accuracy from 53→56% is really
just chasing the break-even line, and a model can have great accuracy and still lose money (or
mediocre accuracy and make money if it only bets when its calibrated probability genuinely exceeds
the price). The biggest real-money move is therefore NOT a fancier model — it is to (1) make `pUp`
calibrated, (2) compute `edge = calibratedSideProb − (price + cost)`, (3) bet only on positive edge,
and (4) measure and select every model on **realized edge / log-utility on a forward holdout**, never
on hit-rate. This reframes the whole effort: the wall at 53% accuracy is the wrong wall.

## The Good (brief)

- **Causal, per-slice feature computation — no normalization leak.** (verified) Every indicator
  (`Indicators.cs`: z-score, ATR%, EMA-spread, Bollinger position, returns) is computed on the trimmed
  slice `candles[0..i]` only, past-only, with no global scaler fit before the split. All features are
  stationary (returns/ratios/z-scores), so the model isn't memorizing price levels.
- **Label is correct and matches venue settlement.** (verified) `ModelTrainer.cs:120` and
  `LivePredictionService.ResolveMaturedAsync` both grade `close(target) > open(target)` — the candle's
  own body — which is exactly how Polymarket BTC up/down settles. No close-vs-prev-close mismatch.
- **Anti-lookahead is enforced consistently across train/backtest/live.** (verified) `TrainingSliceProvider`
  and `BacktestSliceProvider` are mirror implementations; off-tf (15m/1m) candles are clamped by
  CLOSE-time (`clampedEnd - offIntervalMs`) so a still-forming higher-tf bar can never leak; candle i+1
  is never a feature input.
- **Walk-forward is the right shape.** (verified) `WalkForwardEvaluator` retrains per fold, expanding
  window, with an embargo ≥ horizon (`EmbargoCandles=3`, `Max(3,horizon)`), reports in-sample−OOS gap
  as an overfit tripwire, Wilson CI, and folds-above-50%. `PassesGuards` already encodes a real honesty
  gate (CI lower bound > 0.5, ≥1000 bets, majority of folds > 50%, gap ≤ 3pp).
- **The team already polices its own overfitting.** (verified) iter-8 (60%+ on 72 bets) was correctly
  rejected as a statistically-indistinguishable sliver; the project explicitly separates raw hit-rate
  from base-rate drift in `HorizonSweepExperiment`. This is unusually disciplined.

## Improvements — ranked by expected honest lift on the metric that matters

### 1. Grade and select on EDGE, not accuracy; bet only positive-edge candles — current → expected: −3% EV/trade → ~0 to +2% EV/trade · impact: High · effort: S · confidence: 62%
The headline backtest metric is `HitRate` (`BacktestRunner.cs:313`). The paid quantity is
`edge = sideProb − price`. At the fixed 0.55 price, a 53% model is −EV on every candle, yet the
iteration loop reads 53% as "almost there." **Fix:** make the primary backtest/walk-forward output the
**realized per-trade edge and total P&L net of the 0.55 fill** (these are already computed —
`FinalBalance`, odds payoff in `StakingEngine.WinProfit`), and make `PassesGuards` gate on
*positive net edge with CI lower bound > 0*, not on a 0.60 hit-rate. Then route every bet through the
already-built `EdgeAwareKellyNode`, which correctly emits 0 when `fStar ≤ 0` (i.e. when
`winProb ≤ price`) — so a 53% model at 0.55 would simply **stop betting** instead of bleeding. The lift
is not in accuracy; it's in turning a guaranteed slow loss into ~break-even by abstaining on negative-edge
candles and only firing on the genuine high-conviction tail. **Acceptance gate:** on a forward holdout,
total P&L net of 0.55 fills ≥ 0 with bootstrap CI lower bound ≥ 0; bets-placed drops sharply (expect
betting only the top few % of |pUp−0.5|). **Pi-5 feasibility:** trivial — pure arithmetic, no model change.

### 2. Calibrate `pUp` and APPLY it at serving (the rescaler is dead code) — current → expected: miscalibrated probs → calibrated, so edge math is trustworthy · impact: High (enables #1) · effort: S/M · confidence: 70%
`CalibrationRescaler.cs` exists, buckets resolved predictions and interpolates an empirical hit-rate
map — but a repo-wide grep for `CalibrationRescaler|RescaleAsync|PUpCalibrated` returns **zero call
sites**. (verified) It is never invoked in backtest, walk-forward, or `LivePredictionService` — raw
`pUp` from the logistic/GBT sigmoid is stored and bet on directly. This is *train-serve consistent*
(raw both sides, so it's not skew) but it means the `edge = sideProb − price` decision in #1 is using
an **uncalibrated** probability. A logistic-regression sigmoid on a thin-edge target is typically
over-confident; betting positive-edge on an over-confident `pUp` will fire on candles that aren't
truly +EV. **Fix:** fit isotonic/Platt calibration **inside each walk-forward fold** (on a held-out
tail of the train block, never on OOS), persist the calibrator in `TrainedState`, and apply the SAME
map in `LivePredictionService.PredictViaFlowAsync` and `BacktestRunner` before the edge/stake decision.
Either wire the existing rescaler into both paths or replace it with an in-fold isotonic fit (the
current rescaler is fit on *live resolved* rows — a feedback loop decoupled from validation; prefer the
in-fold version). **Acceptance gate:** reliability curve + Brier on the OOS folds improves; the
positive-edge subset from #1 actually realizes ≥ its predicted edge. **Pi-5 feasibility:** trivial —
isotonic is a step function, microseconds to evaluate.

### 3. Negotiate the real entry-price assumption / model true fill + fees — current → expected: 0.55 fixed → realistic ~0.50–0.52 fill · impact: High · effort: M · confidence: 55%
The entire −EV verdict hinges on the 0.55 number, which `VenuePriceStore.cs` documents as a
*conservative* placeholder ("real fills are usually cheaper"). The break-even win-rate is *exactly the
price*: at 0.55 you need 55%; at 0.52 you need 52%; at 0.505 you need 50.5%. So whether a 53% model is
+EV or −EV is **entirely determined by the fill price**, and the project is currently assuming the most
pessimistic case. **Fix:** record actual Polymarket BTC up/down YES/NO quotes at decision time (the
`venue_market_prices` table and `GetEntryAsync` already support per-target observed prices — populate
them from the live order book instead of the 0.55 synthetic), then backtest against the *real* price
distribution. If real fills are ~0.51–0.52, a 53% model is already marginally +EV without any accuracy
gain. This is the single fastest way to learn whether the system is already profitable. **Acceptance
gate:** backtest P&L computed against logged real quotes (not the 0.55 synthetic) has CI lower bound
> 0. **Pi-5 feasibility:** trivial compute; needs a live-quote ingest (already partially present via
`PolymarketClobMarketInfoClient`).

### 4. Prove GBT (v2) out-of-sample before believing the "56–58%" projection — current → expected: unvalidated → measured · impact: Med · effort: S · confidenceः 60%
`model.gbt` (v2 flow) is wired and the trainer computes `gbtFoldAccs`, but the iteration log's
"non-linear could push to 56-58%" is a *projection*, not a measured walk-forward result, and v2 is not
the locked production model. (verified — BuiltInModels comment is hypothetical; HorizonSweepExperiment
uses GBT and still lands at ~52-53% at 5m). My read: on a 53% signal the GBT will NOT beat logistic
regression by enough to matter (the iteration logs show every feature-addition moving the needle <1pp),
and capacity on a thin edge is the iter-8 overfit trap. **Fix:** run the existing `WalkForwardEvaluator`
on v2 vs v1 head-to-head, gate on edge (#1), and only ship GBT if its OOS edge CI lower bound beats
LogReg's. **Pi-5 feasibility:** fits — a depth-3, 200-tree ensemble is ~200·(a few KB) JSON, sub-ms
inference per candle on ARM64 CPU (already runs in the C# `PredictProba` loop).

### 5. Lengthen the horizon as a SEPARATE profitable product, not as an accuracy hack — current → expected: explores real edge at 15m+ · impact: Med · effort: M · confidence: 45%
`HorizonSweepExperiment` already tests 15m/1h/4h/1d and the iteration log shows 15m hitting 55.84% OOS
on 1.9k bets (CI [53.6, 58.0]). At 15m, 55.8% accuracy *does* clear a 0.55 break-even with a real (if
thin) margin — this is the one interval where the honest number is plausibly +EV at the conservative
price. **Fix:** treat the longer-horizon market as the primary product if a matching Polymarket
instrument exists, and grade it on edge (#1). Caveat (verified, and the experiment flags it itself):
longer horizons inflate raw hit-rate via bull-market drift — the base-rate-adjusted edge is the honest
figure, not the raw 55.8%. **Pi-5 feasibility:** identical model, trivial.

(No validity-breaker is hidden: see the audit table. The closest thing to a leak — the live
`CalibrationRescaler` being fit on live resolved rows — is dead code, so it cannot inflate any
reported number. The only real concern is the off-tf close-time clamp, which I verified is correct.)

## Pipeline & system optimizations (my change set)

- **Decision/economic layer (the core bet):** replace `HitRate` as the headline with **net P&L /
  realized edge against the entry price** in `BacktestRunner` and `WalkForwardEvaluator.PassesGuards`;
  route stakes through `EdgeAwareKellyNode` so negative-edge candles auto-abstain. This is the change
  that flips the system from −EV to ~break-even.
- **Calibration:** fit isotonic per walk-forward fold on an in-fold holdout, persist in `TrainedState`,
  apply identically in `BacktestRunner` and `LivePredictionService`. Verify with reliability curve +
  Brier on OOS (already computed). Delete or rewire the live-feedback `CalibrationRescaler`.
- **Pricing realism:** populate `venue_market_prices` with real logged Polymarket quotes at decision
  time; backtest against the real price distribution, not the 0.55 synthetic.
- **Objective/loss (cheap real win):** the logistic node optimizes log-loss for *accuracy*; for the
  paid decision, additionally select the threshold/abstention band on a **cost-weighted utility**
  (expected log-bankroll growth under odds), not on |pUp−0.5|. Quarter-Kelly is already in
  `EdgeAwareKellyNode` — feed it calibrated probs and real prices.
- **Model family / ensembling:** before any deep net, run a **LogReg + GBT blend** (average the two
  calibrated `pUp`s — `MajorityVoteNode` exists but voting throws away probability; prefer a probability
  average) and measure edge. Sequence nets (small int8 GRU/TCN, N-BEATS) are a *last* move: on a
  documented ~53% single-series signal they will not manufacture edge that isn't there, and the Pi-5
  budget is better spent on calibration + decision layer. If tried, validate rolling-origin, past-only,
  int8 ONNX (<5 MB, sub-ms on ARM64) — but rank it LOW until #1–#3 are done.

## Leakage, future-bias, train–serve-skew & overfitting audit (mandatory)

| Stage | Verdict | Finding | Fix |
|---|---|---|---|
| Data ingestion / quality | PASS | Binance klines fetched, anchor is last CLOSED candle (`LivePredictionService:114`), forming bar excluded; no restated/revised history; dedup in experiment loader. | — |
| Feature engineering | PASS | All indicators computed per-slice on `candles[0..i]`, past-only, stationary (returns/ratios/z-scores); no global scaler fit pre-split; off-tf clamped by close-time. | — |
| Labeling | PASS | `close(target) > open(target)` — candle's own body, matches Polymarket settlement; label source candle (i+horizon) is never a feature input. | — |
| Splitting / validation | PASS | Walk-forward expanding window, embargo ≥ horizon, retrains per fold, in-sample−OOS gap reported, Wilson CI, folds-above-half. Single 80/20 was explicitly replaced. | — |
| Training | PASS | IRLS with ridge escalation; GBT seeded/deterministic; per-fold fit; no resampling-before-split. Minor: GBT `IsLeaf` returns true if EITHER child is null — harmless given both are set together, but brittle. | (nit) make `IsLeaf` check Feature<0. |
| Calibration / uncertainty | **FAIL** | `CalibrationRescaler` has **zero call sites** (dead code) — `pUp` is served raw. It IS train-serve consistent (raw both sides), so not skew, but probabilities are uncalibrated and the rescaler that exists is fit on a live feedback loop, decoupled from validation. | Fit isotonic in-fold, persist, apply in BOTH backtest and live. |
| Evaluation / metric honesty | **FAIL** | Headline metric is directional **hit-rate** on a near-50/50 target where the paid quantity is **edge vs a 0.55 price**. 53% accuracy reads as "fine" but is below the 55% break-even ⇒ −EV. The metric does not respect the cost. | Grade & select on net P&L / realized edge against the entry price; gate on positive-edge CI > 0. |
| Train–serve skew | PASS | Live `PredictViaFlowAsync` uses the same flow executor, same `RemapTrainedState`, same anti-lookahead historical adapter as backtest; raw `pUp` on both sides. (One forward gap: v1+ofx microstructure abstains live — correctly documented, not skew.) | — |

## Discriminating experiment (does my hypothesis win?)

**One cheap test, no future bias, runnable today:** take the locked v6/v1 model's existing walk-forward
OOS predictions and, instead of scoring hit-rate, compute **net P&L per $1 staked = winRate·(1/price) − 1**
at three prices — 0.55 (configured), 0.52, 0.505 — for (a) betting every candle and (b) betting only
the top 5–10% |pUp−0.5| candles. **Confirms my thesis if:** at 0.55 every-candle is clearly −EV
(≈ −3%) while the high-conviction subset is closer to or above 0; i.e. the lever is the decision/price
layer, not accuracy. **Refutes it if:** even the high-conviction subset is solidly +EV at 0.55 (then
the system is already profitable and the real lever is just sizing), OR the high-conviction subset is
*also* −EV at realistic prices (then the signal is genuinely absent and only a new data source — order
flow — can help, a signal thesis). This needs only the already-stored OOS predictions + arithmetic.

## Verdict, expected lift & confidence

The pipeline is **leakage-free and the 53% is real** — I corroborate the prior audit's leakage
conclusion independently (and note the prior "validated≠served calibration" risk resolves to "calibration
is simply not applied anywhere"). The dominant cause of the system not making money is **not a modeling
or data wall — it is an objective/decision mismatch**: optimizing directional accuracy while being paid
on edge over a ~0.55 fill, where 53% is below break-even. **Confidence this is the dominant lever: 62%**
(the main uncertainty is the real fill price — if it's ~0.51, the system is nearly profitable already and
my lever's headroom shrinks; that's exactly what experiment confirms). **Current → expected on the paid
metric:** ≈ **−3% EV/trade (53% @ 0.55, every candle) → ~0 to +2% EV/trade** via calibrate-then-bet-only-
positive-edge + grade-on-edge + realistic pricing — accuracy barely changes (53→~53–55%), profitability
flips. **The single piece of evidence that would kill the thesis:** the high-conviction subset is −EV
even at a realistic ~0.51 price (then signal is the wall, not the objective). **My one next experiment:**
the P&L-at-three-prices scoring above — small, no future bias, Pi-5-trivial.

## Changelog

- 2026-05-30 (pass 1, a3 wrong-objective): Independently re-verified the pipeline as leakage-free
  (features per-slice past-only, label = candle own body matching venue, walk-forward with embargo,
  train-serve consistent). Found the honest number is the project's own ~52–53% OOS at 5m (no edge over
  coin-flip), and that the **calibration rescaler is dead code (zero call sites)** and the **headline
  metric is accuracy on a target paid by edge vs a 0.55 fill — making 53% a −EV losing strategy**. Thesis:
  objective/decision mismatch, not a model/data wall. Top levers: grade & bet on edge (auto-abstain via
  the existing EdgeAwareKelly), calibrate-and-apply, log real Polymarket fills. Confidence 62%.
  Re-test next run: P&L-at-three-prices on stored OOS predictions; then v2-GBT-vs-v1 edge head-to-head.
