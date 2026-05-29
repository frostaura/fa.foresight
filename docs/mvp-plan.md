# fa.foresight — MVP Build Spec (greenfield rewrite)

> **Greenfield rewrite.** Built fresh at `Technologies/projects/fa.foresight/src`. The existing `Labs/projects/fa.foresight` build is the **reference only** — mine it for keepers, do not mutate it. This graduates fa.foresight **Labs (moonshot) → Technologies (product / cash engine)**.
>
> **Audience:** this document is the implementation contract for an autonomous coding agent. It is intended to be self-sufficient: architecture, data model, ports, the CLOB V2 wire facts, per-workstream task checklists (all unchecked), acceptance criteria, and sequencing. Where a decision was open, a **default is stated and labelled `[DEFAULT — override if wrong]`**.

---

## 1. North star
A lean, automated **directional-trading platform**: deterministic models predict short-horizon crypto up/down, strategies size the bet against **real prediction-market odds**, and the system trades automatically at volume — proven first in a faithful paper sim + chaos/bust test, then run live. First instrument: **BTC up/down on Polymarket, 5m & 15m.**

**MVP = lean, not shallow.** Carry forward only what makes the trader win; leave everything off-mission behind. Full R&D depth on models *and* strategies stays in.

---

## 2. Scope

### In
- Deterministic models (logistic `v6` / `5m v1` family, GBT `v2`); no LLM model.
- Model R&D: training, walk-forward evaluation (overfit tripwire), feature experimentation.
- Strategy layer: baselines (`flat`, `martingale`, fixed-fraction `kelly`, whole-dollar `kelly-d1`) + **edge-aware true Kelly**.
- Faithful **Polymarket odds-based economics** in paper, backtest, and bust test.
- **Calibration rescaling** (raw model prob → calibrated prob per interval) — load-bearing for edge.
- **Chaos/bust test** over random historical windows, model×strategy matrix.
- **Dual-view authoring** (design ⇄ code) for models **and** strategies; **executable sandboxed deterministic nodes** (Python sidecar); **notebook-style step-through runner**.
- Multi-session **paper + live** trading; **config-hash uniqueness**; **per-session bankroll reservation** against a real account balance.
- Automated **live execution on Polymarket CLOB V2**.
- Navigation (**Trading → Status / Live / Paper**) + cohesive UI/UX redesign; full-screen charts.
- **Venue** and **symbol** both pluggable (Polymarket + BTC bundled; Kalshi / ETH / etc. later).

### Out / left behind (not ported)
- Markets browser / screener, market discovery, LLM+evidence market forecasting, sentiment/chat/suggestions, the autonomous discover-any-market loop.
- The LLM prediction model and LLM-specific flow nodes.
- B2B forecast feed, whale-flow / on-chain analytics.

### Open decisions resolved with defaults
- **Telegram/Discord control bot.** `[DEFAULT — override if wrong]` Ship a **thin notifications channel only** in Phase 2 (trade placed, bet resolved, session bust, circuit-breaker trip), behind the existing `IChannelAdapter` port. Defer the full inbound command bot (`/golive`, `/positions`, …) to a fast-follow. Rationale: an automated money trader needs alerts; it doesn't need a chat command surface to be useful on day one, and `/golive` can live in the web UI.
- **Multi-tenancy / auth.** `[DEFAULT — override if wrong]` **Keep the multi-tenant data scaffolding** (every entity tenant-scoped) since it's cheap and preserves B2B optionality, but **single-user auth** for the MVP (one tenant resolved via header/local identity, no passkey/JWT build yet).

---

