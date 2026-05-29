namespace FrostAura.Foresight.Infrastructure.Persistence;

/// <summary>
/// Seed-time builders for the global built-in model definitions. Each returns a flow-DAG JSON
/// string that DatabaseInitializer writes into the Models.Definition jsonb column. Kept in
/// Infrastructure (not Domain) because the concrete flow shape references node TypeIds defined in
/// FrostAura.Foresight.Application.Flow.Nodes and FrostAura.Foresight.Infrastructure.Flow.Nodes.
/// </summary>
internal static class BuiltInModels
{
    /// <summary>
    /// Foresight v6 — iter-0 baseline. Pure logistic regression over the nine FeaturePack columns
    /// (EMA12/26, MACD triple, Bollinger U/L, log return, 20-bar z-score). The body is the
    /// iteration delta: each iteration of v6 rewrites this method to add a new indicator node
    /// (volume, momentum, regime, etc.) and extend the matrix.columns array. DatabaseInitializer
    /// re-seeds on every boot so a build+restart deploys the change.
    /// </summary>
    public static string BuildForesightV6Flow()
    {
        // Final settings — locked at iter-7's confidence gate (0.04) for production v6.
        //
        // Iteration summary (90-day out-of-sample backtest, BTCUSDT, flat $10 stakes):
        //   iter-0 baseline: 1m 51.89, 5m 51.54, 15m 52.94  (raw feature_pack only)
        //   iter-1 +volume:  1m 51.72, 5m 51.47, 15m 53.10
        //   iter-2 +norm:    1m 51.73, 5m 51.86, 15m 52.85  (vol-normalised features)
        //   iter-3 +mom:     1m 51.71, 5m 51.72, 15m 52.37  (multi-lag returns)
        //   iter-4 trimmed:  1m 51.60, 5m 51.60, 15m 53.00  (8 features, low L2)
        //   iter-5 +cross:   1m 51.55, 5m 51.70, 15m 52.89  (interaction features)
        //   iter-6 gate=0.20: too tight — almost no bets placed.
        //   iter-7 gate=0.04: 1m 53.23 (41k), 5m 53.16 (9k),  15m 55.84 (1.9k)  ← LOCKED HERE
        //   iter-8 gate=0.08: 1m 54.42 (283), 5m 56.81 (1.2k), 15m 61.11 (72)   ← OVERFIT (rejected)
        //   iter-9 gate=0.05: 1m 53.62 (28k), 5m 53.39 (6k),   15m 55.37 (997)
        //
        // Why iter-7 (gate=0.04) is the production choice:
        //   - iter-8 hit 60%+ on 15m but with 72 bets — 95% CI [49.8%, 72.4%], statistically
        //     indistinguishable from coinflip. Cherry-picking a tiny confident sliver.
        //   - iter-7 keeps 22-36% of candles bet across all intervals — large samples (1m =
        //     41k bets, narrow CIs). 15m 55.84% is real signal: 95% CI [53.6%, 58.0%].
        //   - Linear LogReg on technical features caps around 53% raw + 55-56% with confidence
        //     gating. Pushing further would require non-linear models (GBM / ensemble) or live
        //     microstructure features (which break backtestability).
        return /*lang=json,strict*/ """
        {
          "schemaVersion": 1,
          "modelKind": "deterministic",
          "supportsBacktesting": true,
          "warmupCandles": 60,
          "nodes": [
            { "id": "candles", "type": "source.binance.klines",   "params": { "tf": "target", "limit": 60 }, "position": { "x":  40, "y":  40 } },
            { "id": "feat",    "type": "indicator.feature_pack",  "params": {},                              "position": { "x": 320, "y":   0 } },
            { "id": "norm",    "type": "indicator.norm_pack",     "params": {},                              "position": { "x": 320, "y": 160 } },
            { "id": "vol",     "type": "indicator.volume_pack",   "params": {},                              "position": { "x": 320, "y": 320 } },
            { "id": "mom",     "type": "indicator.momentum_pack", "params": {},                              "position": { "x": 320, "y": 480 } },
            { "id": "cross",   "type": "indicator.cross_pack",    "params": {},                              "position": { "x": 320, "y": 640 } },
            { "id": "matrix",  "type": "feature.matrix_builder",
              "params": { "columns": ["ret_1","ret_3","ret_10","z20","bb_pos","ema_spread_atr","vol_z20","atr_pct","mom_x_vol","pos_x_mom","regime_x_mom","trend_x_mom","vol_x_range"] },
              "position": { "x": 640, "y": 240 } },
            { "id": "model",   "type": "model.logistic_regression",
              "params": { "l2": 0.001, "min_confidence": 0.04 },
              "position": { "x": 960, "y": 160 } },
            { "id": "out",     "type": "output.prediction",       "params": {},                              "position": { "x": 1280, "y": 160 } }
          ],
          "edges": [
            { "from": "candles.candles", "to": "feat.candles" },
            { "from": "candles.candles", "to": "norm.candles" },
            { "from": "candles.candles", "to": "vol.candles" },
            { "from": "candles.candles", "to": "mom.candles" },
            { "from": "candles.candles", "to": "cross.candles" },
            { "from": "feat.z20",             "to": "matrix.z20" },
            { "from": "norm.ema_spread_atr",  "to": "matrix.ema_spread_atr" },
            { "from": "norm.bb_pos",          "to": "matrix.bb_pos" },
            { "from": "norm.atr_pct",         "to": "matrix.atr_pct" },
            { "from": "vol.vol_z20",          "to": "matrix.vol_z20" },
            { "from": "mom.ret_1",            "to": "matrix.ret_1" },
            { "from": "mom.ret_3",            "to": "matrix.ret_3" },
            { "from": "mom.ret_10",           "to": "matrix.ret_10" },
            { "from": "cross.mom_x_vol",      "to": "matrix.mom_x_vol" },
            { "from": "cross.pos_x_mom",      "to": "matrix.pos_x_mom" },
            { "from": "cross.regime_x_mom",   "to": "matrix.regime_x_mom" },
            { "from": "cross.trend_x_mom",    "to": "matrix.trend_x_mom" },
            { "from": "cross.vol_x_range",    "to": "matrix.vol_x_range" },
            { "from": "matrix.matrix",        "to": "model.matrix" },
            { "from": "matrix.ready",         "to": "model.ready" },
            { "from": "model.pUp",            "to": "out.pUp" },
            { "from": "model.confidence",     "to": "out.confidence" }
          ]
        }
        """;
    }

