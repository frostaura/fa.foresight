using System.Text.Json;
using FrostAura.Foresight.Application.Backtesting;
using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Application.Models;
using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Domain.Live;
using FrostAura.Foresight.Domain.Models;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Infrastructure.Adapters;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Infrastructure.Live;

/// <summary>
/// Generates and persists a forecast of the next candle's close on a Binance spot pair, using the
/// deterministic flow executor. Also resolves matured predictions on the way through so the chart
/// overlay can show hit/miss without a separate background job.
/// </summary>
public interface ILivePredictionService
{
    /// <summary>
    /// horizon = 0 → predict the close of the still-forming (active) candle.
    /// horizon = 1 → predict the close of the next full candle (the one that hasn't opened yet).
    /// </summary>
    Task<LivePrediction> PredictAsync(string symbol, string interval, int horizon, CancellationToken ct);
    Task<IReadOnlyList<LivePrediction>> ListAsync(string symbol, string interval, int take, CancellationToken ct);
    Task<int> ResolveMaturedAsync(string symbol, string interval, CancellationToken ct);

    /// <summary>
    /// Materialises resolved predictions for the most recent <paramref name="candleCount"/> closed
    /// candles by replaying the active model over history (leakage-free, deterministic). Skips any
    /// candle that already has a prediction for the active model, so it's idempotent and cheap on
    /// re-runs. Returns the number of rows actually backfilled. No-op (returns 0) for live-only
    /// models that don't support backtesting — their historical calls can't be honestly reconstructed.
    /// </summary>
    Task<int> BackfillHistoryAsync(string symbol, string interval, int candleCount, CancellationToken ct);
}

public sealed class LivePredictionService : ILivePredictionService
{
    private readonly BinanceMarketDataClient _binance;
    private readonly ForesightDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ILivePredictionEventHub _events;
    private readonly IActiveModelResolver _activeModels;
    private readonly IFlowExecutor _executor;
    private readonly IHistoricalCandleProvider _historicalCandles;
    private readonly CachedMicrostructureReader _microstructure;
    private readonly BacktestRunner _backtestRunner;
    private readonly ILogger<LivePredictionService> _logger;

    public LivePredictionService(
        BinanceMarketDataClient binance,
        ForesightDbContext db,
        ITenantContext tenant,
        ILivePredictionEventHub events,
        IActiveModelResolver activeModels,
        IFlowExecutor executor,
        IHistoricalCandleProvider historicalCandles,
        CachedMicrostructureReader microstructure,
        BacktestRunner backtestRunner,
        ILogger<LivePredictionService> logger)
    {
        _binance = binance;
        _db = db;
        _tenant = tenant;
        _events = events;
        _activeModels = activeModels;
        _executor = executor;
        _historicalCandles = historicalCandles;
        _microstructure = microstructure;
        _backtestRunner = backtestRunner;
        _logger = logger;
    }

    public async Task<LivePrediction> PredictAsync(string symbol, string interval, int horizon, CancellationToken ct)
    {
        if (!_tenant.IsResolved) throw new InvalidOperationException("Tenant not resolved.");
        if (horizon < 0 || horizon > 4) throw new ArgumentOutOfRangeException(nameof(horizon));

        // Resolve anything that's already closed before generating new — keeps the overlay honest.
        try { await ResolveMaturedAsync(symbol, interval, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "ResolveMatured failed mid-predict"); }

        // All predictions run through the deterministic flow executor.
        var activeModelId = await _activeModels.ResolveAsync(_tenant.TenantId!.Value, symbol, interval, ct);
        return await PredictViaFlowAsync(activeModelId, symbol, interval, horizon, ct);
    }

