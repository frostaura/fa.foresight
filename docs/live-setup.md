# fa.foresight — Live Trading Setup Guide

This guide covers everything needed to move from paper simulation to live execution on Polymarket CLOB V2. Live trading is deliberately disarmed in code until the supervised $1 validation order passes.

## Prerequisites

- A funded Polygon (Matic) EOA wallet
- MATIC for gas fees (small amounts, Polygon is cheap — 0.01 MATIC covers many transactions)
- USDC.e on Polygon to wrap into pUSD
- Access to the backend's environment configuration

---

## Step 1 — Create and Fund the EOA

1. Generate a new EOA (never reuse a hot wallet from another platform):
   ```bash
   # Using cast (Foundry) — generates a random key
   cast wallet new
   # Output: Address + Private Key (save securely, never commit)
   ```
2. Fund with MATIC for gas: bridge from Ethereum or buy directly on Polygon.
3. Bridge USDC.e to Polygon if not already there.

---

## Step 2 — Wrap USDC.e into pUSD

Polymarket uses pUSD as collateral. Wrap via the Collateral Onramp contract:

- **Collateral Onramp:** `0x93070a847efEf7F70739046A929D47a521F5B8ee`
- **pUSD token:** `0xC011a7E12a19f7B1f670d46F03B03f3342E82DFB`

Using cast (Foundry):
```bash
export RPC="https://polygon-rpc.com"
export ONRAMP="0x93070a847efEf7F70739046A929D47a521F5B8ee"
export USDC_E="<your USDC.e contract address on Polygon>"
export WALLET="<your EOA address>"
export PRIVATE_KEY="<your private key>"
export AMOUNT_6DP="10000000"  # 10 USDC.e in 6dp (= $10)

# 1. Approve USDC.e transfer to onramp
cast send $USDC_E "approve(address,uint256)" $ONRAMP $AMOUNT_6DP \
    --rpc-url $RPC --private-key $PRIVATE_KEY

# 2. Deposit to onramp (wraps USDC.e → pUSD)
cast send $ONRAMP "deposit(address,uint256)" $WALLET $AMOUNT_6DP \
    --rpc-url $RPC --private-key $PRIVATE_KEY
```

---

## Step 3 — Token Approvals for the CTF Exchange

The CLOB exchange must be approved to transfer pUSD (collateral) and CTF outcome tokens on your behalf.

```bash
export CTF_EXCHANGE="0xE111180000d2663C0091e4f400237545B87B996B"
export CTF_EXCHANGE_NEG_RISK="0xe2222d279d744050d28e00520010520000310F59"
export CTF_TOKENS="0x4D97DCd97eC945f40cF65F87097ACe5EA0476045"
export PUSD="0xC011a7E12a19f7B1f670d46F03B03f3342E82DFB"
export MAX="115792089237316195423570985008687907853269984665640564039457584007913129639935"

# Approve pUSD to both exchange variants
cast send $PUSD "approve(address,uint256)" $CTF_EXCHANGE $MAX \
    --rpc-url $RPC --private-key $PRIVATE_KEY
cast send $PUSD "approve(address,uint256)" $CTF_EXCHANGE_NEG_RISK $MAX \
    --rpc-url $RPC --private-key $PRIVATE_KEY

# Approve CTF tokens (ERC-1155) to both exchanges
cast send $CTF_TOKENS "setApprovalForAll(address,bool)" $CTF_EXCHANGE true \
    --rpc-url $RPC --private-key $PRIVATE_KEY
cast send $CTF_TOKENS "setApprovalForAll(address,bool)" $CTF_EXCHANGE_NEG_RISK true \
    --rpc-url $RPC --private-key $PRIVATE_KEY
```

---

## Step 4 — Derive CLOB API Credentials

The Foresight backend derives its own CLOB API key via the L1 auth flow (EIP-712 signed ClobAuth message). No manual action required — the backend calls `GET /auth/derive-api-key` on first use when `LiveTrading=true` and a private key is configured.

To verify credentials work manually:
```bash
# Replace with your real values after the backend derives them (logged at INFO level on startup).
curl -H "POLY_ADDRESS: <address>" \
     -H "POLY_SIGNATURE: <l1-sig>" \
     -H "POLY_TIMESTAMP: <unix-seconds>" \
     -H "POLY_NONCE: 0" \
     https://clob.polymarket.com/auth/derive-api-key
# Returns: {"apiKey":"...","secret":"...","passphrase":"..."}
```

---

## Step 5 — Configure Environment Variables

Add to `.env` (never commit the private key):

```dotenv
# Live trading gate (false = paper/shadow mode)
Polymarket__LiveTrading=false

# Wallet signing key (hex, with or without 0x)
KeyVault__PrivateKey=<your-private-key>

# EIP-712 signatureType: 0=EOA (default), 1=POLY_PROXY, 2=POLY_GNOSIS_SAFE
KeyVault__SignatureType=0

# Optional funder/maker override (leave unset for EOA)
# KeyVault__Funder=<proxy-or-safe-address>

# Per-trade cap for live sessions (pUSD). Start at $1 for validation.
Trading__MaxPerTradeUsd=1.00

# Per-session drawdown circuit breaker (0.50 = stop at 50% loss)
Trading__SessionDrawdownCircuitBreakerPct=0.50

# Max concurrent live sessions
Trading__MaxConcurrentLiveSessions=1
```

---

## Step 6 — The Supervised $1 Validation Step