    /// <summary>
    /// Foresight | 5m | v1 — a clean-sheet, 5m-only model (NOT a v6 derivative). Where v6 is one
    /// general flow retrained per interval on 5m/15m TA, v1 specialises for 5m by combining three
    /// causal information sources v6 ignores:
    ///   • intraday session seasonality (temporal_pack, from the target candle's open time);
    ///   • 15m regime alignment (htf_regime_pack over a 15m klines source);
    ///   • 1m sub-bar pressure (subbar_pack over a 1m klines source — a cheap order-flow proxy).
    /// plus the proven stationary 5m momentum / vol-normalised / volume features.
    ///
    /// The estimator is deliberately pluggable behind the matrix → pUp+confidence contract; v1
    /// ships on logistic regression first (simplest model that could clear the bar, best overfit
    /// defence), with gradient-boosted trees as a drop-in engine swap once the harness is in place.
    /// No in-node confidence gate — the model bets every candle (matches the live "always bet"
    /// rule and the placedFloor honesty guard); confidence gating is a reporting-layer concern.
    ///
    /// cross_pack is intentionally dropped: hand-crafted product features are a linear-model crutch
    /// that a non-linear engine discovers natively, and on a richer feature set they only add noise.
    /// </summary>
    public static string BuildForesight5mV1Flow()
    {
        return /*lang=json,strict*/ """
        {
          "schemaVersion": 1,
          "modelKind": "deterministic",
          "supportsBacktesting": true,
          "warmupCandles": 60,
          "nodes": [
            { "id": "c5",   "type": "source.binance.klines", "params": { "tf": "target", "limit": 60 }, "position": { "x":  40, "y":   0 } },
            { "id": "c15",  "type": "source.binance.klines", "params": { "tf": "15m",    "limit": 60 }, "position": { "x":  40, "y": 560 } },
            { "id": "c1",   "type": "source.binance.klines", "params": { "tf": "1m",     "limit": 60 }, "position": { "x":  40, "y": 720 } },

            { "id": "mom",  "type": "indicator.momentum_pack", "params": {}, "position": { "x": 320, "y":   0 } },
            { "id": "norm", "type": "indicator.norm_pack",     "params": {}, "position": { "x": 320, "y": 160 } },
            { "id": "vol",  "type": "indicator.volume_pack",   "params": {}, "position": { "x": 320, "y": 320 } },
            { "id": "time", "type": "indicator.temporal_pack", "params": {}, "position": { "x": 320, "y": 480 } },
            { "id": "htf",  "type": "indicator.htf_regime_pack","params": {},"position": { "x": 320, "y": 600 } },
            { "id": "sub",  "type": "indicator.subbar_pack",   "params": { "window": 15 }, "position": { "x": 320, "y": 760 } },

            { "id": "matrix", "type": "feature.matrix_builder",
              "params": { "columns": [
                "ret_1","ret_3","ret_5","ret_10",
                "ema_spread_atr","px_vs_ema26_atr","bb_pos","atr_pct",
                "vol_z20","up_vol_ratio","obv_z20",
                "hour_sin","hour_cos","dow_sin","dow_cos","is_us_session","is_eu_session","is_weekend",
                "htf_ema_spread_atr","htf_px_vs_ema26_atr","htf_atr_pct","htf_rsi","htf_ret_4",
                "subbar_rvol","subbar_up_ratio","subbar_vol_skew","subbar_ret_5"
              ] },
              "position": { "x": 660, "y": 320 } },

            { "id": "model", "type": "model.logistic_regression",
              "params": { "l2": 0.005 },
              "position": { "x": 980, "y": 320 } },
            { "id": "out",   "type": "output.prediction", "params": {}, "position": { "x": 1300, "y": 320 } }
          ],
          "edges": [
            { "from": "c5.candles",  "to": "mom.candles" },
            { "from": "c5.candles",  "to": "norm.candles" },
            { "from": "c5.candles",  "to": "vol.candles" },
            { "from": "c15.candles", "to": "htf.candles" },
            { "from": "c1.candles",  "to": "sub.candles" },

            { "from": "mom.ret_1",  "to": "matrix.ret_1" },
            { "from": "mom.ret_3",  "to": "matrix.ret_3" },
            { "from": "mom.ret_5",  "to": "matrix.ret_5" },
            { "from": "mom.ret_10", "to": "matrix.ret_10" },

            { "from": "norm.ema_spread_atr",  "to": "matrix.ema_spread_atr" },
            { "from": "norm.px_vs_ema26_atr", "to": "matrix.px_vs_ema26_atr" },
            { "from": "norm.bb_pos",          "to": "matrix.bb_pos" },
            { "from": "norm.atr_pct",         "to": "matrix.atr_pct" },

            { "from": "vol.vol_z20",      "to": "matrix.vol_z20" },
            { "from": "vol.up_vol_ratio", "to": "matrix.up_vol_ratio" },
            { "from": "vol.obv_z20",      "to": "matrix.obv_z20" },

            { "from": "time.hour_sin",      "to": "matrix.hour_sin" },
            { "from": "time.hour_cos",      "to": "matrix.hour_cos" },
            { "from": "time.dow_sin",       "to": "matrix.dow_sin" },
            { "from": "time.dow_cos",       "to": "matrix.dow_cos" },
            { "from": "time.is_us_session", "to": "matrix.is_us_session" },
            { "from": "time.is_eu_session", "to": "matrix.is_eu_session" },
            { "from": "time.is_weekend",    "to": "matrix.is_weekend" },

            { "from": "htf.htf_ema_spread_atr",  "to": "matrix.htf_ema_spread_atr" },
            { "from": "htf.htf_px_vs_ema26_atr", "to": "matrix.htf_px_vs_ema26_atr" },
            { "from": "htf.htf_atr_pct",         "to": "matrix.htf_atr_pct" },
            { "from": "htf.htf_rsi",             "to": "matrix.htf_rsi" },
            { "from": "htf.htf_ret_4",           "to": "matrix.htf_ret_4" },

            { "from": "sub.subbar_rvol",     "to": "matrix.subbar_rvol" },
            { "from": "sub.subbar_up_ratio", "to": "matrix.subbar_up_ratio" },
            { "from": "sub.subbar_vol_skew", "to": "matrix.subbar_vol_skew" },
            { "from": "sub.subbar_ret_5",    "to": "matrix.subbar_ret_5" },

            { "from": "matrix.matrix",    "to": "model.matrix" },
            { "from": "matrix.ready",     "to": "model.ready" },
            { "from": "model.pUp",        "to": "out.pUp" },
            { "from": "model.confidence", "to": "out.confidence" }
          ]
        }
        """;
    }

