# fa.foresight — Go-Live Readiness Report

**Date:** 2026-05-29 · **Branch:** `feat/mvp-greenfield-rewrite` · **Status:** READY for the supervised $1 validation order (human step). Live trading remains **disarmed** (`Polymarket__LiveTrading=false`).

This report is the output of a read-only audit of the live-execution path, the offline signing suite, a runtime check of the arming API, and the fixes that followed. It deliberately stops at the human $1 order — funding a wallet, supplying keys, and confirming the first real fill are yours to perform (see `live-setup.md`).

---

## Verdict

The path from prediction → sizing → guardrails → CLOB order is implemented, gated by four independent safety layers, and covered by an offline signing suite that round-trips against Nethereum. Three audit findings were fixed (one HIGH). **171/171 backend tests pass.** The remaining unknowns are inherent to a real exchange and can only be closed by the $1 order itself — they are called out below so the first live order is treated as the validation event it is.

---

## What is verified (offline / shadow)

**Four-layer kill chain before any pUSD moves** — all four must pass:
1. **Config gate** — `Polymarket__LiveTrading=false` registers `NullExecutionProvider` (shadow-logs, never touches the network). Real provider only with `=true` **and** a wallet key.
2. **DI gate** — provider selection happens once at startup.
3. **Early-exit arm check** — `LiveSessionEngine.ProcessAsync` skips the whole tick for a live session when the arm is not confirmed.
4. **Placement arm check** — the order call is wrapped in a second `IsArmed` guard; otherwise it logs `[SHADOW/DISARMED] Would place live bet`.

**Arming API (runtime-verified this session):** `request-code` → 6-digit code (5-min TTL, explicit money warning) → `confirm` arms → wrong code rejected (400) → `killswitch` disarms. State is per-tenant and in-memory (a restart safely disarms).

**CLOB V2 wire facts (code == docs == spec):**
- EIP-712 domain `name="Polymarket CTF Exchange"`, `version="2"`, `chainId=137`, `verifyingContract` routed per-market by neg-risk.
- V2 order struct (11 fields); V1 fields (`taker`, `expiration`, `nonce`, `feeRateBps`) confirmed absent by test.
- Amount scaling: BUY/SELL floor-to-6dp + tick rounding (golden-vector tested).
- L2 HMAC keeps `=` padding (the V1 gotcha) — golden-vector tested.
- L1 `ClobAuth` derive-api-key — EIP-712 ecrecover round-trip tested.
- Contract addresses (CTF Exchange, NegRisk Exchange, CTF, pUSD, onramp) match across code and docs.

**Now implemented (were deferred):** on-chain pUSD `balanceOf` via Polygon `eth_call`; real CLOB market resolution as the primary settlement path; SELL adapter; 60s reconciliation background service.

**Guardrails enforced around placement:** `MaxPerTradeUsd` (caps stake), `SessionDrawdownCircuitBreakerPct` (trips → bust + release + notify), `MaxConcurrentLiveSessions`.

**Tests:** 171/171 — including the offline EIP-712/HMAC signing suite, the new arm state machine, and reservation invariants.

---

## Fixed during this readiness pass

| Sev | Issue | Fix |
|---|---|---|
| HIGH | `StartAsync` reserved twice (`Guid.Empty` then `session.Id`) → session start failed when wallet ≤ 2× balance | Pre-flight `GetFreeAsync` affordability check + exactly one audit reservation; no orphan rows |
| MED | Live bets settled optimistically before CLOB resolution (inflated P&L) | Live bet now stays **open** until `GetMarketResolutionAsync` returns `Resolved=true`; paper/backtest proxy unchanged |
| LOW | Unused `Polymarket.ExchangeAddress` orphan in `appsettings.json` | Removed |

---

## Known limitations (acceptable for the $1 order; track for scale-up)

- **No early-exit/SELL in the live lifecycle.** `SellAsync` exists but the engine holds to resolution — correct for a binary up/down that settles at expiry. Discretionary close is a future workstream.
- **No CLOB position reconciliation** — positions tracked in the local DB only; the reconcile loop covers collateral drift, not position-level state.
- **Arm state is in-memory** — re-arm after any backend restart (safe: no orders fire while disarmed, but a live session pauses).

## Validate *with* the $1 order (cannot be checked offline)

1. **EIP-712 `chainId` encoding** — serialized as string `"137"`; Nethereum coerces it and the offline round-trip passes, but only an accepted order proves the CLOB verifier agrees. If the first order is rejected with a signature error, this is the first suspect.
2. **pUSD address** `0xC011a7E12a19f7B1f670d46F03B03f3342E82DFB` — confirm it reflects your funded Polymarket collateral on Polygon mainnet.

---

## Checklist to the $1 order (human-owned steps in **bold**)

Full commands in `live-setup.md`. Stop after step 8 and confirm the fill before scaling.

1. **Generate a fresh EOA; fund with MATIC (gas) + USDC.e.**
2. **Wrap USDC.e → pUSD via the onramp** (~$10 is plenty for a $1 test).
3. **Approve pUSD + CTF tokens to both exchange variants.**
4. **Set `.env`:** `KeyVault__PrivateKey`, `KeyVault__SignatureType=0`, `Trading__MaxPerTradeUsd=1.00`, keep `Polymarket__LiveTrading=false`.
5. Start backend; create a **live-mode** session (initialBalance≈2). Confirm logs show `[SHADOW/DISARMED] Would place live bet` and on-chain pUSD balance reads correctly. (No money moves — this is shadow.)
6. **Flip `Polymarket__LiveTrading=true`; restart.** Backend derives CLOB API creds via L1 on first use.
7. **Arm:** `request-code` → `confirm` with the 6-digit code → `status` shows `armed:true`.
8. **Watch the first real $1 BTC up/down order place + fill on the Polymarket dashboard.** Verify signature accepted, pUSD debited, settlement on resolution. Keep `killswitch` ready.
9. After a clean fill + settle: raise `Trading__MaxPerTradeUsd` to operating level, restart, and only then consider arming automation for unattended runs.

**Kill switch at any time:** `POST /api/golive/killswitch`.

---

## Not blocking go-live, but recommended next

- Frontend has **zero** automated tests — the biggest quality gap for unattended money operation.
- CI/deploy (`ci.yml` + Dockerfiles) hasn't had a green dry-run on this branch.
- Branch is not yet pushed / no PR.