    /// <summary>
    /// Runs the active model's flow definition through the <see cref="IFlowExecutor"/>, then
    /// persists a <see cref="LivePrediction"/> with <c>ModelId</c> stamped to the active model so
    /// the foreign-key integrity holds and the per-card paper-trading layer attributes outcomes to
    /// the right model.
    /// </summary>
    private async Task<LivePrediction> PredictViaFlowAsync(Guid modelId, string symbol, string interval, int horizon, CancellationToken ct)
    {
        var model = await _db.Models.AsNoTracking().FirstOrDefaultAsync(m => m.Id == modelId, ct)
            ?? throw new InvalidOperationException($"Active model {modelId} not found.");

        // Anchor on the last CLOSED candle — NEVER the currently-forming bar. Binance returns the
        // in-progress candle as the last element, and its close is still moving. The model is
        // trained to predict direction relative to the last *closed* candle's close
        // (ModelTrainer: yDir = close[i+1] > close[i]), and the feature packs read the
        // close-capped historical adapter, so the anchor used for change%/band/direction-grading
        // must be that same last-closed close. Using the forming bar's moving price mis-grades
        // correct calls whenever price has already drifted intra-bar in the predicted direction.
        var primary = await _binance.GetKlinesAsync(symbol, interval, 60, ct);
        if (primary.Count < 20) throw new InvalidOperationException("Not enough candles to forecast.");
        var intervalMs = BinanceMarketDataClient.IntervalMs(interval);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var anchor = primary.LastOrDefault(c => c.CloseTime <= nowMs) ?? primary[^2];
        var anchorClose = anchor.Close;
        // Target candle opens immediately after the anchor, shifted by horizon (0 = the candle now
        // forming). Identical value to the old `forming.OpenTime + horizon*interval`, so idempotency
        // keys and resolution matching are unchanged — only the anchor reference is corrected.
        var targetOpenTime = anchor.OpenTime + (horizon + 1) * intervalMs;

        // Idempotency per (model, target) so a card that re-toggles the same active model doesn't
        // pay twice for the same candle.
        var existing = await _db.LivePredictions.AsNoTracking()
            .FirstOrDefaultAsync(p =>
                p.TenantId == _tenant.TenantId!.Value &&
                p.Symbol == symbol && p.Interval == interval &&
                p.TargetOpenTime == targetOpenTime &&
                p.ModelId == modelId, ct);
        if (existing != null) return existing;

        FlowDefinition? flow;
        try { flow = JsonSerializer.Deserialize<FlowDefinition>(model.Definition, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException ex) { throw new InvalidOperationException($"Model definition invalid JSON: {ex.Message}"); }
        if (flow is null) throw new InvalidOperationException("Model definition is empty.");

        // Multi-interval models persist coefficients under `variants[interval]`. The same remap
        // BacktestRunner uses for runtime — select the variant matching THIS live prediction's
        // interval, then rewrite the keys onto what the regression nodes expect. Without this
        // the regression nodes look for `model.linear_regression` at the root and find nothing,
        // silently falling back to default 0.5 predictions.
        JsonElement? trainedState = null;
        if (!string.IsNullOrWhiteSpace(model.TrainedState))
        {
            using var trainedDoc = JsonDocument.Parse(model.TrainedState);
            trainedState = BacktestRunner.RemapTrainedState(trainedDoc.RootElement, interval);
        }

        var ctx = new FlowContext(
            _tenant.TenantId!.Value, modelId, symbol, interval,
            targetOpenTime, horizon, FlowMode.Live, _historicalCandles, trainedState,
            Microstructure: _microstructure);

        var result = await _executor.ExecuteAsync(flow, ctx, ct);

        // Pull the output node's port values. Each port is optional — the node defaults missing
        // ones to 0.5 / null / "" so the column NOT NULL constraints are always satisfied.
        decimal pUp = (result.OutputPrediction.GetValueOrDefault("pUp") as decimal?) ?? 0.5m;
        decimal confidence = (result.OutputPrediction.GetValueOrDefault("confidence") as decimal?) ?? 0.5m;
        decimal? predicted = result.OutputPrediction.GetValueOrDefault("predicted") as decimal?;
        decimal p05 = (result.OutputPrediction.GetValueOrDefault("p05") as decimal?) ?? 0m;
        decimal p50 = (result.OutputPrediction.GetValueOrDefault("p50") as decimal?) ?? predicted ?? 0m;
        decimal p95 = (result.OutputPrediction.GetValueOrDefault("p95") as decimal?) ?? 0m;
        var reasoning = result.OutputPrediction.GetValueOrDefault("reasoning") as string;

        // Synthesise a tight band around p50 if the flow didn't produce quantiles (statistical
        // models often only emit a single point or pUp).
        if (p50 <= 0m) p50 = predicted ?? anchorClose * (1m + (pUp >= 0.5m ? 0.001m : -0.001m));
        if (p05 <= 0m) p05 = p50 * 0.999m;
        if (p95 <= 0m) p95 = p50 * 1.001m;
        var predictedClose = p50;
        var predictedChangePct = anchorClose == 0 ? 0m : (predictedClose - anchorClose) / anchorClose * 100m;

        var prediction = new LivePrediction
        {
            Id = Guid.NewGuid(),
            TenantId = _tenant.TenantId!.Value,
            ModelId = modelId,
            Symbol = symbol,
            Interval = interval,
            TargetOpenTime = targetOpenTime,
            AnchorClose = anchorClose,
            PredictedClose = predictedClose,
            PredictedChangePct = predictedChangePct,
            DirectionUpProbability = Math.Clamp(pUp, 0m, 1m),
            ClosePercentile05 = p05,
            ClosePercentile50 = p50,
            ClosePercentile95 = p95,
            Confidence = Math.Clamp(confidence, 0m, 1m),
            Reasoning = Truncate(reasoning ?? "", 7800),
            Model = model.Name,
            // Persist a JSON-safe slice of the per-node trace — we can't include FeatureMatrix
            // (it's a 2D double array System.Text.Json can't serialize). Stringify the values so
            // the audit log captures shape + scalar ports without crashing.
            PromptTraceJson = JsonSerializer.Serialize(new
            {
                modelKind = model.Kind,
                modelName = model.Name,
                outputPrediction = result.OutputPrediction.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value switch
                    {
                        null => null,
                        decimal d => (object)d,
                        string s => s,
                        _ => kv.Value.ToString(),
                    }),
            }, new JsonSerializerOptions { WriteIndented = false }),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.LivePredictions.Add(prediction);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true)
        {
            _db.Entry(prediction).State = EntityState.Detached;
            var theirs = await _db.LivePredictions.AsNoTracking().FirstOrDefaultAsync(p =>
                p.TenantId == _tenant.TenantId!.Value &&
                p.Symbol == symbol && p.Interval == interval &&
                p.TargetOpenTime == targetOpenTime &&
                p.ModelId == modelId, ct);
            return theirs ?? prediction;
        }
        _events.Publish(new LivePredictionEvent(LivePredictionEventKind.Created, prediction));
        return prediction;
    }

