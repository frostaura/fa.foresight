# ML audit resolution — fa.foresight (2026-05-30)

What was implemented in response to the three independent ML-engineer audits
(`ml-audit-2026-05-30-a1-signal-ceiling.md`, `-a2-served-not-validated.md`,
`-a3-wrong-objective.md`). The three reports were read against the actual code first; where they
disagreed, the code was ground-truthed and the factually-correct reviewer followed.

## Ground-truth: resolving the a2 ↔ a3 contradiction

a2 and a3 directly contradicted each other on calibration. **a2 was correct, a3 was wrong:**

- a3 claimed `CalibrationRescaler` is dead code with "zero call sites." **False.** It is wired into
  live (`LiveSessionEngine.cs:422`) and paper (`PaperTradingService.cs:468`), and the bet **side** was
  decided on the *calibrated* probability there.
- a2's "served ≠ validated" thesis was therefore correct and verified: the backtest / walk-forward
  decide the side on **raw** `pUp` (`BacktestRunner.cs:238`, `PUpCalibrated = null`), while live + paper
  decided it on **calibrated** `pUp`. Because the rescaler is non-monotonic and can cross 0.5, the
  served bet set could diverge from the validated bet set — every honest WF number described a model
  that was not being run.

## Changes shipped (all three reviewers converge on these)

### 1. EV gate — bet only positive-EV candles (a1 §2, a2 §4, a3 §1) — the bleed fix

The system is paid on **edge vs the entry price**, not accuracy at 0.5. Against the fixed conservative
fee (≈0.55) a ~53% model betting every candle is −EV on every trade (`0.53·(1/0.55)−1 ≈ −3.6%`). The
machinery already existed (`EdgeAwareKellyStakingStrategy` returns 0 when `fStar ≤ 0`) but was not
applied as a universal gate, and the default sizing path bet every candle.

- Added `StakingEngine.HasPositiveEdge(...)` + `StakingEngine.DefaultMinEdge` (= 0, pure break-even):
  a bet is placed only when the chosen side's win probability strictly exceeds the price it pays
  (plus an optional cushion). For a $1-payout binary, EV per $1 = `winProb/price − 1`, positive iff
  `winProb > price`.
- **Paper + live now abstain on every non-+EV candle** (`PaperTradingService`, `LiveSessionEngine`).
  Expect bets-placed to drop sharply — only the high-conviction tail (chosen-side prob > the fee) is
  bet. That is the intended, honest behaviour: betting −EV is strictly worse than abstaining.
- `BacktestRunner.RunAsync` gained an opt-in `evGateMargin` parameter so a measurement run can mirror
  the served behaviour. Left **off by default** so the walk-forward skill measurement and the
  flat-staking bust sweep keep betting every candle (they measure directional skill / tail risk, not
  served PnL).

### 2. Served == Validated — side off raw pUp in live + paper (a2 §1) — confidence 80%

