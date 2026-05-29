using System.Text.Json;
using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Application.Flow.Nodes;
using FrostAura.Foresight.Application.Markets;
using FrostAura.Foresight.Application.Models;
using FrostAura.Foresight.Domain.Backtesting;
using FrostAura.Foresight.Domain.MarketData;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Domain.Trading;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Application.Backtesting;

/// <summary>
/// Replays a deterministic model over historical candles with a configurable staking strategy.
/// The runner is pure orchestration — strategy + step math live in <see cref="StakingEngine"/>
/// (shared with live), flow execution in <see cref="IFlowExecutor"/>, and persistence is handled
/// by the caller (the runner returns a fully-populated <see cref="BacktestOutcome"/> and the
/// controller writes the row).
///
/// Bankruptcy behaviour is configurable per run: with <c>allowBorrow=true</c> (default) the
/// balance can dip negative and the shortfall is recorded; with <c>allowBorrow=false</c> the
/// run halts the moment the next sized bet exceeds the bankroll — matches the live paper-
/// trading bust contract. Zero-crossings accumulate every sign change in either mode.
/// </summary>
public sealed class BacktestRunner
{
    private readonly IFlowExecutor _executor;
    private readonly IHistoricalCandleProvider _candles;
    private readonly IHistoricalMicrostructureProvider? _micro;
    private readonly IVenuePriceStore? _venuePrices;
    private readonly ILogger<BacktestRunner> _logger;

    public BacktestRunner(IFlowExecutor executor, IHistoricalCandleProvider candles, ILogger<BacktestRunner> logger,
        IHistoricalMicrostructureProvider? micro = null,
        IVenuePriceStore? venuePrices = null)
    {
        _executor = executor;
        _candles = candles;
        _logger = logger;
        _micro = micro;
        _venuePrices = venuePrices;
    }