    public async Task<IReadOnlyList<LivePrediction>> ListAsync(string symbol, string interval, int take, CancellationToken ct)
    {
        if (!_tenant.IsResolved) return Array.Empty<LivePrediction>();
        var rows = await _db.LivePredictions.AsNoTracking()
            .Where(p => p.TenantId == _tenant.TenantId!.Value && p.Symbol == symbol && p.Interval == interval)
            .OrderByDescending(p => p.TargetOpenTime)
            .Take(take)
            .ToListAsync(ct);
        return rows;
    }

    public async Task<int> BackfillHistoryAsync(string symbol, string interval, int candleCount, CancellationToken ct)
    {
        if (!_tenant.IsResolved) return 0;
        candleCount = Math.Clamp(candleCount, 1, 1000);

        var tenantId = _tenant.TenantId!.Value;
        var activeModelId = await _activeModels.ResolveAsync(tenantId, symbol, interval, ct);
        var model = await _db.Models.AsNoTracking().FirstOrDefaultAsync(m => m.Id == activeModelId, ct);
        if (model is null) return 0;

        FlowDefinition? flow;
        try { flow = JsonSerializer.Deserialize<FlowDefinition>(model.Definition, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return 0; }
        // Live-only models (live-microstructure) can't be reconstructed historically without
        // fabricating their inputs — leave their dots to the forward-only gap-filler.
        if (flow is null || !flow.SupportsBacktesting) return 0;

        var intervalMs = BinanceMarketDataClient.IntervalMs(interval);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Backfill targets across the visible window. The replay's 2-step canon excludes the still-
        // forming candle on its own, so endMs = now is safe — the most recent target is the last
        // fully-closed candle, which the live path may also be writing; the duplicate guard below
        // handles that race.
        var startMs = nowMs - (long)candleCount * intervalMs;

        var existing = await _db.LivePredictions.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.Symbol == symbol && p.Interval == interval
                     && p.ModelId == activeModelId
                     && p.TargetOpenTime >= startMs && p.TargetOpenTime <= nowMs)
            .Select(p => p.TargetOpenTime)
            .ToListAsync(ct);
        var have = new HashSet<long>(existing);

        var points = await _backtestRunner.ReplayDirectionsAsync(
            flow, model.TrainedState ?? "", tenantId, activeModelId, symbol, interval, startMs, nowMs, ct);

        LivePrediction ToRow(ReplayPoint pt)
        {
            var anchorClose = pt.AnchorClose;
            var predictedClose = pt.Predicted is > 0m ? pt.Predicted!.Value
                : anchorClose * (1m + (pt.PUp >= 0.5m ? 0.001m : -0.001m));
            var pUp = Math.Clamp(pt.PUp, 0m, 1m);
            // Mirror ResolveMaturedAsync: direction graded close(target) vs the stored anchor close
            // (close(a) under the 2-step canon). Exactly-0.5 is honest abstention → DirectionHit null.
            bool? directionHit = pUp == 0.5m ? null : (pt.ActualClose >= anchorClose) == (pUp > 0.5m);
            return new LivePrediction
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ModelId = activeModelId,
                Symbol = symbol,
                Interval = interval,
                TargetOpenTime = pt.TargetOpenTime,
                AnchorClose = anchorClose,
                PredictedClose = predictedClose,
                PredictedChangePct = anchorClose == 0 ? 0m : (predictedClose - anchorClose) / anchorClose * 100m,
                DirectionUpProbability = pUp,
                ClosePercentile05 = predictedClose * 0.999m,
                ClosePercentile50 = predictedClose,
                ClosePercentile95 = predictedClose * 1.001m,
                Confidence = Math.Clamp(pt.Confidence, 0m, 1m),
                Reasoning = "",
                Model = model.Name,
                // Marked so calibration reporting can tell reconstructed-from-history rows apart from
                // genuinely-live ones — backfilled rows are leakage-free replays, not real-time calls.
                PromptTraceJson = JsonSerializer.Serialize(
                    new { backfilled = true, modelKind = model.Kind, modelName = model.Name },
                    new JsonSerializerOptions { WriteIndented = false }),
                CreatedAt = DateTimeOffset.UtcNow,
                ResolvedAt = DateTimeOffset.UtcNow,
                ActualClose = pt.ActualClose,
                AbsoluteErrorPct = anchorClose == 0 ? 0m : Math.Abs((pt.ActualClose - predictedClose) / anchorClose * 100m),
                DirectionHit = directionHit,
            };
        }

