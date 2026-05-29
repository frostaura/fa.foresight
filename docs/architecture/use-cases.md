# Use Cases

> Each use case names actor, trigger, flow, and the acceptance test that proves it. Changing any of these is a "use-case change" and triggers integration + manual-regression gates (constitution §5).

## UC-1 Author a model (dual view)
**Actor:** Dean. **Flow:** create/edit a model as a node-graph in the visual designer or its code/DSL view; the two round-trip losslessly; validate (acyclic, typed ports, one prediction output). **Accept:** a model authored in code view renders identically in the designer and vice-versa; invalid graphs are rejected with a reason.

## UC-2 Author a strategy (dual view)
**Actor:** Dean. **Flow:** same authoring substrate as models; a strategy graph maps `(prediction, market odds, bankroll, history) → sized bet`. Built-ins (flat, martingale, fixed-fraction kelly, whole-dollar kelly, **edge-aware true Kelly**) ship as definitions. **Accept:** a built-in and a custom strategy both produce identical sizes in step-through and backtest.

## UC-3 Step through a graph (notebook runner)
**Actor:** Dean. **Flow:** execute the graph node-by-node on real inputs, inspecting each node's output, including executable (Python) nodes. **Accept:** stepping produces the same per-node values the batch runner produces for the same inputs.

## UC-4 Run a backtest
**Actor:** Dean. **Flow:** pick model × strategy × symbol × interval × window + starting balance/bet; replay over historical candles using **real venue odds** for payoff; produce hit-rate, P&L, drawdown, ledger. **Accept:** a bet's P&L equals the hand-computed odds-based payoff; results reproducible bit-for-bit.

## UC-5 Run the chaos / bust test
**Actor:** Dean. **Flow:** for a matrix of model × strategy, sample N random-start windows (configurable) at a fixed length + optional length sweep across all history; strict bust halts a window at balance ≤ 0. **Accept:** report per-combo bust-rate, worst drawdown, profit distribution; a known ruinous combo fails and a ruin-resistant one passes.

## UC-6 Run a paper session
**Actor:** Dean. **Flow:** start a session (venue, symbol, interval, model, strategy, starting balance, initial bet, gate); the processor bets each candle and settles on close against real odds; ledger + balance stream live; bust ends it. **Accept:** multiple concurrent paper sessions; identical config rejected by config-hash; ledger matches the engine.

## UC-7 Run a live session (automated, real money)
**Actor:** Dean. **Flow:** same as UC-6 but routed through `IExecutionProvider` (Polymarket CLOB V2) after `/golive` arming; starting the session **reserves** its bankroll from free account balance; orders sized in whole dollars; settlement reconciled against the wallet. **Accept:** a supervised **$1 order** fills and reconciles; reservation math holds; disarmed = no live order placed.

## UC-8 Configure venue & symbol
**Actor:** Dean. **Flow:** select the active venue (Polymarket default); UI/session options are driven by the venue's capability matrix (symbols → timeframes). **Accept:** a second (stub) venue registers and appears without touching core code.

## UC-9 Status overview
**Actor:** Dean. **Flow:** one surface summarizing all live + paper sessions — overlaid balance curves, hit/miss, per-side (live vs paper) totals. **Accept:** every active session appears with correct balance/P&L; charts open well-framed and go fullscreen.