    public async Task<BacktestOutcome> RunAsync(
        FlowDefinition flow,
        string trainedStateJson,
        Guid tenantId,
        Guid modelId,
        string symbol,
        string interval,
        long startMs,
        long endMs,
        decimal initialBalance,
        decimal initialBetSize,
        bool allowBorrow,
        IStakingStrategy strategy,
        IProgress<BacktestProgress>? progress,
        CancellationToken ct,
        int horizonSteps = 2,
        bool applyGate = false,
        decimal? gateBand = null)
    {
        if (!flow.SupportsBacktesting)
            throw new InvalidOperationException("Flow does not support backtesting (set supportsBacktesting=true and remove live-only data sources).");

        var intervalMs = IntervalMs(interval);
        var warmupMs = (long)flow.WarmupCandles * intervalMs;
        var candles = await _candles.GetRangeAsync(symbol, interval, startMs - warmupMs, endMs, ct);
        if (candles.Count <= flow.WarmupCandles + 1)
            throw new InvalidOperationException($"Not enough candles ({candles.Count}) — need at least {flow.WarmupCandles + 2}.");

        // Pre-fetch every OTHER supported timeframe once up-front so the slice provider can serve
        // multi-timeframe flows (e.g. a 15m regime feature feeding a 5m model) from memory. Each
        // off-tf gets its OWN warmup window based on its interval length so its indicators have
        // enough coverage at iteration start; without per-tf scaling, off-tf features stay null
        // forever and the matrix never readies.
        var offTfCandles = new Dictionary<string, IReadOnlyList<HistoricalCandle>>();
        foreach (var otherTf in FrostAura.Foresight.Domain.MarketData.SupportedSymbols.Intervals)
        {
            if (otherTf == interval) continue;
            var offWarmupMs = Math.Max(warmupMs, 60L * IntervalMs(otherTf));   // ≥ 60 off-tf candles of warmup
            try { offTfCandles[otherTf] = await _candles.GetRangeAsync(symbol, otherTf, startMs - offWarmupMs, endMs, ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "Pre-fetch failed for {Tf}; flow nodes that need it will get empty data", otherTf); offTfCandles[otherTf] = Array.Empty<HistoricalCandle>(); }
        }

        // Pre-fetch microstructure bars once (when a provider is wired). Per-iteration slices below
        // clamp them by close-time so order-flow features stay anti-lookahead, exactly like candles.
        IReadOnlyList<MicrostructureBar> microPool = Array.Empty<MicrostructureBar>();
        if (_micro is not null)
        {
            try { microPool = await _micro.GetRangeAsync(symbol, interval, startMs - warmupMs, endMs, ct); }
            // Actionable config errors (e.g. the on-demand order-flow ingest cap for a too-long
            // window) surface as a clear failed backtest instead of silently abstaining on everything.
            catch (InvalidOperationException) { throw; }
            catch (Exception ex) { _logger.LogDebug(ex, "Microstructure pre-fetch failed; order-flow nodes will see empty data"); }
        }

        // Parse trained state (LR + LogReg coefficients) into the JsonElement shape the regression
        // nodes expect. The trainer's JSON has top-level keys "modelLinearRegression" /
        // "modelLogisticRegression"; node code reads "model.linear_regression" / "model.logistic_regression".
        // Bridge here so the trained-state contract stays human-readable while the runtime keys
        // align with the node TypeIds. The parent JsonDocument stays in a `using` so its
        // JsonElements remain valid until RemapTrainedState clones a detached element.
        JsonElement? trainedFor = null;
        if (!string.IsNullOrWhiteSpace(trainedStateJson))
        {
            using var doc = JsonDocument.Parse(trainedStateJson);
            trainedFor = RemapTrainedState(doc.RootElement, interval);
        }

        var bets = new List<BacktestBet>();
        var balance = initialBalance;
        // Odds-mode: track lastStake and lastWon for placement-time sizing.
        var lastStake = initialBetSize;
        var lastWon = true;
        var peakBalance = initialBalance;
        var troughBalance = initialBalance;
        var maxDrawdown = 0m;
        var peakBorrowed = 0m;
        var zeroCrossings = 0;
        var maxMartingaleStep = 0;       // deepest doubling chain encountered
        int won = 0, placed = 0;
        int syntheticCount = 0;

        // Headline hit-rate is now the UNGATED, bet-every-candle number — the live-equivalent
        // strategy (the model no longer abstains in-node). `min_confidence`, if the model node
        // carries one, only drives a SECONDARY high-conviction reporting subset so we can still see
        // "accuracy on the bets we'd most believe" without the headline diverging from live.
        var minConf = 0m;
        foreach (var n in flow.Nodes)
        {
            var c = Flow.Nodes.NodeParams.GetDecimal(n.Params, "min_confidence", 0m);
            if (c > minConf) minConf = c;
        }
        int gatedWon = 0, gatedPlaced = 0;
        double brierSum = 0.0;           // Σ (pUp - actualUp)² — calibration / sharpness signal

        // Effective confidence-gate band. An explicit gateBand wins (the sweep path — vary the
        // no-bet band to find its profitability/risk-reduction sweet spot); falling back to the
        // default ±2pp band when applyGate is on; 0 = bet every candle. A band > 0 skips candles
        // inside the band exactly as the chart + live paper gate do.
        var effectiveGateBand = gateBand ?? (applyGate ? StakingEngine.DefaultNoBetBand : 0m);

        // Progress is reported as CANDLES PROCESSED / TOTAL CANDLES, not bets placed. Bets are a
        // fraction of candles (gated/abstaining models bet rarely or never), so a bets-based fraction
        // never reaches 100% and barely moves — that was the "% / candles no longer showing" bug.
        var totalCandles = Math.Max(0, (candles.Count - horizonSteps) - flow.WarmupCandles);

        // Horizon-ahead canon (matches how a live bet is actually placed): at the decision moment we
        // only have data through the last CLOSED candle (index i = "previous candle"). The target is
        // candle i+horizonSteps, graded close(i+horizonSteps) vs close(i). horizon=2 (default) skips
        // candle i+1 — the candle "forming" while a slow decision is made — so it's never an input or
        // reference. horizon=1 predicts the very next candle directly, viable now that the decision is
        // an instant deterministic compute at candle close. Either way candle i+1+ is NEVER a feature
        // input: features see candles[0..i] only (the slice is trimmed to i+1 items = indices 0..i).
        for (var i = flow.WarmupCandles; i < candles.Count - horizonSteps; i++)
        {
            if (ct.IsCancellationRequested) break;

            var anchor = candles[i];                   // previous closed candle — decision edge AND direction reference
            var target = candles[i + horizonSteps];    // bettable candle — what we predict & settle against

            // Boundary = the moment the bet is placed, which is the CLOSE time of the anchor
            // candle (== open time of candle[i+1]). At this instant: every target-tf candle 0..i
            // has fully closed, AND every off-tf candle whose close-time ≤ this instant has also
            // closed. The slice provider uses this exact moment to gate off-tf data so it can't
            // leak the close of a still-open higher-timeframe candle.
            var boundaryMs = anchor.OpenTime + intervalMs;
            var slice = new BacktestSliceProvider(candles, anchor.OpenTime, interval, offTfCandles, boundaryMs);
            var microSlice = _micro is null ? null : new MicrostructureSlice(microPool, intervalMs, boundaryMs);
            var ctx = new FlowContext(tenantId, modelId, symbol, interval, target.OpenTime, 1,
                FlowMode.Backtest, slice, trainedFor, microSlice);

            decimal pUpRaw;
            try
            {
                var result = await _executor.ExecuteAsync(flow, ctx, ct);
                if (result.OutputPrediction.GetValueOrDefault("pUp") is not decimal pUp) continue;
                pUpRaw = pUp;
                // Honest no-opinion: a model that emits *exactly* 0.5 is signalling "I refuse to
                // predict." Treat as abstention — no bet placed, no metric incremented, balance
                // untouched. Mirrors LivePredictionService's resolution rule (DirectionHit=null
                // when DirectionUpProbability==0.50). Without this, Flat Baseline (which always
                // emits 0.5) falls through to StakingEngine.Step where `>= 0.5` collapses it to
                // "always UP" and the supposedly-risk-averse control busts to zero on the slight
                // bearish bias of the window. Strict equality only — opinionated models that land
                // on 0.50 by coincidence are vanishingly rare and would correctly abstain anyway.
                if (pUpRaw == 0.5m) continue;
                // Optional confidence gate (the "safety" mode the user toggles): skip candles the
                // model isn't confident enough to bet — the SAME ±2pp no-bet band the chart shows.
                // The skipped candle places no bet, so balance and Martingale step are untouched,
                // exactly as if the model had abstained. Off by default → bet every candle.
                if (effectiveGateBand > 0m && StakingEngine.IsNoBet(pUpRaw, effectiveGateBand)) continue;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Backtest skipping candle {OpenTime} due to flow execution error", anchor.OpenTime);
                continue;
            }

            // ── Odds-based placement-time settlement ────────────────────────────────────────
            // 1. Fetch entry quote for this candle (anti-look-ahead: latest ObservedAt ≤ openTime).
            Domain.Markets.EntryQuote entry;
            if (_venuePrices is not null)
            {
                entry = await _venuePrices.EnsureEntryAsync("polymarket", symbol, interval, target.OpenTime, pUpRaw, ct);
            }
            else
            {
                // No price store wired (e.g. unit-test harness without DI) — synthesise in-memory.
                var rawYes = 0.5m + (pUpRaw - 0.5m) * 0.8m;
                var synYes = Math.Max(0.02m, Math.Min(0.98m, rawYes));
                entry = new Domain.Markets.EntryQuote(synYes, 1m - synYes, true, null);
            }

            // 2. Determine outcome (pure-candle backtest: close(target) vs close(anchor)).
            var outcomeUp = target.Close > anchor.Close;

            // 3. Size the bet via the strategy using this candle's edge inputs.
            var entryInputs = new StakingInputs(pUpRaw, entry.YesPrice, entry.NoPrice);
            var stake = strategy.NextBetSize(new StrategyStep(lastStake, lastWon, initialBetSize, balance, entryInputs));

            if (stake <= 0m)
            {
                // Strategy returned 0 — skip this candle (edge/gate signal).
                continue;
            }

            // 4. Strict-bust check: if this stake would exceed balance, halt (no borrow).
            if (!allowBorrow && stake > balance) break;

            // 5. Settle via shared engine.
            StakingStep step;
            try
            {
                var side = StakingEngine.DecideSide(pUpRaw);
                var entryPrice = side == "UP" ? entry.YesPrice : entry.NoPrice;
                step = StakingEngine.Settle(side, entryPrice, stake, balance, outcomeUp, allowBorrow);
            }
            catch (InvalidOperationException)
            {
                // Strict mode: unaffordable (should be caught above, but belt-and-suspenders).
                break;
            }

            bets.Add(new BacktestBet
            {
                Id = Guid.NewGuid(),
                BacktestId = Guid.Empty,         // filled by the controller before persistence
                TargetOpenTime = target.OpenTime,
                Side = step.Side,
                PUpRaw = pUpRaw,
                PUpCalibrated = null,
                Size = stake,
                BalanceBefore = balance,
                BalanceAfter = step.BalanceAfter,
                Won = step.Won,
                BorrowedShortfall = step.BorrowedShortfall,
                EntryPrice = step.Side == "UP" ? entry.YesPrice : entry.NoPrice,
                Shares = step.Shares,
                Payout = step.Payout,
                Synthetic = entry.Synthetic,
                MarketExternalId = entry.MarketExternalId,
            });

            if (entry.Synthetic) syntheticCount++;

            // Aggregate metrics.
            placed++;
            if (step.Won) won++;
            // Brier score is graded on the directional probability vs the realised outcome, not on
            // the staked side — measures calibration + sharpness independent of the staking strategy.
            var actualUp = outcomeUp ? 1.0 : 0.0;
            var diff = (double)pUpRaw - actualUp;
            brierSum += diff * diff;
            // High-conviction reporting subset (confidence = |pUp - 0.5| * 2).
            if (minConf > 0m && Math.Abs(pUpRaw - 0.5m) * 2m >= minConf)
            {
                gatedPlaced++;
                if (step.Won) gatedWon++;
            }
            // Martingale step measured off the stake we just placed (before next sizing).
            var currentStep = stake <= 0 ? 0 : (int)Math.Round(Math.Log2((double)(stake / Math.Max(0.0001m, initialBetSize))));
            if (currentStep > maxMartingaleStep) maxMartingaleStep = currentStep;
            balance = step.BalanceAfter;
            lastStake = stake;
            lastWon = step.Won;
            if (step.CrossedZero) zeroCrossings++;
            if (balance > peakBalance) peakBalance = balance;
            if (balance < troughBalance) troughBalance = balance;
            var drawdown = peakBalance - balance;
            if (drawdown > maxDrawdown) maxDrawdown = drawdown;
            // Peak borrowed tracks the worst credit moment across two channels: the literal
            // negative-balance dip AND the per-step shortfall (bet > balance at placement time).
            // The second case was previously invisible — a winning oversized bet would never
            // push balance negative, but the strategy was still bankrupt at that moment.
            if (balance < 0 && Math.Abs(balance) > peakBorrowed) peakBorrowed = Math.Abs(balance);
            if (step.BorrowedShortfall > peakBorrowed) peakBorrowed = step.BorrowedShortfall;

            var processed = i - flow.WarmupCandles + 1;
            if (progress is not null && processed % 200 == 0)
                progress.Report(new BacktestProgress(processed, totalCandles, placed));
        }

        // Final tick so the bar lands on 100% even if the last batch wasn't a multiple of 200.
        progress?.Report(new BacktestProgress(totalCandles, totalCandles, placed));

        // placed=0 means every candle abstained (the Flat Baseline case). Returning 0 there would
        // paint the control as a 0% hit-rate disaster on the leaderboard; null is honest — the
        // frontend renders "—" and pnlClass treats it as neutral.
        decimal? hitRate = placed == 0 ? null : (decimal)won / placed;
        decimal? brierScore = placed == 0 ? null : (decimal)(brierSum / placed);
        decimal? gatedHitRate = gatedPlaced == 0 ? null : (decimal)gatedWon / gatedPlaced;
        decimal? syntheticBetFraction = placed == 0 ? null : (decimal)syntheticCount / placed;
        var markersJson = JsonSerializer.Serialize(bets.Select(b => new { t = b.TargetOpenTime, hit = b.Won, side = b.Side }),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        if (placed > 0)
            _logger.LogInformation(
                "Backtest {Symbol}/{Interval}: headline (ungated) hit-rate {Hit:P2} on {Placed} bets, Brier {Brier:F4}{Gated}",
                symbol, interval, hitRate, placed, brierScore,
                minConf > 0m ? $", gated≥{minConf:F2} {gatedHitRate:P2} on {gatedPlaced} bets" : "");

        return new BacktestOutcome(
            Bets: bets,
            BetsPlaced: placed,
            BetsWon: won,
            HitRate: hitRate,
            FinalBalance: balance,
            PeakBalance: peakBalance,
            TroughBalance: troughBalance,
            MaxDrawdown: maxDrawdown,
            PeakBorrowed: peakBorrowed,
            ZeroCrossingsCount: zeroCrossings,
            MaxMartingaleStep: maxMartingaleStep,
            MarkersJson: markersJson,
            BrierScore: brierScore,
            GatedBetsPlaced: gatedPlaced,
            GatedBetsWon: gatedWon,
            GatedHitRate: gatedHitRate,
            TotalCandles: totalCandles,
            SyntheticBetFraction: syntheticBetFraction);
    }

    /// <summary>
    /// Pure, staking-free replay of a deterministic model over a historical window. For every
    /// closed candle in [startMs, endMs] this emits the direction the model WOULD have produced
    /// live at that candle's decision edge — using the same anti-lookahead slice machinery as
    /// <see cref="RunAsync"/> (features see only candles through the anchor; off-tf + microstructure
    /// are boundary-clamped). This is what backfills the chart's hit/miss dots for the stretch
    /// before the live gap-filler started recording: the model is deterministic and reproducible,
    /// so a backfilled point is exactly the prediction the live path would have persisted.
    ///
    /// Mirrors the live <c>PredictViaFlowAsync</c> contract: horizonSteps=2 ⇒ target = anchor + 2
    /// intervals, decision at close(anchor), direction graded close(target) vs close(anchor) — the
    /// 2-step canon. Candles that error out or that the flow can't ready are skipped (no point).
    /// </summary>
    public async Task<IReadOnlyList<ReplayPoint>> ReplayDirectionsAsync(
        FlowDefinition flow,
        string trainedStateJson,
        Guid tenantId,
        Guid modelId,
        string symbol,
        string interval,
        long startMs,
        long endMs,
        CancellationToken ct,
        int horizonSteps = 2)
    {
        if (!flow.SupportsBacktesting) return Array.Empty<ReplayPoint>();

        var intervalMs = IntervalMs(interval);
        var warmupMs = (long)flow.WarmupCandles * intervalMs;
        var candles = await _candles.GetRangeAsync(symbol, interval, startMs - warmupMs, endMs, ct);
        if (candles.Count <= flow.WarmupCandles + horizonSteps) return Array.Empty<ReplayPoint>();

        var offTfCandles = new Dictionary<string, IReadOnlyList<HistoricalCandle>>();
        foreach (var otherTf in FrostAura.Foresight.Domain.MarketData.SupportedSymbols.Intervals)
        {
            if (otherTf == interval) continue;
            var offWarmupMs = Math.Max(warmupMs, 60L * IntervalMs(otherTf));
            try { offTfCandles[otherTf] = await _candles.GetRangeAsync(symbol, otherTf, startMs - offWarmupMs, endMs, ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "Replay off-tf pre-fetch failed for {Tf}", otherTf); offTfCandles[otherTf] = Array.Empty<HistoricalCandle>(); }
        }

        IReadOnlyList<MicrostructureBar> microPool = Array.Empty<MicrostructureBar>();
        if (_micro is not null)
        {
            try { microPool = await _micro.GetRangeAsync(symbol, interval, startMs - warmupMs, endMs, ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "Replay microstructure pre-fetch failed; order-flow nodes will see empty data"); }
        }

        JsonElement? trainedFor = null;
        if (!string.IsNullOrWhiteSpace(trainedStateJson))
        {
            using var doc = JsonDocument.Parse(trainedStateJson);
            trainedFor = RemapTrainedState(doc.RootElement, interval);
        }

        var points = new List<ReplayPoint>();
        for (var i = flow.WarmupCandles; i < candles.Count - horizonSteps; i++)
        {
            if (ct.IsCancellationRequested) break;
            var anchor = candles[i];
            var target = candles[i + horizonSteps];
            var boundaryMs = anchor.OpenTime + intervalMs;
            var slice = new BacktestSliceProvider(candles, anchor.OpenTime, interval, offTfCandles, boundaryMs);
            var microSlice = _micro is null ? null : new MicrostructureSlice(microPool, intervalMs, boundaryMs);
            var ctx = new FlowContext(tenantId, modelId, symbol, interval, target.OpenTime, 1,
                FlowMode.Backtest, slice, trainedFor, microSlice);

            try
            {
                var result = await _executor.ExecuteAsync(flow, ctx, ct);
                if (result.OutputPrediction.GetValueOrDefault("pUp") is not decimal pUp) continue;
                var confidence = (result.OutputPrediction.GetValueOrDefault("confidence") as decimal?) ?? 0.5m;
                var predicted = result.OutputPrediction.GetValueOrDefault("predicted") as decimal?
                    ?? result.OutputPrediction.GetValueOrDefault("p50") as decimal?;
                points.Add(new ReplayPoint(target.OpenTime, anchor.Close, pUp, confidence, predicted, target.Close));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Replay skipping candle {OpenTime} due to flow execution error", anchor.OpenTime);
            }
        }
        return points;
    }

    /// <summary>
    /// Selects the per-interval variant from a multi-interval TrainedState JSON and remaps its
    /// regression blobs onto the keys the node executors expect.
    ///
    /// New format (current): <c>{ "variants": { "1m": {modelLR, modelLogReg, …}, "5m": {…}, "15m": {…} } }</c>
    /// — picks <c>variants[interval]</c> as the source. If a model has been retrained under the
    /// new pipeline but the requested interval has no variant (shouldn't happen given the trainer
    /// loops over SupportedSymbols.Intervals), we fall back to whichever variant exists so the
    /// run produces something rather than throwing.
    ///
    /// Legacy format: flat <c>{ modelLinearRegression, modelLogisticRegression }</c> at the root.
    /// Honored unchanged so models trained before the multi-interval migration still serve, even
    /// if the served coefficients are no longer interval-correct (the user should retrain).
    ///
    /// Exposed as <c>internal</c> so the live-prediction service can share the same lookup —
    /// otherwise live mode would only see the raw multi-interval shape and the regression nodes
    /// (which look up <c>model.linear_regression</c>) would silently fall back to defaults.
    /// </summary>
    public static JsonElement? RemapTrainedState(JsonElement source, string interval)
    {
        if (source.ValueKind != JsonValueKind.Object) return null;

        var variantRoot = source;
        if (source.TryGetProperty("variants", out var variants) && variants.ValueKind == JsonValueKind.Object)
        {
            if (variants.TryGetProperty(interval, out var picked))
            {
                variantRoot = picked;
            }
            else
            {
                // Requested interval missing — first available variant is better than nothing.
                using var iter = variants.EnumerateObject().GetEnumerator();
                if (iter.MoveNext()) variantRoot = iter.Current.Value;
            }
        }

        var lrJson   = variantRoot.TryGetProperty("modelLinearRegression",   out var lr) ? lr.GetRawText() : "{}";
        var logrJson = variantRoot.TryGetProperty("modelLogisticRegression", out var lo) ? lo.GetRawText() : "{}";
        // GBT ensemble (present only for model.gbt flows); pass through under the node's TypeId so
        // GradientBoostedTreesNode can read it. Null/absent for linear models — the node treats a
        // non-object as "no model" and abstains, exactly like the regression nodes.
        var gbtJson  = variantRoot.TryGetProperty("modelGbt", out var gb) && gb.ValueKind == JsonValueKind.Object ? gb.GetRawText() : "null";
        var combined = "{\"model.linear_regression\":" + lrJson + ",\"model.logistic_regression\":" + logrJson + ",\"model.gbt\":" + gbtJson + "}";
        using var doc = JsonDocument.Parse(combined);
        return doc.RootElement.Clone();
    }

    private static long IntervalMs(string interval) => PublicIntervalMs(interval);

    /// <summary>
    /// Converts an interval string to its duration in milliseconds. Shared across the application
    /// layer and infrastructure: the slice provider, the chaos precompute path, and anywhere else
    /// that needs a deterministic interval→ms mapping without pulling in the Binance client.
    /// </summary>
    public static long PublicIntervalMs(string interval) => interval switch
    {
        "1m"  => 60_000L,
        "5m"  => 300_000L,
        "15m" => 900_000L,
        _ => throw new ArgumentException($"Unsupported interval '{interval}'.", nameof(interval)),
    };

    /// <summary>
    /// Binary-search the OpenTime-ascending <paramref name="sorted"/> list for candles with
    /// OpenTime in [<paramref name="loInclusive"/>, <paramref name="hiInclusive"/>] and matching
    /// <paramref name="symbol"/>. O(log n + window) — this replaces the per-iteration
    /// <c>candles.Take(i+1).ToList()</c> + full-list <c>Where</c> scan that made backtest/training
    /// O(n²) over large candle counts (catastrophic at 30-day 1m ≈ 43k candles). The pre-fetched
    /// lists are already OpenTime-sorted (the adapter returns them ordered), so the search is valid.
    /// Public so the chaos precompute path can share the same binary-range logic without duplicating it.
    /// </summary>
    public static List<HistoricalCandle> SortedRange(
        IReadOnlyList<HistoricalCandle> sorted, string symbol, long loInclusive, long hiInclusive)
    {
        var res = new List<HistoricalCandle>();
        if (hiInclusive < loInclusive || sorted.Count == 0) return res;
        int lo = 0, hi = sorted.Count;
        while (lo < hi) { var mid = (lo + hi) >> 1; if (sorted[mid].OpenTime < loInclusive) lo = mid + 1; else hi = mid; }
        for (var k = lo; k < sorted.Count && sorted[k].OpenTime <= hiInclusive; k++)
            if (sorted[k].Symbol == symbol) res.Add(sorted[k]);
        return res;
    }
}

public sealed record BacktestOutcome(
    IReadOnlyList<BacktestBet> Bets,
    int BetsPlaced,
    int BetsWon,
    decimal? HitRate,
    decimal FinalBalance,
    decimal PeakBalance,
    decimal TroughBalance,
    decimal MaxDrawdown,
    decimal PeakBorrowed,
    int ZeroCrossingsCount,
    int MaxMartingaleStep,
    string MarkersJson,
    // Headline HitRate is ungated (bet-every-candle, live-equivalent). These extras are reporting
    // signals consumed by the walk-forward harness + iteration loop; default-valued so older
    // construction sites stay valid.
    decimal? BrierScore = null,
    int GatedBetsPlaced = 0,
    int GatedBetsWon = 0,
    decimal? GatedHitRate = null,
    int TotalCandles = 0,
    /// <summary>Fraction of bets using synthetic odds (null when no bets placed).</summary>
    decimal? SyntheticBetFraction = null);

public sealed record BacktestProgress(int CandlesProcessed, int TotalCandles, int BetsPlaced);

/// <summary>
/// One historical-replay point: the model's leakage-free direction call for a target candle, plus
/// the anchor close it was graded against and the candle's actual close. Consumed by the live-
/// prediction backfill to materialise resolved <c>LivePrediction</c> rows for the chart overlay.
/// </summary>
public sealed record ReplayPoint(
    long TargetOpenTime,
    decimal AnchorClose,
    decimal PUp,
    decimal Confidence,
    decimal? Predicted,
    decimal ActualClose);

internal sealed class BacktestSliceProvider : IHistoricalCandleProvider
{
    private readonly IReadOnlyList<HistoricalCandle> _targetFull;
    private readonly long _targetCapOpenInclusive;
    private readonly string _targetInterval;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<HistoricalCandle>> _offTf;
    private readonly long _currentBoundaryMs;