## 3. Locked decisions (scoping outcomes)
1. **Venue:** Polymarket, automated. Venue = first-class pluggable integration (`IPredictionMarketProvider` + `IExecutionProvider` pair). Polymarket bundled default for both data and execution.
2. **Symbols/intervals:** BTC up/down, **5m + 15m** (1h dropped). Symbol axis extensible (ETH later). Each venue declares a **capability matrix** (symbol → supported intervals) that drives session/UI options.
3. **Payoff = real odds, not even-money.** Buy YES/NO at price `p` → shares = stake/`p`; a win pays shares×$1 (≈ +`(1−p)/p`×stake), a loss = −stake. Edge = calibrated model probability vs market price.
4. **Historical prices:** real Polymarket price history wherever available; synthetic per-candle pricing only as a flagged last-resort fallback.
5. **Strategies:** keep the four baselines; **add edge-aware true Kelly** — f\* = p − q/b (b = (1−price)/price), apply fractional multiplier, whole-dollar rounding, **skip when target rounds below $1**. Both outcome-only and edge-aware coexist.
6. **Bust = balance ≤ 0.** Strict mode halts the run at the bust point unless allow-borrow is on. **Pass = balance stayed > 0 the whole window.**
7. **Chaos/bust sampling:** many **random start points** at a configurable fixed length + optional sweep over a few lengths; model×strategy matrix; report bust-rate, worst drawdown, profit distribution, per-combo pass/fail.
8. **Sessions:** paper & live share one config surface (model, strategy, interval, starting balance, initial bet, gate). Multiple run in parallel. **Config-hash uniqueness** — reject a new session whose full-config hash equals an already-active one (paper too).
9. **Capital:** per-session budget + bet, **no account-level hard cap.** Starting a live session **reserves** its bankroll from the account's free balance; the reservation **floats with the session's current balance**. Invariant: `free = wallet pUSD − Σ(current balances of active live sessions)`. New session cannot reserve > `free`. On stop, balance merges back into free. App owns reconciliation vs the real wallet.
10. **Wallet:** EOA direct (signatureType 0); `signatureType` + funder address **configurable** so a proxy/deposit wallet is a no-rewrite switch later. Recent-data config guide ships with live setup.
11. **Authoring:** Logic-Apps-style **dual view** — one canonical definition, visual **design view** ⇄ editable **code view** (serialized DSL), round-tripping — for models *and* strategies. **Executable nodes** (Python) run **sandboxed & deterministic** (no ambient network/clock/unseeded RNG); backtests use a **vectorized run-once-over-series** contract; a **step-through runner** executes node-by-node on upstream outputs. **Non-negotiable: code nodes are pure functions of their inputs** so live, step-through, and bust-test results are identical.
12. **UI:** cohesive FrostAura-brand redesign (frosty-glass, light-blue-on-dark-blue, React + Tailwind + shadcn/ui). **No functional regression** — keep result orbs (hit/miss/skip/pending), the active-candle lean dot, zero-crossing "wildness" markers, ledger detail. Fix chart default zoom, make the node palette genuinely draggable, fix blocky misalignment, add full-screen charts. Sidebar collapse already exists in the reference (port it).

---

## 4. Architecture

### 4.1 Tech stack (FrostAura mandate)
- **Backend:** .NET 10, ASP.NET Core (Minimal APIs + SSE), EF Core, PostgreSQL 16. Hexagonal (domain core + ports + adapters).
- **Frontend:** React 19 + TypeScript, Redux Toolkit (RTK Query), Tailwind CSS, shadcn/ui, Vite, PWA.
- **Sandbox sidecar:** Python execution service (see §4.6) — containerized, no network egress.
- **Deploy:** Docker / Compose; GitHub Action → Docker Hub (multi-arch) → Portainer re-pull → Cloudflared tunnel (FrostAura standard pattern; mirror `fa.startup/.github/workflows/ci.yml`).
- **Repo:** push to `github.com/frostaura/fa.foresight` (secrets: DOCKERHUB_*, PORTAINER_*).

### 4.2 Solution structure
```
src/backend/
  FrostAura.Foresight.Domain/         # entities, ports, pure logic (StakingEngine, strategies, flow contracts)
  FrostAura.Foresight.Application/    # use-cases, flow executor + nodes, backtest/walk-forward, chaos engine
  FrostAura.Foresight.Infrastructure/ # adapters (Polymarket data+exec, Binance, Postgres, sandbox client, channels)
  FrostAura.Foresight.Api/            # Minimal API endpoints + SSE
  FrostAura.Foresight.Tests/          # xUnit + FluentAssertions
src/frontend/                         # React app
src/sandbox/                          # Python execution sidecar
docs/                                 # this plan, charter, architecture, specs
```