    /// <summary>
    /// Foresight | 5m | v1+ofx — v1 plus backtestable ORDER-FLOW microstructure: taker-volume
    /// imbalance, CVD momentum, large-order skew, and trade intensity, sourced from the historical
    /// microstructure store (Binance aggTrades dumps). This is the lever the plan bets on to push
    /// past the ~53% technical-analysis ceiling — the signal the live LLM uses to reach ~60%, now
    /// finally available in a deterministic, backtestable flow.
    ///
    /// Backtest/training only for now: the daily dumps lag real time by ~1 day, so live inference has
    /// no microstructure for the most recent bars and this model abstains live until a live
    /// recent-trades feed is added. Kept separate from v1 (which stays fully live-capable).
    /// </summary>
    public static string BuildForesight5mV1OfxFlow()
    {
        return /*lang=json,strict*/ """
        {
          "schemaVersion": 1,
          "modelKind": "deterministic",
          "supportsBacktesting": true,
          "warmupCandles": 60,
          "nodes": [
            { "id": "c5",   "type": "source.binance.klines", "params": { "tf": "target", "limit": 60 }, "position": { "x":  40, "y":   0 } },
            { "id": "c15",  "type": "source.binance.klines", "params": { "tf": "15m",    "limit": 60 }, "position": { "x":  40, "y": 560 } },
            { "id": "c1",   "type": "source.binance.klines", "params": { "tf": "1m",     "limit": 60 }, "position": { "x":  40, "y": 720 } },
            { "id": "ofx",  "type": "source.microstructure.orderflow", "params": { "limit": 60 },        "position": { "x":  40, "y": 900 } },

            { "id": "mom",  "type": "indicator.momentum_pack", "params": {}, "position": { "x": 320, "y":   0 } },
            { "id": "norm", "type": "indicator.norm_pack",     "params": {}, "position": { "x": 320, "y": 160 } },
            { "id": "vol",  "type": "indicator.volume_pack",   "params": {}, "position": { "x": 320, "y": 320 } },
            { "id": "time", "type": "indicator.temporal_pack", "params": {}, "position": { "x": 320, "y": 480 } },
            { "id": "htf",  "type": "indicator.htf_regime_pack","params": {},"position": { "x": 320, "y": 600 } },
            { "id": "sub",  "type": "indicator.subbar_pack",   "params": { "window": 15 }, "position": { "x": 320, "y": 760 } },
            { "id": "of",   "type": "indicator.orderflow_pack","params": {}, "position": { "x": 320, "y": 900 } },
            { "id": "deriv","type": "indicator.derivatives_pack","params": {}, "position": { "x": 320, "y": 1040 } },

            { "id": "matrix", "type": "feature.matrix_builder",
              "params": { "columns": [
                "ret_1","ret_3","ret_5","ret_10",
                "ema_spread_atr","px_vs_ema26_atr","bb_pos","atr_pct",
                "vol_z20","up_vol_ratio","obv_z20",
                "hour_sin","hour_cos","dow_sin","dow_cos","is_us_session","is_eu_session","is_weekend",
                "htf_ema_spread_atr","htf_px_vs_ema26_atr","htf_atr_pct","htf_rsi","htf_ret_4",
                "subbar_rvol","subbar_up_ratio","subbar_vol_skew","subbar_ret_5",
                "of_imbalance","of_count_imbalance","of_large_skew","of_cvd_z","of_intensity_z",
                "d_oi_mom","d_oi_z","d_toptrader_ls","d_taker_ls","d_ls_ratio"
              ] },
              "position": { "x": 660, "y": 320 } },

            { "id": "model", "type": "model.logistic_regression",
              "params": { "l2": 0.005 },
              "position": { "x": 980, "y": 320 } },
            { "id": "out",   "type": "output.prediction", "params": {}, "position": { "x": 1300, "y": 320 } }
          ],
          "edges": [
            { "from": "c5.candles",  "to": "mom.candles" },
            { "from": "c5.candles",  "to": "norm.candles" },
            { "from": "c5.candles",  "to": "vol.candles" },
            { "from": "c15.candles", "to": "htf.candles" },
            { "from": "c1.candles",  "to": "sub.candles" },
            { "from": "ofx.bars",    "to": "of.bars" },

            { "from": "mom.ret_1",  "to": "matrix.ret_1" },
            { "from": "mom.ret_3",  "to": "matrix.ret_3" },
            { "from": "mom.ret_5",  "to": "matrix.ret_5" },
            { "from": "mom.ret_10", "to": "matrix.ret_10" },

            { "from": "norm.ema_spread_atr",  "to": "matrix.ema_spread_atr" },
            { "from": "norm.px_vs_ema26_atr", "to": "matrix.px_vs_ema26_atr" },
            { "from": "norm.bb_pos",          "to": "matrix.bb_pos" },
            { "from": "norm.atr_pct",         "to": "matrix.atr_pct" },

            { "from": "vol.vol_z20",      "to": "matrix.vol_z20" },
            { "from": "vol.up_vol_ratio", "to": "matrix.up_vol_ratio" },
            { "from": "vol.obv_z20",      "to": "matrix.obv_z20" },

            { "from": "time.hour_sin",      "to": "matrix.hour_sin" },
            { "from": "time.hour_cos",      "to": "matrix.hour_cos" },
            { "from": "time.dow_sin",       "to": "matrix.dow_sin" },
            { "from": "time.dow_cos",       "to": "matrix.dow_cos" },
            { "from": "time.is_us_session", "to": "matrix.is_us_session" },
            { "from": "time.is_eu_session", "to": "matrix.is_eu_session" },
            { "from": "time.is_weekend",    "to": "matrix.is_weekend" },

            { "from": "htf.htf_ema_spread_atr",  "to": "matrix.htf_ema_spread_atr" },
            { "from": "htf.htf_px_vs_ema26_atr", "to": "matrix.htf_px_vs_ema26_atr" },
            { "from": "htf.htf_atr_pct",         "to": "matrix.htf_atr_pct" },
            { "from": "htf.htf_rsi",             "to": "matrix.htf_rsi" },
            { "from": "htf.htf_ret_4",           "to": "matrix.htf_ret_4" },

            { "from": "sub.subbar_rvol",     "to": "matrix.subbar_rvol" },
            { "from": "sub.subbar_up_ratio", "to": "matrix.subbar_up_ratio" },
            { "from": "sub.subbar_vol_skew", "to": "matrix.subbar_vol_skew" },
            { "from": "sub.subbar_ret_5",    "to": "matrix.subbar_ret_5" },

            { "from": "of.of_imbalance",       "to": "matrix.of_imbalance" },
            { "from": "of.of_count_imbalance", "to": "matrix.of_count_imbalance" },
            { "from": "of.of_large_skew",      "to": "matrix.of_large_skew" },
            { "from": "of.of_cvd_z",           "to": "matrix.of_cvd_z" },
            { "from": "of.of_intensity_z",     "to": "matrix.of_intensity_z" },

            { "from": "ofx.bars",              "to": "deriv.bars" },
            { "from": "deriv.d_oi_mom",        "to": "matrix.d_oi_mom" },
            { "from": "deriv.d_oi_z",          "to": "matrix.d_oi_z" },
            { "from": "deriv.d_toptrader_ls",  "to": "matrix.d_toptrader_ls" },
            { "from": "deriv.d_taker_ls",      "to": "matrix.d_taker_ls" },
            { "from": "deriv.d_ls_ratio",      "to": "matrix.d_ls_ratio" },

            { "from": "matrix.matrix",    "to": "model.matrix" },
            { "from": "matrix.ready",     "to": "model.ready" },
            { "from": "model.pUp",        "to": "out.pUp" },
            { "from": "model.confidence", "to": "out.confidence" }
          ]
        }
        """;
    }

