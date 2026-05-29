# System Components

## Backend projects (`src/backend`)
- **Foresight.Domain** — pure model: entities, value objects, the trading core (side/payoff/staking/bust), strategy interface + built-in strategies, the capability-matrix type, port interfaces. No dependencies.
- **Foresight.Application** — use cases & orchestration: forecasting, position sizing, session lifecycle, the flow engine (node-graph execution + validation), the step-through runner, the backtest runner, the chaos/bust harness, the reservation ledger. Depends on Domain only.
- **Foresight.Infrastructure** — adapters: venue integrations (Polymarket market-data + CLOB V2 execution), Binance candle/microstructure data, the EF Core `DbContext` + PostgreSQL, the EOA `IKeyVault` signer, the deterministic executable-node sandbox (Python sidecar), channel/notification adapters.
- **Foresight.Api** — ASP.NET Core host: REST endpoints, SSE streams (live predictions, sessions, backtest progress), and the **`/mcp`** Model Context Protocol surface mirroring the API. Composition root (DI).

## Ports (Domain interfaces) → bundled adapter
| Port | Responsibility | Bundled adapter |
|---|---|---|
| `IMarketDataProvider` | candles, current venue odds, historical odds, resolution status; advertises capability matrix | Polymarket + Binance |
| `IExecutionProvider` | place/cancel order, order state, positions | Polymarket CLOB V2 (EOA) |
| `IKeyVault` | EIP-712 signing, public address | local EOA (Nethereum) |
| `INodeRuntime` | execute a node body deterministically (vectorized over a series) | sandboxed Python sidecar |
| `IClock` / `IRandom` | injected time/seeded RNG (determinism) | system / seeded |
| `INotificationChannel` | push notifications & control | Telegram/Discord (later) |

## Venue integration (first-class, pluggable)
A `VenueIntegration` bundles an `IMarketDataProvider` + `IExecutionProvider` + a `CapabilityMatrix` (symbol → supported timeframes) + venue config (contracts, fee model, tick/min-size). The active venue is config-selected; **Polymarket** is the default. Symbols and timeframes shown in the UI/session config are driven by the active venue's matrix — never hard-coded. Adding Kalshi/Manifold = new `VenueIntegration` registration, no core change.

## Background services (Application, hosted in Api)
- **Live prediction loop** — per active (venue,symbol,interval) demand, generate the next-candle prediction, stream via SSE, resolve against actuals.
- **Session processor** — for each active paper/live session, place a bet at each candle boundary and settle on close; live sessions route through `IExecutionProvider` + reservation ledger; bust + circuit-breaker enforced.
- **Backtest / chaos workers** — run requested backtests and chaos/bust sweeps off the request path, stream progress.

## Data store (PostgreSQL / EF Core)
Tenanted tables: models, strategies (catalogue + authored), active-model selections, sessions, bets, bankroll/reservation ledger, predictions (+resolution), backtests (+bets), chaos runs (+window results), historical candles, venue odds history. Detail in [class-diagrams](class-diagrams.md).
