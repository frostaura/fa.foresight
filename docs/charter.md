# fa.foresight — Project Charter

> Part of FrostAura Technologies. Graduated from FrostAura Labs → Technologies on 2026-05-29.

---

## Positioning

FrostAura Foresight is a **lean, automated directional-trading platform** that operates on information markets. It is a cash engine, not a research dashboard. The product generates capital by running deterministic ML models against real Polymarket odds, sizing bets with edge-aware strategies, and executing automatically on the Polymarket CLOB V2. It earns its place in Technologies by meeting the "generates meaningful capital" criterion.

The platform is explicitly carved out of the FrostAura gambling red line under the prediction-market / event-futures exception (`docs/governance/red-lines.md` §1, revised 2026-05-06). It must be positioned as an information market trading system, not a betting product.

---

## Mission

Prove and deploy an automated trading edge on Polymarket's BTC up/down markets — first in paper simulation, then live — generating consistent positive returns within reservation and guardrail limits.

---

## What it owns

- **Directional-trading engine:** deterministic model pipeline (logistic v6/5m-v1 family, GBT v2) → calibration rescaling → strategy layer → bet execution.
- **Strategy library:** flat, martingale, fixed-fraction Kelly, whole-dollar Kelly (kelly-d1), and edge-aware true Kelly.
- **Dual-view DAG authoring surface:** design view ↔ code view for both models and strategies; round-trip lossless serialization.
- **Python sandbox sidecar:** isolated, deterministic execution environment for code nodes (no network, no filesystem writes, no ambient RNG/clock).
- **Session engine:** paper and live sessions, config-hash dedup, per-session bankroll reservation, bust detection.
- **Chaos/bust test engine:** random-window sampling, model × strategy matrix, bust-rate and profit distribution aggregates.
- **Polymarket CLOB V2 adapter:** venue market data, historical odds, order placement/tracking/settlement.
- **Reservation ledger:** invariant accounting of free balance vs active session reservations.

---

## Success criteria

| Gate | Criterion |
|------|-----------|
| Phase 1 | At least one model + strategy combo survives every window in a chaos/bust sample (bust rate = 0); median profit positive. |
| Phase 2 | Supervised $1 live order fills and settles correctly on Polymarket. Automated live sessions run within reservation + guardrail limits without intervention. |
| Ongoing | Paper session rolling 30-day P&L positive. Edge-aware Kelly measurably outperforms flat over equivalent windows. |

---

## Lean team

Consistent with FrostAura talent philosophy (small, elite, senior-first):

| Role | Responsibility |
|------|---------------|
| Owner / Lead Engineer (Dean) | Product direction, architecture sign-off, live trading arm decision |
| Backend Engineer (to be assigned) | .NET 10 domain, ports, adapters, session + trading engine |
| Frontend Engineer (to be assigned) | React 19 DAG authoring UI, session dashboard, full-screen charts |
| ML / Strategy Engineer (to be assigned) | Model R&D, calibration, walk-forward evaluation, chaos engine |
| Sandbox / Infrastructure (to be assigned) | Python sidecar, purity validation, CI, Portainer |

---

## Primary interfaces

- **FrostAura Technologies** — shared tech stack, Portainer deploy infrastructure, brand tokens.
- **Polymarket** — external venue (CLOB V2 data + execution APIs, on-chain Polygon contracts).
- **Binance** — historical OHLCV + microstructure candle provider.
- **FrostAura Labs** (prior home) — reference codebase mined for keepers; no runtime coupling.

---

## Strategic priorities (ordered)

1. **Phase 1: prove the edge.** Get the chaos/bust test to a passing verdict. Without this, nothing else matters.
2. **Phase 2: deploy it live.** Supervised $1 order → guardrail-bounded automation.
3. **Capital performance over feature breadth.** Scope is tightly capped; every addition must pass the "makes the trader win" test.
4. **Purity and reproducibility.** The determinism contract for code nodes is non-negotiable — it is the foundation of the chaos/bust test and live/paper parity.
5. **Prediction/settlement alignment.** Model predicts a Binance candle; Polymarket settles on its own window and reference feed. Pin the exact mapping before trusting any backtest result.
6. **Polymarket CLOB V2 correctness.** V1 was decommissioned 2026-04-28. Verify wire facts against live docs at build time; the $1 supervised order is the acceptance gate before automation is armed.