### 4.3 Ports (hexagonal)
- `IPredictionMarketProvider` — venue market data: resolve the current/next BTC up/down market for (symbol, interval, targetOpenTime), current YES/NO price, resolution status, **historical price series**, capability matrix. Adapter: Polymarket (Gamma + CLOB price history).
- `IExecutionProvider` — venue order placement, order state, cancel, positions. Adapter: Polymarket CLOB **V2** (§6).
- `IKeyVault` — sign EIP-712 typed data, expose public address. Adapter: local Nethereum signer (configurable signatureType/funder); stub throws when unconfigured.
- `IHistoricalCandleProvider` / live market data — Binance (port from reference).
- `IModelExecutor` / flow execution — runs a model or strategy definition (DAG) given inputs (§4.5–4.6).
- `IChannelAdapter` — outbound notifications (Phase 2 thin adapter).
- `IAccountLedger` — wallet balance + reservation accounting (§5.4).

**Adapter discipline:** domain tests pass with every adapter faked. New adapter = drop-in, no domain changes.

### 4.4 Data model (Postgres, all tenant-scoped)
Port surviving tables; change/extend as noted.
- `tenants` — id, name, slug, settings (jsonb), created_at.
- `historical_candles` (symbol, interval, open_time) — OHLCV cache (Binance).
- `historical_microstructure` (symbol, interval, open_time) — order-flow bars (Binance aggTrades).
- `venue_market_prices` **(new)** — (venue, symbol, interval, target_open_time, observed_at) → yes_price, no_price; real Polymarket odds history for faithful backtests. Source of truth for entry prices; synthetic rows flagged `synthetic=true`.
- `models` — id, tenant_id (null=builtin), name, description, kind (`deterministic`), definition (jsonb DAG), trained_state (jsonb), training_status, train window (symbol/interval/start/end), accuracies, timestamps.
- `strategies` **(new/extended)** — id, tenant_id, name, definition (jsonb DAG) **or** built-in id, params (jsonb). Strategies become first-class definitions (see §4.5).
- `active_models` (tenant, symbol, interval) → model_id.
- `live_predictions` — per-candle prediction with calibration fields, prompt/trace, resolution (port; drop LLM-only columns if unused).
- `sessions` **(unified paper+live)** — id, tenant_id, **mode (`paper`|`live`)**, venue, symbol, interval, model_id, strategy_id, params (jsonb: gate, etc.), **config_hash (unique among active)**, initial_balance, initial_bet_size, current_balance, current_bet_size, **reserved_amount** (live), bust, zero_crossings, peak_borrowed, started_at, stopped_at.
- `session_bets` — id, session_id, target_open_time, side, model_prob, market_entry_price, shares, stake, balance_before, balance_after, resolved, outcome, payout, resolved_at, actual_close, **external_order_id** (live), notes (jsonb).
- `positions` / order records (live) — venue order id, status, fills, realized pnl (reconciliation).
- `account_ledger` **(new)** — wallet balance snapshots + reservation entries for the invariant in §5.4.
- `backtests` + `backtest_bets` — port; extend with odds-based fields (entry_price, payoff) and `batch_kind` (`backtest`|`chaos`).
- `chaos_runs` **(new)** — id, batch_id, model_id, strategy_id, symbol, interval, window_length, sample_count, allow_borrow, and per-sample results (start_ms, survived, final_balance, max_drawdown, zero_crossings) + aggregates (bust_rate, profit p5/p50/p95, worst_drawdown).

