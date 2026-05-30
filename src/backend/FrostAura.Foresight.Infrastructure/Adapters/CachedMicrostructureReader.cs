using FrostAura.Foresight.Domain.MarketData;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Foresight.Infrastructure.Adapters;

/// <summary>
/// Cache-ONLY microstructure provider for the LIVE prediction path. Reads <c>historical_microstructure</c>
/// straight from Postgres and never triggers ingestion — so a live predict can never block on a
/// multi-second daily-dump download. The <see cref="MicrostructureFollowerService"/> is responsible
/// for keeping the cache current at the edge; if a bar isn't there yet, the order-flow features
/// simply come back null and the model abstains for that candle (fail-safe).
///
/// (Backtest/training use the full <see cref="BinanceHistoricalMicrostructureAdapter"/> instead,
/// which DOES ingest on demand — slow first run is fine offline.)
/// </summary>
public sealed class CachedMicrostructureReader : IHistoricalMicrostructureProvider
{
    // A fresh DbContext per call (own scope), NOT the request-scoped one. The flow executor runs
    // sibling source/feature nodes concurrently (Task.WhenAll per layer); if two of them shared one
    // DbContext we'd hit EF's "second operation started on this context" race. Per-call contexts are
    // independent, so concurrent reads are safe. Cheap — Npgsql pools the underlying connection.
    private readonly IServiceScopeFactory _scopes;
    public CachedMicrostructureReader(IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task<IReadOnlyList<MicrostructureBar>> GetRangeAsync(
        string symbol, string interval, long startMs, long endMs, CancellationToken ct = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();
        return await db.HistoricalMicrostructure.AsNoTracking()
            .Where(b => b.Symbol == symbol && b.Interval == interval && b.OpenTime >= startMs && b.OpenTime <= endMs)
            .OrderBy(b => b.OpenTime)
            .ToListAsync(ct);
    }
}