    /// <summary>
    /// Anti-lookahead provider for a single backtest iteration. Holds the FULL pre-fetched
    /// target-tf list (OpenTime-sorted) plus a cap (<paramref name="targetCapOpenInclusive"/> =
    /// the anchor's open time) so target candles after the anchor are never returned — no per-
    /// iteration copy. Off-tf candles come from the pre-fetched dict, capped by CLOSE-time at
    /// <paramref name="currentBoundaryMs"/> so a still-open higher-tf bar can't leak. Both paths
    /// use a binary-search window (<see cref="BacktestRunner.SortedRange"/>), so each lookup is
    /// O(log n + window) instead of O(n).
    /// </summary>
    public BacktestSliceProvider(
        IReadOnlyList<HistoricalCandle> targetFull,
        long targetCapOpenInclusive,
        string targetInterval,
        IReadOnlyDictionary<string, IReadOnlyList<HistoricalCandle>> offTf,
        long currentBoundaryMs)
    {
        _targetFull = targetFull;
        _targetCapOpenInclusive = targetCapOpenInclusive;
        _targetInterval = targetInterval;
        _offTf = offTf;
        _currentBoundaryMs = currentBoundaryMs;
    }

    public Task<IReadOnlyList<HistoricalCandle>> GetRangeAsync(string symbol, string interval, long startMs, long endMs, CancellationToken ct = default)
    {
        if (interval == _targetInterval)
        {
            // Cap the upper bound at the anchor's open so only candles 0..i are ever visible.
            var hi = Math.Min(endMs, _targetCapOpenInclusive);
            return Task.FromResult<IReadOnlyList<HistoricalCandle>>(BacktestRunner.SortedRange(_targetFull, symbol, startMs, hi));
        }
        if (!_offTf.TryGetValue(interval, out var pool))
            return Task.FromResult<IReadOnlyList<HistoricalCandle>>(Array.Empty<HistoricalCandle>());
        // CRITICAL: off-tf is capped by CLOSE-time. A bar whose open ≤ boundary but whose close is
        // in the future would leak — so the upper bound on OpenTime is (clampedEnd - offInterval),
        // i.e. only bars fully closed by the bet-placement moment.
        var clampedEnd = Math.Min(endMs, _currentBoundaryMs);
        var offIntervalMs = BacktestRunner.PublicIntervalMs(interval);
        return Task.FromResult<IReadOnlyList<HistoricalCandle>>(BacktestRunner.SortedRange(pool, symbol, startMs, clampedEnd - offIntervalMs));
    }
}