### 4.5 Flow / DAG model (models AND strategies)
- A **definition** is a DAG: `nodes[]` (id, typeId, params, ports) + `edges[]` (from `nodeId.port` → `nodeId.port`). Serialized JSON = the **code view**; the visual designer is the **design view**; they round-trip losslessly.
- **Model DAG** terminal node `output.prediction` emits pUp, confidence, predicted, p05/p50/p95.
- **Strategy DAG** terminal node `output.stake` emits next stake (and side, if the strategy chooses side) given inputs: calibrated pUp, market price, current balance, current bet, initial bet, last outcome, history.
- Node library (port, minus LLM nodes): indicator/feature packs, regression/GBT model nodes, microstructure packs, matrix builder, plus **new strategy nodes** (kelly, martingale step, flat, edge-aware kelly, clamp/round, gate) and the **code node** (§4.6).
- `FlowExecutor` (Kahn layered, parallel per layer, stateless nodes, trained state passed via context) — port from reference.
- `FlowValidator` — unique ids, known types, one terminal, resolvable typed ports, no cycles, backtest flows reject `RequiresLiveData` nodes — port from reference.

### 4.6 Executable nodes + step-through runner
- **Sandbox sidecar** (`src/sandbox`): a Python service that executes a node body against typed inputs and returns typed outputs. **No network egress, no filesystem writes, no wall-clock/RNG unless seeded.** Container with locked-down runtime.
- **Two execution paths, one definition:**
  - *Step-through runner* — execute node N given upstream outputs; return outputs + captured stdout for inspection; iterate (notebook-style). Latency-tolerant.
  - *Batch backtest runner* — **vectorized "run once over the whole series"** contract: the code node receives arrays (the candle/feature series) and returns arrays, so a backtest pays one sidecar call per node, not one per candle.
- **Purity contract (enforced):** a code node is a pure function of declared inputs. Same definition ⇒ identical output live, in step-through, and in bust test. Determinism is validated by a test that runs the same node twice and asserts byte-identical output.

---

## 5. Trading engine semantics

### 5.1 Prediction → decision → settlement
1. For a session's (symbol, interval), resolve the venue market for the target candle and its current YES/NO price.
2. Run the model → calibrated pUp; decide side (UP→YES, DOWN→NO). Optional confidence gate.
3. Run the strategy → stake (edge-aware Kelly uses pUp vs market price; whole-dollar round; skip if < $1).
4. Compute shares = stake / entry_price; record bet.
5. On candle/market resolution: win = outcome matches side; payout = shares×$1 on win, 0 on loss; update balance; next bet size from strategy.

### 5.2 Odds-based StakingEngine (replaces even-money)
Pure functions, shared by paper / backtest / chaos / live:
- `DecideSide(calibratedPUp)`; `IsNoBet(pUp, band)` (gate).
- `Settle(strategy, side, entryPrice, stake, balance, outcomeUp, …)` → balanceAfter, nextStake, crossedZero/bust. **Win:** balance += stake×((1−entryPrice)/entryPrice). **Loss:** balance −= stake. Bust when balance ≤ 0 (strict halt unless allowBorrow).

### 5.3 Strategies
Built-in (parameterized) + authorable (DAG). Edge-aware Kelly: `f* = pUp − (1−pUp)/b`, `b=(1−price)/price`; stake = clamp(fraction × f* × balance), whole-dollar round, skip < $1.

### 5.4 Reservation ledger (live)
- On live session start: `reserve = initial_balance`; require `reserve ≤ free` where `free = walletPUSD − Σ(active live session current_balance)`. Reject otherwise.
- Reservation **floats**: as a session's `current_balance` changes, its claim on the account changes; `free` recomputes from the invariant.
- On stop: balance merges into free (claim removed).
- **Reconciliation pass**: periodically compare Σ(session balances) + free against real on-chain pUSD (via venue/balance read); surface drift.

---

## 6. Polymarket CLOB V2 — wire facts (verified May 2026; re-verify at build)
> V1 was **decommissioned 2026-04-28** with no backward compatibility. Build to V2.