    /// <summary>
    /// Foresight | 5m | v1+ofx2 — v1+ofx plus the intra-bar (high-frequency) order-flow pack. Identical
    /// to the ofx flow except a single extra node (<c>indicator.microflow_pack</c>) reading the same
    /// <c>ofx.bars</c> and three extra matrix columns, so a walk-forward A/B against v1+ofx isolates
    /// exactly the value of intra-bar resolution. See <see cref="ModelIds.ForesightFiveMinV1Ofx2"/>.
    /// </summary>
    public static string BuildForesight5mV1Ofx2Flow()
    {
        return /*lang=json,strict*/ """
        {
          "schemaVersion": 1,
          "modelKind": "deterministic",
          "supportsBacktesting": true,
          "warmupCandles": 60,
          "nodes": [
            { "id": "c5",   "type": "source.binance.klines", "params": { "tf": "target", "limit": 60 }, "position": { "x":  40, "y":   0 } },
            { "id": "c15",  "type": "source.binance.klines", "params": { "tf": "15m",    "limit": 60 }, "position": { "x":  40, "y": 560 } },
            { "id": "c1",   "type": "source.binance.klines", "params": { "tf": "1m",     "limit": 60 }, "position": { "x":  40, "y": 720 } },
            { "id": "ofx",  "type": "source.microstructure.orderflow", "params": { "limit": 60 },        "position": { "x":  40, "y": 900 } },

            { "id": "mom",  "type": "indicator.momentum_pack", "params": {}, "position": { "x": 320, "y":   0 } },
            { "id": "norm", "type": "indicator.norm_pack",     "params": {}, "position": { "x": 320, "y": 160 } },
            { "id": "vol",  "type": "indicator.volume_pack",   "params": {}, "position": { "x": 320, "y": 320 } },
            { "id": "time", "type": "indicator.temporal_pack", "params": {}, "position": { "x": 320, "y": 480 } },
            { "id": "htf",  "type": "indicator.htf_regime_pack","params": {},"position": { "x": 320, "y": 600 } },
            { "id": "sub",  "type": "indicator.subbar_pack",   "params": { "window": 15 }, "position": { "x": 320, "y": 760 } },
            { "id": "of",   "type": "indicator.orderflow_pack","params": {}, "position": { "x": 320, "y": 900 } },
            { "id": "mf",   "type": "indicator.microflow_pack","params": {}, "position": { "x": 320, "y": 1180 } },
            { "id": "deriv","type": "indicator.derivatives_pack","params": {}, "position": { "x": 320, "y": 1040 } },

            { "id": "matrix", "type": "feature.matrix_builder",
              "params": { "columns": [
                "ret_1","ret_3","ret_5","ret_10",
                "ema_spread_atr","px_vs_ema26_atr","bb_pos","atr_pct",
                "vol_z20","up_vol_ratio","obv_z20",
                "hour_sin","hour_cos","dow_sin","dow_cos","is_us_session","is_eu_session","is_weekend",
                "htf_ema_spread_atr","htf_px_vs_ema26_atr","htf_atr_pct","htf_rsi","htf_ret_4",
                "subbar_rvol","subbar_up_ratio","subbar_vol_skew","subbar_ret_5",
                "of_imbalance","of_count_imbalance","of_large_skew","of_cvd_z","of_intensity_z",
                "mf_late_imbalance","mf_imbalance_accel","mf_late_intensity",
                "d_oi_mom","d_oi_z","d_toptrader_ls","d_taker_ls","d_ls_ratio"
              ] },
              "position": { "x": 660, "y": 320 } },

            { "id": "model", "type": "model.logistic_regression",
              "params": { "l2": 0.005 },
              "position": { "x": 980, "y": 320 } },
            { "id": "out",   "type": "output.prediction", "params": {}, "position": { "x": 1300, "y": 320 } }
          ],
          "edges": [
            { "from": "c5.candles",  "to": "mom.candles" },
            { "from": "c5.candles",  "to": "norm.candles" },
            { "from": "c5.candles",  "to": "vol.candles" },
            { "from": "c15.candles", "to": "htf.candles" },
            { "from": "c1.candles",  "to": "sub.candles" },
            { "from": "ofx.bars",    "to": "of.bars" },
            { "from": "ofx.bars",    "to": "mf.bars" },

            { "from": "mom.ret_1",  "to": "matrix.ret_1" },
            { "from": "mom.ret_3",  "to": "matrix.ret_3" },
            { "from": "mom.ret_5",  "to": "matrix.ret_5" },
            { "from": "mom.ret_10", "to": "matrix.ret_10" },

            { "from": "norm.ema_spread_atr",  "to": "matrix.ema_spread_atr" },
            { "from": "norm.px_vs_ema26_atr", "to": "matrix.px_vs_ema26_atr" },
            { "from": "norm.bb_pos",          "to": "matrix.bb_pos" },
            { "from": "norm.atr_pct",         "to": "matrix.atr_pct" },

            { "from": "vol.vol_z20",      "to": "matrix.vol_z20" },
            { "from": "vol.up_vol_ratio", "to": "matrix.up_vol_ratio" },
            { "from": "vol.obv_z20",      "to": "matrix.obv_z20" },

            { "from": "time.hour_sin",      "to": "matrix.hour_sin" },
            { "from": "time.hour_cos",      "to": "matrix.hour_cos" },
            { "from": "time.dow_sin",       "to": "matrix.dow_sin" },
            { "from": "time.dow_cos",       "to": "matrix.dow_cos" },
            { "from": "time.is_us_session", "to": "matrix.is_us_session" },
            { "from": "time.is_eu_session", "to": "matrix.is_eu_session" },
            { "from": "time.is_weekend",    "to": "matrix.is_weekend" },

            { "from": "htf.htf_ema_spread_atr",  "to": "matrix.htf_ema_spread_atr" },
            { "from": "htf.htf_px_vs_ema26_atr", "to": "matrix.htf_px_vs_ema26_atr" },
            { "from": "htf.htf_atr_pct",         "to": "matrix.htf_atr_pct" },
            { "from": "htf.htf_rsi",             "to": "matrix.htf_rsi" },
            { "from": "htf.htf_ret_4",           "to": "matrix.htf_ret_4" },

            { "from": "sub.subbar_rvol",     "to": "matrix.subbar_rvol" },
            { "from": "sub.subbar_up_ratio", "to": "matrix.subbar_up_ratio" },
            { "from": "sub.subbar_vol_skew", "to": "matrix.subbar_vol_skew" },
            { "from": "sub.subbar_ret_5",    "to": "matrix.subbar_ret_5" },

            { "from": "of.of_imbalance",       "to": "matrix.of_imbalance" },
            { "from": "of.of_count_imbalance", "to": "matrix.of_count_imbalance" },
            { "from": "of.of_large_skew",      "to": "matrix.of_large_skew" },
            { "from": "of.of_cvd_z",           "to": "matrix.of_cvd_z" },
            { "from": "of.of_intensity_z",     "to": "matrix.of_intensity_z" },

            { "from": "mf.mf_late_imbalance",  "to": "matrix.mf_late_imbalance" },
            { "from": "mf.mf_imbalance_accel", "to": "matrix.mf_imbalance_accel" },
            { "from": "mf.mf_late_intensity",  "to": "matrix.mf_late_intensity" },

            { "from": "ofx.bars",              "to": "deriv.bars" },
            { "from": "deriv.d_oi_mom",        "to": "matrix.d_oi_mom" },
            { "from": "deriv.d_oi_z",          "to": "matrix.d_oi_z" },
            { "from": "deriv.d_toptrader_ls",  "to": "matrix.d_toptrader_ls" },
            { "from": "deriv.d_taker_ls",      "to": "matrix.d_taker_ls" },
            { "from": "deriv.d_ls_ratio",      "to": "matrix.d_ls_ratio" },

            { "from": "matrix.matrix",    "to": "model.matrix" },
            { "from": "matrix.ready",     "to": "model.ready" },
            { "from": "model.pUp",        "to": "out.pUp" },
            { "from": "model.confidence", "to": "out.confidence" }
          ]
        }
        """;
    }