`LiveSessionEngine` and `PaperTradingService` now decide the bet **side, the confidence gate, and the
sizing inputs all on the raw model probability** (`pred.DirectionUpProbability`) — the exact quantity
the walk-forward / backtest validate on. The walk-forward side-accuracy is now, by construction, the
served side-accuracy. The calibrated probability is still computed and persisted to the bet notes for
telemetry, but it **no longer drives the bet** (chosen a2's "cheapest & safest" option (a)).

### 3. Calibration hygiene (a2 §2) — honest telemetry, side-preserving

Even as telemetry, the rescaler is now trustworthy and ready for a future in-fold sizing use:

- **Monotonic (isotonic / pool-adjacent-violators):** a higher predicted up-probability can never map
  to a lower empirical up-rate, so the map can no longer cross 0.5 on noise.
- **Backfilled rows excluded:** rows whose `PromptTraceJson` carries `"backfilled":true` are leakage-
  free historical replays, not genuinely-live calls; mixing them fit the calibrator partly on its own
  past output (the self-fit hazard). They are now filtered out of the fit.

### 4. Whole-dollar / min-$1 venue compliance (Polymarket places whole-dollar stakes)

The EV-gated `kelly-edge` strategy sized in **cents** ($27.78) — un-placeable on Polymarket, which
takes whole-dollar stakes, min $1. Fixed at the placement chokepoint, not in the Kelly math:

- `StakingEngine.QuantizeToWholeDollars` — **floors** a sized stake to a whole dollar (never stakes
  MORE than the strategy/Kelly prescribed) and returns 0 (no-bet) below $1.
- Applied in **paper** (after sizing) and **live** (after the per-trade cap, so a fractional cap can't
  reintroduce cents); sub-$1 is a clean abstain, not a bust.
- The built-in Kelly strategies (`kelly`, `kelly-edge`) stay **continuous** by design — they mirror the
  bare `EdgeAwareKellyNode`. Whole-dollar rounding is a separate concern: the placement chokepoint for
  built-in strategies, and the `clamp_round` node for DAG strategies. (Backtest is left continuous — it
  measures directional skill / strategy PnL; paper is the faithful whole-dollar dry-run of live.)
- **Interpretation:** "whole dollars" = the USD stake/notional is an integer ≥ $1. Shares
  (`stake / price`) remain fractional. If the venue actually requires whole *shares* or a different
  minimum, that is a different quantization — flag and it's a one-line change.

## Tests added

- `StakingEngineOddsTests` — `HasPositiveEdge` (both overloads, side selection, margin, degenerate
  prices); `DefaultMinEdge` is break-even.
- `CalibrationMonotonicTests` — PAVA correctness (already-monotonic untouched, single dip pooled, fully
  decreasing collapses to the mean, null bins skipped, < 2 bins is a no-op).
- `Foresight5mV1PipelineTests.Ev_gate_abstains_on_non_positive_edge_candles` — end-to-end: against a
  fixed 0.55 fee, the gated run places strictly fewer bets than bet-every-candle, and an unreachable
  margin places none.
- `StakingEngineOddsTests.QuantizeToWholeDollars_floors_and_skips_below_one` — floors to whole dollars,
  abstains below $1.

Full backend suite (run **in the worktree**): **262 passed, 0 failed**.

## Deferred — recommended follow-ups (reviewers themselves rate these lower-confidence / "A/B it")

- **Horizon = 1** (a1 §1, a2 §3): predict the freshest candle. Backtest `horizonSteps` and the live
  prediction horizon are offset by one in naming (`horizonSteps == liveHorizon + 1`); flipping the
  global default couples to resolution, idempotency keys, and trained models, and a2 explicitly says
  "A/B against horizon=2." Wire it as an A/B arm rather than a unilateral flip.
- **WF / user-backtest economic gate** (a1 §2, a2 §4, a3 §1): surface OOS cost-weighted PnL / realized
  edge as a reported metric and add it to `PassesGuards`, keeping hit-rate + Brier + Wilson CI as
  guards. (The `evGateMargin` hook in `BacktestRunner` is the wiring point.)
- **In-fold isotonic calibration persisted in `TrainedState`** (a1 §2, a3 §2): fit per walk-forward
  fold on a held-out tail, apply identically in backtest and live — this is what would let calibration
  safely re-enter the *sizing* decision (not the side).
- **Realistic fill price** (a3 §3): log real Polymarket YES/NO quotes at decision time into
  `venue_market_prices` instead of the synthetic 0.55. Whether a 53% model is +EV is *entirely*
  determined by the fill; the EV gate above is gated on whatever price is supplied, so cheaper real
  fills directly widen the +EV candle set.
- **Magnitude-conditioned label / horizon-1 target** (a1 §1): drop the coin-flip flat candles. Larger
  training-side change, 60% confidence.

## Changelog

- 2026-05-30: Shipped EV gate (paper+live abstain on non-+EV candles; opt-in in backtest), served==
  validated (side/gate/sizing off raw pUp in live+paper), calibration monotonicity + backfilled-row
  exclusion. Ground-truthed that a3's "calibration is dead code" claim was false and a2's served≠
  validated defect was real. 238 tests green.