- **Base URL:** `https://clob.polymarket.com` (V2 took over the prod URL).
- **Exchange EIP-712 domain:** `name="Polymarket CTF Exchange"`, **`version="2"`**, `chainId=137`, `verifyingContract`= the exchange for the market type.
- **Contracts (Polygon 137):** CTF Exchange `0xE111180000d2663C0091e4f400237545B87B996B`; **Neg-Risk** CTF Exchange `0xe2222d279d744050d28e00520010520000310F59` (route neg-risk markets here); Conditional Tokens `0x4D97DCd97eC945f40cF65F87097ACe5EA0476045`; pUSD collateral `0xC011a7E12a19f7B1f670d46F03B03f3342E82DFB`; Collateral Onramp (wrap USDC.e→pUSD) `0x93070a847efEf7F70739046A929D47a521F5B8ee`.
- **Order struct (signed, V2):** `salt uint256, maker address, signer address, tokenId uint256, makerAmount uint256, takerAmount uint256, side uint8, signatureType uint8, timestamp uint256(ms), metadata bytes32, builder bytes32`. **Removed vs V1:** taker, expiration, nonce, feeRateBps. `timestamp` (ms) replaces nonce for uniqueness; `metadata`/`builder` = zero bytes32 unless using a builder code.
- **POST /order body:** order fields above with `side` as string `"BUY"`/`"SELL"`, plus `signature`, `owner=<apiKey>`, `orderType="GTC"` (or FOK/FAK).
- **Amounts:** USDC/pUSD 6 decimals, CTF tokens 6 decimals. BUY: makerAmount = round_down(price×size×1e6) (collateral in), takerAmount = round_down(size×1e6) (tokens out). Respect per-market **min tick (mts)** and **min order size (mos)** from `getClobMarketInfo(conditionId)`.
- **Fees:** protocol-set at match time; **no feeRateBps in the signed order**; makers free, takers pay; query `getClobMarketInfo` for fee details.
- **signatureType:** `0` EOA (**our default**), `1` POLY_PROXY, `2` POLY_GNOSIS_SAFE.
- **L1 auth (unchanged in V2):** ClobAuth domain `name="ClobAuthDomain"`, `version="1"`, `chainId=137`; type `ClobAuth{address, timestamp string, nonce uint256, message string}`; message = `"This message attests that I control the given wallet"`. Headers POLY_ADDRESS / POLY_SIGNATURE / POLY_TIMESTAMP / POLY_NONCE.
- **L2 auth (unchanged):** headers POLY_ADDRESS / POLY_API_KEY / POLY_PASSPHRASE / POLY_TIMESTAMP / POLY_SIGNATURE; signature = `base64url( HMAC_SHA256( base64decode(secret), timestamp+method+path+body ) )` — **convert `+`→`-`, `/`→`_`, and KEEP the `=` padding (do NOT trim).** (The reference V1 code wrongly trimmed `=`.)
- **First live use:** a single supervised **$1 order**, confirm fill on-chain, then ramp. The CLOB V2 path is unvalidated against the live exchange until then.

---

## 7. Workstreams & tasks (all unchecked)

### A. Scaffold the rewrite + port forward keepers `[Phase 1, first]`
- [ ] Create the solution + projects per §4.2; wire DI, config, Postgres, EF Core, Minimal API + SSE host.
- [ ] New project `CLAUDE.md` + `docs/charter.md` (follow `Technologies/projects/CLAUDE.md` schema).
- [ ] Update root project registry + `docs/portfolio/project-placement.md` for the **Labs→Technologies** graduation.
- [ ] Mirror the FrostAura CI/deploy workflow (`.github/workflows/ci.yml`); add Dockerfiles + compose.
- [ ] Port: domain entities, ports, `FlowExecutor`, `FlowValidator`, node library (**exclude LLM nodes**).
- [ ] Port: Binance candle + microstructure adapters; `IHistoricalCandleProvider`.
- [ ] Port: deterministic models (`v6`, `5m v1` family, GBT `v2`) + `BuiltInModels` seeding; set a **deterministic default model**.
- [ ] Port: `CalibrationRescaler` (raw→calibrated per interval) and `LivePredictionService` minus the LLM default path.
- [ ] Port: model **training** + **walk-forward evaluator** (overfit tripwire) — model-side R&D.
- [ ] Thin Polymarket backend slice only (market lookup, price, resolution); no browser/discovery code.
- **Acceptance:** new solution builds; `dotnet test` green; default deterministic model produces live 5m/15m predictions; no left-behind paradigm references.