    /// <summary>
    /// Foresight | 5m | v2 — the non-linear engine. IDENTICAL leakage-proof feature matrix to v1
    /// (momentum / vol-normalised / volume / temporal / 15m regime / 1m sub-bar), but fit with
    /// gradient-boosted trees (<c>model.gbt</c>) instead of logistic regression. This is the one
    /// modeling lever the v1/v6 iteration logs flag as genuinely untested — the v6 log projects a
    /// non-linear model "could plausibly push to 56-58%, possibly higher with gating". GBT captures
    /// the feature interactions a linear model can't, on the same honest data, so a walk-forward A/B
    /// vs v1 isolates exactly the value of non-linearity.
    ///
    /// Hyper-parameters are deliberately conservative (shallow depth 3, large min_samples_leaf,
    /// row/col subsampling, strong L2) — a thin-edge signal punishes capacity, so we resist
    /// overfitting hard (the iter-8 trap). A <c>min_confidence</c> gate drives the high-conviction
    /// reporting subset (the honest path to 60% on the bets we'd most believe) without the headline
    /// diverging from the always-bet live number.
    /// </summary>
    public static string BuildForesight5mV2Flow()
    {
        return /*lang=json,strict*/ """
        {
          "schemaVersion": 1,
          "modelKind": "deterministic",
          "supportsBacktesting": true,
          "warmupCandles": 60,
          "nodes": [
            { "id": "c5",   "type": "source.binance.klines", "params": { "tf": "target", "limit": 60 }, "position": { "x":  40, "y":   0 } },
            { "id": "c15",  "type": "source.binance.klines", "params": { "tf": "15m",    "limit": 60 }, "position": { "x":  40, "y": 560 } },
            { "id": "c1",   "type": "source.binance.klines", "params": { "tf": "1m",     "limit": 60 }, "position": { "x":  40, "y": 720 } },

            { "id": "mom",  "type": "indicator.momentum_pack", "params": {}, "position": { "x": 320, "y":   0 } },
            { "id": "norm", "type": "indicator.norm_pack",     "params": {}, "position": { "x": 320, "y": 160 } },
            { "id": "vol",  "type": "indicator.volume_pack",   "params": {}, "position": { "x": 320, "y": 320 } },
            { "id": "time", "type": "indicator.temporal_pack", "params": {}, "position": { "x": 320, "y": 480 } },
            { "id": "htf",  "type": "indicator.htf_regime_pack","params": {},"position": { "x": 320, "y": 600 } },
            { "id": "sub",  "type": "indicator.subbar_pack",   "params": { "window": 15 }, "position": { "x": 320, "y": 760 } },

            { "id": "matrix", "type": "feature.matrix_builder",
              "params": { "columns": [
                "ret_1","ret_3","ret_5","ret_10",
                "ema_spread_atr","px_vs_ema26_atr","bb_pos","atr_pct",
                "vol_z20","up_vol_ratio","obv_z20",
                "hour_sin","hour_cos","dow_sin","dow_cos","is_us_session","is_eu_session","is_weekend",
                "htf_ema_spread_atr","htf_px_vs_ema26_atr","htf_atr_pct","htf_rsi","htf_ret_4",
                "subbar_rvol","subbar_up_ratio","subbar_vol_skew","subbar_ret_5"
              ] },
              "position": { "x": 660, "y": 320 } },

            { "id": "model", "type": "model.gbt",
              "params": { "n_estimators": 200, "max_depth": 3, "learning_rate": 0.03, "min_samples_leaf": 250, "subsample": 0.7, "colsample": 0.7, "l2": 2.0, "min_confidence": 0.15 },
              "position": { "x": 980, "y": 320 } },
            { "id": "out",   "type": "output.prediction", "params": {}, "position": { "x": 1300, "y": 320 } }
          ],
          "edges": [
            { "from": "c5.candles",  "to": "mom.candles" },
            { "from": "c5.candles",  "to": "norm.candles" },
            { "from": "c5.candles",  "to": "vol.candles" },
            { "from": "c15.candles", "to": "htf.candles" },
            { "from": "c1.candles",  "to": "sub.candles" },

            { "from": "mom.ret_1",  "to": "matrix.ret_1" },
            { "from": "mom.ret_3",  "to": "matrix.ret_3" },
            { "from": "mom.ret_5",  "to": "matrix.ret_5" },
            { "from": "mom.ret_10", "to": "matrix.ret_10" },

            { "from": "norm.ema_spread_atr",  "to": "matrix.ema_spread_atr" },
            { "from": "norm.px_vs_ema26_atr", "to": "matrix.px_vs_ema26_atr" },
            { "from": "norm.bb_pos",          "to": "matrix.bb_pos" },
            { "from": "norm.atr_pct",         "to": "matrix.atr_pct" },

            { "from": "vol.vol_z20",      "to": "matrix.vol_z20" },
            { "from": "vol.up_vol_ratio", "to": "matrix.up_vol_ratio" },
            { "from": "vol.obv_z20",      "to": "matrix.obv_z20" },

            { "from": "time.hour_sin",      "to": "matrix.hour_sin" },
            { "from": "time.hour_cos",      "to": "matrix.hour_cos" },
            { "from": "time.dow_sin",       "to": "matrix.dow_sin" },
            { "from": "time.dow_cos",       "to": "matrix.dow_cos" },
            { "from": "time.is_us_session", "to": "matrix.is_us_session" },
            { "from": "time.is_eu_session", "to": "matrix.is_eu_session" },
            { "from": "time.is_weekend",    "to": "matrix.is_weekend" },

            { "from": "htf.htf_ema_spread_atr",  "to": "matrix.htf_ema_spread_atr" },
            { "from": "htf.htf_px_vs_ema26_atr", "to": "matrix.htf_px_vs_ema26_atr" },
            { "from": "htf.htf_atr_pct",         "to": "matrix.htf_atr_pct" },
            { "from": "htf.htf_rsi",             "to": "matrix.htf_rsi" },
            { "from": "htf.htf_ret_4",           "to": "matrix.htf_ret_4" },

            { "from": "sub.subbar_rvol",     "to": "matrix.subbar_rvol" },
            { "from": "sub.subbar_up_ratio", "to": "matrix.subbar_up_ratio" },
            { "from": "sub.subbar_vol_skew", "to": "matrix.subbar_vol_skew" },
            { "from": "sub.subbar_ret_5",    "to": "matrix.subbar_ret_5" },

            { "from": "matrix.matrix",    "to": "model.matrix" },
            { "from": "matrix.ready",     "to": "model.ready" },
            { "from": "model.pUp",        "to": "out.pUp" },
            { "from": "model.confidence", "to": "out.confidence" }
          ]
        }
        """;
    }
}