**Do this before arming automation.**

1. Set `Polymarket__LiveTrading=false` (keep shadow mode for now).
2. Start the backend and create a live session via the API (mode=live, initialBalance=2).
3. Verify the paper-style logs show `[SHADOW/DISARMED] Would place live bet` — confirms the engine runs correctly in shadow.
4. When satisfied, set `Polymarket__LiveTrading=true` and restart the backend.
5. Request an arming code:
   ```bash
   curl -X POST http://localhost:8088/api/golive/request-code \
        -H "X-Tenant: default"
   # Returns a 6-digit code valid for 5 minutes.
   ```
6. Confirm the code:
   ```bash
   curl -X POST http://localhost:8088/api/golive/confirm \
        -H "X-Tenant: default" \
        -H "Content-Type: application/json" \
        -d '{"code":"<6-digit-code>"}'
   # Returns: {"armed":true,...}
   ```
7. Check arm status:
   ```bash
   curl http://localhost:8088/api/golive/status -H "X-Tenant: default"
   # Returns: {"armed":true}
   ```
8. Watch the logs for the first real order. Confirm on Polymarket's dashboard that a $1 BTC up/down position was opened and filled.
9. After confirming: raise `Trading__MaxPerTradeUsd` to your operating level and restart.

**Kill switch** (disarm immediately):
```bash
curl -X POST http://localhost:8088/api/golive/killswitch -H "X-Tenant: default"
```

---

## Reference: CLOB V2 Contract Addresses (Polygon mainnet, chainId=137)

| Contract | Address |
|---|---|
| CTF Exchange (standard) | `0xE111180000d2663C0091e4f400237545B87B996B` |
| NegRisk CTF Exchange | `0xe2222d279d744050d28e00520010520000310F59` |
| Conditional Tokens (CTF) | `0x4D97DCd97eC945f40cF65F87097ACe5EA0476045` |
| pUSD collateral | `0xC011a7E12a19f7B1f670d46F03B03f3342E82DFB` |
| Collateral Onramp | `0x93070a847efEf7F70739046A929D47a521F5B8ee` |

EIP-712 domain: name="Polymarket CTF Exchange", version="2", chainId=137. verifyingContract is routed per market: negRisk=true → NegRiskCtfExchange, negRisk=false → CtfExchange.

---

## What is Gated Behind the $1 Order

The following will NOT fire until the supervised $1 order passes and the arm is confirmed:

- Real `PlaceOrderAsync` calls to the CLOB (gated by four independent layers: `LiveTrading=false` → `NullExecutionProvider`; the DI gate; the early-exit arm check in `LiveSessionEngine.ProcessAsync`; and the placement-time arm check). All four must pass before any order fires.
- Any pUSD leaving the wallet

The following is active and safe in shadow/disarmed mode:

- Full predict → size → guardrail evaluation pipeline
- Paper bets placed and resolved identically to real orders
- SSE events, session tracking, ledger audit entries
- All 171 tests (including the offline EIP-712/HMAC signing suite, arm state machine, and reservation invariants)

---

## Implemented since first draft (verified in audit, 2026-05-29)

The items below were originally deferred but are now in code and covered by tests:

1. **On-chain pUSD balance** — `AccountLedger.GetWalletPusdAsync` makes a real `eth_call` to `balanceOf(wallet)` on the Polygon RPC (`PolygonOptions.RpcUrl`, default `https://polygon-rpc.com`) and scales by 1e6. Returns 0 when no wallet key is configured (shadow mode).
2. **Market resolution from CLOB** — `GetMarketResolutionAsync` queries `GET /markets/{conditionId}` for the real `resolved`/`outcome`. This is the primary settlement path; **a live bet now stays open until the market actually resolves** (no optimistic proxy). Divergence between model side and market outcome is recorded in `live_bets.divergence_note`. (Paper/backtest still settle via the candle-direction proxy by design.)
3. **SELL order support** — `PolymarketExecutionProvider.SellAsync` is implemented and gated by the same `LiveTrading` flag. See "Known limitations" for lifecycle status.
4. **Reconcile loop** — `AccountReconciliationService` (a hosted background service) calls `ReconcileAsync` every 60s (`FORESIGHT_RECONCILE_INTERVAL_SECONDS`) for tenants with active live sessions; non-zero drift is logged and notified.

---

## Known limitations (acceptable for the $1 validation; track for scale-up)

- **Early exit / SELL is not wired into the live session lifecycle.** `SellAsync` exists in the adapter but the engine never calls it — live bets are held to market resolution. This is intentional for BTC up/down (a binary that resolves at expiry); a discretionary close flow is a future workstream.
- **Open positions are not reconciled against the CLOB** — positions are tracked in the local DB only (`GetOpenPositionsAsync` returns empty). The pUSD-balance reconcile loop covers collateral drift; position-level reconciliation is a scale-up item.
- **Arm state is in-memory** — a backend restart silently disarms (safe: no orders fire), and an armed live session will pause with "skipped — arm not confirmed" until re-armed. Re-arm after any restart.
- **Validate on the $1 order, not before:** (a) the EIP-712 `chainId` is serialized as the string `"137"` — Nethereum coerces it to `uint256` and the ecrecover round-trip passes offline, but only a real accepted order confirms the CLOB verifier agrees; (b) confirm the pUSD address `0xC011a7E12a19f7B1f670d46F03B03f3342E82DFB` reflects your funded Polymarket collateral on Polygon mainnet.