### B. Faithful economics + edge-aware strategies `[Phase 1]`
- [ ] Implement odds-based `StakingEngine` (§5.2); delete even-money assumption.
- [ ] `IPredictionMarketProvider`: real **historical price series** for recurring 5m/15m BTC markets → `venue_market_prices`; synthetic fallback flagged.
- [ ] **Window/settlement alignment:** map the model's predicted Binance candle to the Polymarket market's exact resolution window AND reference price source, so prediction and settlement agree. Encode the mapping in the venue adapter; assert it in tests (a predicted UP that the market settles DOWN due to a boundary/source mismatch is a correctness bug, not a loss).
- [ ] Edge-aware true Kelly strategy (§5.3) + whole-dollar rounding + sub-$1 skip; keep `flat`/`martingale`/`kelly`/`kelly-d1`.
- [ ] Strategy resolution + params surface; unit tests for each strategy's size dynamic and payoff math.
- **Acceptance:** a $2 win nets ~$3.5–3.8 at ~0.52–0.56 entry; edge-aware Kelly sizes off calibrated pUp vs price; backtest/paper settle on real-odds payoff; tests cover win/loss/skip/bust.

### C. Dual-view authoring + executable nodes `[Phase 1]`
- [ ] Promote strategies to DAG definitions (`output.stake`); add strategy nodes (§4.5).
- [ ] Serialized DSL ⇄ visual designer round-trip (lossless) for models and strategies.
- [ ] Sandbox sidecar (`src/sandbox`): isolated Python, no network, deterministic; typed I/O protocol.
- [ ] Code node (design + code view); vectorized batch contract + step-through path.
- [ ] Step-through runner API + UI (run node N, inspect outputs/stdout, iterate).
- [ ] Determinism test: same node twice ⇒ identical output; purity guard.
- **Acceptance:** a model and a strategy each author/edit in both views; a Python node runs identically in step-through and batch backtest.

### D. Backtest + chaos/bust test `[Phase 1]`
- [ ] Backtest runner on odds-based payoff (port + adapt); per-bet entry price from `venue_market_prices`.
- [ ] Chaos engine: random-start sampling (configurable count + length, optional length sweep), model×strategy matrix, strict halt at bust, allow-borrow toggle.
- [ ] Efficiency: precompute per-candle prediction once per model; replay strategy per window.
- [ ] Aggregates: bust-rate, profit p5/p50/p95, worst drawdown, per-combo pass/fail (pass = never ≤0).
- [ ] SSE progress; persist `chaos_runs`; results API.
- **Acceptance:** N models × M strategies → ranked "survives & profits regardless of entry" verdict with the metrics above.

### E. Live automated trading + venue abstraction `[Phase 2]`
- [ ] Polymarket CLOB **V2** execution adapter per §6 (domain v2, V2 order struct, neg-risk routing, pUSD, correct L2 HMAC with `=` kept).
- [ ] Nethereum signer (`IKeyVault`), configurable `signatureType`/funder, EOA default.
- [ ] L1 cred derivation + L2 signing; `getClobMarketInfo` for tick/min-size/fees.
- [ ] Unify session engine for **live** mode: place/cancel/track orders, resolution & settlement polling for fast 5m/15m markets, persist fills/positions.
- [ ] Resolve each live bet against the **market's own settlement** (not the Binance candle) and reconcile any divergence from the model's predicted candle (see B window/settlement alignment).
- [ ] Reservation ledger + invariant (§5.4) + reconciliation pass vs wallet.
- [ ] Config-hash uniqueness across active sessions (paper + live).
- [ ] Guardrails (per-trade cap, max concurrent, drawdown circuit breaker) + web `/golive` arm + kill switch.
- [ ] Venue as pluggable integration (capability matrix; Polymarket default); symbol-extensible.
- [ ] Offline signing test suite: EIP-712 sign→ecrecover round-trip, ClobAuth, L2 HMAC vector (padding kept), amount scaling.
- [ ] **Recent-data config guide** (`docs/live-setup.md`): create/fund EOA, export key safely, MATIC for gas, wrap USDC.e→pUSD via Onramp, token approvals, derive CLOB creds, env keys — verified against live sources at write time.
- [ ] Supervised **$1 order** validation before arming automation.
- **Acceptance:** a live session places, fills, and settles real Polymarket BTC orders automatically within reservation + guardrail limits; venue/symbol are config, not code; $1 order validated.

