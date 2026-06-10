# Signal-expansion plan — what to steal from `stacking_5m`

**Date:** 2026-05-30 · **Owner:** Dean · **Status:** proposed
**Source:** the Exodus `stacking_5m` recipe — BTC 5m up/down, 80 causal features, LR+XGB+LGBM → meta-LR, reported 60.5% test accuracy.

## Verdict in one line

Foresight is already ahead of `stacking_5m` on signal. Its headline "good" — derivatives (funding/OI/long-short), order flow, time-of-day, HTF regime, GBT — Foresight already has, usually richer. Only **four** ideas are genuinely additive, and **none** ships on accuracy: each must lift OOS cost-weighted PnL / realized edge through the walk-forward + EV gate already built (`docs/ml-audit-2026-05-30-resolution.md`).

## Already covered — do NOT re-port

| `stacking_5m` idea | Foresight equivalent (richer where noted) |
|---|---|
| funding, OI, long/short, ls-imbalance | `indicator.derivatives_pack` — `d_oi_mom`, `d_oi_z`, `d_toptrader_ls`, `d_ls_ratio`, taker L/S (top-trader vs global split = richer) |
| taker_buy_ratio flow proxies | `indicator.orderflow_pack` + `indicator.microflow_pack` (intra-bar) = richer |
| MA / VWAP structure, multi-window ma_slope | `indicator.tech_pack` + `indicator.htf_regime_pack` |
| RSI / MACD / BB / ATR / MFI | `indicator.tech_pack` |
| tod_sin/cos, dow, is_weekend | `indicator.temporal_pack` |
| ret / rv / vol_z multi-window | `indicator.momentum_pack` + `indicator.volume_pack` + `indicator.norm_pack` |
| XGB / LGBM non-linear engine | `model.gbt` (the v2 line) |
| feature×feature crosses | `indicator.cross_pack` (already judged a linear-model crutch; dropped for GBT) |

The 30 bps **train-side** label filter is also already on the radar as audit follow-up **a1 §1** ("magnitude-conditioned label," 60% confidence).

## The four real steals (ranked by conviction)

### 1. Cross-exchange spread feed — Binance vs Coinbase/Kraken  ★ highest conviction
Every current feature is computed inside a single Binance tape. A second venue's price is the one signal **orthogonal** to the entire existing feature set: cross-venue lead/lag and spread mean-reversion is documented short-horizon alpha — exactly the part of `stacking_5m` Foresight cannot already reproduce.
- **Build:** new `source.coinbase.klines` (or Kraken) node mirroring `BinanceKlinesNode`'s anti-lookahead window (`endMs = TargetOpenTime - 1`); a new `indicator.crossvenue_pack` emitting `cb_spread_pct`, `cb_spread_z_60`, `cb_spread_chg_{5,15}`, `cb_lead_ret`. Requires a second historical-candle provider in Infrastructure + a cache-warmer entry.
- **Causal discipline (the thing to actually copy):** the other venue's bar close isn't known until its bar ends — shift that series forward one full bar before reindexing. `stacking_5m` uses `+300s`; Foresight uses `+1 interval`. Skip it and you leak the future.
- **Effort:** M–L (new market-data client + gap-filler). **Lift:** the best shot at genuinely *new* edge.
- **A/B:** ship as `Foresight | 5m | v1+xvenue`, sibling of v1, so the WF delta is attributable.

### 2. ETH cross-asset feed  ★ cheapest new-signal win
BTC/ETH lead-lag and correlation-regime carry direction the BTC-only tape can't see. Cheapest steal because **ETHUSDT already runs through the Binance client** — no new exchange, just a peer symbol + cache-warmer row.
- **Build:** fetch ETHUSDT via the existing `source.binance.klines` (peer-symbol param) → new `indicator.crossasset_pack`: `eth_ret_{1,5,15}`, `eth_btc_spread`, `eth_btc_corr_60`, `eth_rv`.
- **Effort:** S–M. **Lift:** medium.
- **A/B:** `v1+ethx`.

### 3. Learned stacking meta — replace majority vote with a logistic meta
`aggregator.majority_vote` exists today. `stacking_5m`'s meta-LR learned **unequal** weights (LR +1.0, XGB +1.6, LGBM +2.6) and beat every single base. A learned meta over the existing base pUps (v1 logistic, v2 GBT, +ofx) is a small, no-new-data win.
- **Build:** `aggregator.stack_meta` node — logistic meta fit on **out-of-fold** base probabilities. Do NOT copy their shortcut of fitting the meta on the val set; use proper WF OOF (the fold machinery already exists).
- **Effort:** S–M, no new data. **Lift:** small but real.
- **Caveat:** only worthwhile *if* the current aggregator is a hard vote rather than already learned — verify `aggregator.majority_vote` internals first.

### 4. Magnitude-conditioned training label — green-light a1 §1 (train-side only)
Dropping near-zero-move bars **from training** cleans label noise and can sharpen the decision boundary. This is the *only* legitimate part of `stacking_5m`'s magnitude filter.
- **Build:** training-time flag in `ModelTrainer` to exclude `|fwd_ret| < θ` rows from the **fit set** only; serve and measure on all candles.
- **Guard:** train-on-big / serve-on-all introduces covariate shift — measure both ways through the WF economic gate before keeping it.
- **Effort:** S. **Lift:** uncertain (60% conf). A/B only.

## The anti-pattern — do NOT copy

`stacking_5m`'s reported 60.5% is measured on a test set filtered by `|mag_5m| ≥ 30 bps`, where `mag_5m` uses `close[t+5]` — the future. It scores ~4% of bars, hand-picked by outcome, and is not reproducible live. The a1/a2/a3 audit already killed this class of error (served≠validated, wrong objective). The **causal** version of "only act on big moves" is the EV gate already shipped — keep that; never reintroduce an outcome filter into measurement.

## Honest-measurement gate (applies to all four)

A steal ships only if it raises **OOS cost-weighted PnL / realized edge** in walk-forward, after the EV gate — not accuracy, not AUC, not accuracy-on-a-subset. Wiring points already exist: the `applyGate` / `gateBand` params on `BacktestRunner.RunAsync` (and `StakingEngine.HasPositiveEdge` / guardrail `MinEdge` on the served side), and the deferred "WF economic gate" in `WalkForwardEvaluator.PassesGuards` (resolution doc, §Deferred). **Prerequisite:** surface realized-edge / cost-weighted PnL as a first-class WF metric first — without it none of these can be ranked honestly.

## Sequencing

- **Tier 0 (prerequisite):** realized-edge / cost-weighted PnL as a reported WF metric + guard.
- **Tier 1 (cheap, attributable):** #2 ETH feed + #3 learned stack.
- **Tier 1b (the real bet):** #1 cross-exchange spread — most likely *new* alpha, most infra.
- **Tier 2 (protocol A/B):** #4 magnitude label.

## Definition of done (per steal)

New source/pack/meta node + matrix wiring; clean A/B sibling model id; anti-lookahead verified (boundary-clamped slice in backtest); WF run showing a realized-edge delta vs the sibling baseline with Wilson CI; abstains cleanly when the feed is cold (mirror the `v1+ofx` live-abstain pattern).