        var toAdd = points.Where(pt => !have.Contains(pt.TargetOpenTime)).Select(ToRow).ToList();
        if (toAdd.Count == 0) return 0;

        _db.LivePredictions.AddRange(toAdd);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Race with the live path / gap-filler inserting the most-recent target. Detach the
            // whole batch, re-read what's now present, and retry only the still-missing rows.
            foreach (var row in toAdd) _db.Entry(row).State = EntityState.Detached;
            var nowHave = new HashSet<long>(await _db.LivePredictions.AsNoTracking()
                .Where(p => p.TenantId == tenantId && p.Symbol == symbol && p.Interval == interval
                         && p.ModelId == activeModelId
                         && p.TargetOpenTime >= startMs && p.TargetOpenTime <= nowMs)
                .Select(p => p.TargetOpenTime)
                .ToListAsync(ct));
            toAdd = toAdd.Where(r => !nowHave.Contains(r.TargetOpenTime)).ToList();
            if (toAdd.Count == 0) return 0;
            _db.LivePredictions.AddRange(toAdd);
            await _db.SaveChangesAsync(ct);
        }
        return toAdd.Count;
    }

    public async Task<int> ResolveMaturedAsync(string symbol, string interval, CancellationToken ct)
    {
        if (!_tenant.IsResolved) return 0;
        var intervalMs = BinanceMarketDataClient.IntervalMs(interval);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // "Matured" = target candle's close-time is in the past.
        var pending = await _db.LivePredictions
            .Where(p => p.TenantId == _tenant.TenantId!.Value
                     && p.Symbol == symbol && p.Interval == interval
                     && p.ResolvedAt == null
                     && p.TargetOpenTime + intervalMs <= nowMs)
            .OrderBy(p => p.TargetOpenTime)
            .Take(50)
            .ToListAsync(ct);
        if (pending.Count == 0) return 0;

        // Pull enough candles to cover the oldest pending. Binance returns chronological order.
        var oldest = pending[0].TargetOpenTime;
        var spanIntervals = (int)((nowMs - oldest) / intervalMs) + 5;
        var limit = Math.Min(Math.Max(spanIntervals, 50), 1000);
        var candles = await _binance.GetKlinesAsync(symbol, interval, limit, ct);
        var byOpenTime = candles.ToDictionary(c => c.OpenTime);

        var resolved = new List<LivePrediction>();
        foreach (var p in pending)
        {
            if (!byOpenTime.TryGetValue(p.TargetOpenTime, out var c)) continue;
            var actual = c.Close;
            p.ActualClose = actual;
            p.ResolvedAt = DateTimeOffset.UtcNow;
            p.AbsoluteErrorPct = p.AnchorClose == 0 ? 0 : Math.Abs((actual - p.PredictedClose) / p.AnchorClose * 100m);
            // Direction-only resolution. Exact 0.50 is an explicit non-bet — leave DirectionHit null
            // so accuracy stats don't penalise the model for honest uncertainty. Tradable signals
            // are those strictly above or strictly below 0.50.
            //
            // 2-step canon: direction is the target's close vs the PREVIOUS closed candle's close —
            // exactly the AnchorClose we stored at prediction time (the last closed candle, index a).
            // The non-closed candle (a+1) is excluded entirely — its price was still moving at
            // decision time, so it can be neither a feature nor the reference. Matches the training
            // label yDir = close(a+2) > close(a).
            var actualUp = actual >= p.AnchorClose;
            if (p.DirectionUpProbability == 0.5m)
            {
                p.DirectionHit = null;
            }
            else
            {
                var predictedUp = p.DirectionUpProbability > 0.5m;
                p.DirectionHit = actualUp == predictedUp;
            }
            resolved.Add(p);
        }
        await _db.SaveChangesAsync(ct);
        foreach (var p in resolved)
            _events.Publish(new LivePredictionEvent(LivePredictionEventKind.Resolved, p));
        return resolved.Count;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