### F. Navigation + UI/UX redesign `[Phase 1 surfaces; live surface in Phase 2]`
- [ ] Rename/route **Trading → Status / Live / Paper** sub-sections.
- [ ] **Status** overview: all live + paper sessions, overlaid balance curves, hit/miss, totals (one block live, one paper).
- [ ] Session create UX (model, strategy, interval, starting balance, initial bet, gate) for paper + live; show config-hash dedup feedback; live shows reservation vs free.
- [ ] Per-session ledger / portfolio view (open on click), with starting snapshot.
- [ ] Cohesive FrostAura-brand redesign (tokens, shadcn/ui, frosty-glass, light-blue-on-dark-blue); fix blocky misalignment.
- [ ] Chart: sane default zoom; **full-screen** mode (tablet dashboard); port collapsible sidebar.
- [ ] Flow designer: **genuinely draggable** palette + nodes (fix the fake-draggable issue).
- [ ] **Preserve all intricacies** — result orbs (hit/miss/skip/pending), active-candle lean dot, zero-crossing markers, ledger detail. No functional regression.
- **Acceptance:** Trading surface is clean, aligned, tablet-friendly, full-screenable; designer is truly drag-and-drop; every existing signal still present and working.

### Cross-cutting
- [ ] Thin notifications channel (Phase 2): trade placed / resolved / bust / breaker, via `IChannelAdapter`.
- [ ] Tests: domain unit tests with faked adapters; strategy/payoff math; determinism; signing; chaos aggregates; key API integration tests.
- [ ] Observability: structured logs + SSE event traces for predictions, bets, orders, reconciliation.

---

## 8. Sequencing
- **Phase 1 — prove the edge (no money):** A → B → C → D, with F's paper/status surfaces alongside. **Exit:** ≥1 model+strategy combo demonstrably survives and profits across random windows in the chaos test.
- **Phase 2 — trade it live:** E, with F's live surface alongside. **Exit:** supervised $1 order validated, then automated live sessions running within reservation + guardrail limits.
- Dependency notes: B depends on A; C depends on A (flow engine); D depends on B (odds payoff) + C (strategy/model defs); E depends on A (sessions) + B (sizing) + D (a chosen winning combo). F has no hard dep on E for its non-live surfaces.

## 9. Risks / open items
- **Polymarket 5m/15m price-history depth** — recent markets; real-odds backtests may be shallow. Verify available history early; synthetic fallback bounds downside but is flagged.
- **Python sandbox determinism + performance** — purity contract + vectorized batch path are load-bearing; validate before strategies depend on code nodes.
- **CLOB V2 unvalidated live** — first use is the supervised $1 order; do not arm automation before it passes.
- **Reservation ↔ wallet reconciliation** — keep app reservations consistent with on-chain pUSD as bets settle.
- **Prediction/settlement alignment** — the model predicts a Binance candle; the Polymarket market settles on its own window + reference feed. Mismatched boundaries or price sources corrupt both backtest faithfulness and live P&L. Pin the exact mapping early (verify the live market's resolution rules) before trusting any result.
- **Default decisions** in §2 (bot scope, tenancy) — confirm or override.
