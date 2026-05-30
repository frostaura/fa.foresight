namespace FrostAura.Foresight.Domain.Ports;

/// <summary>
/// Config-driven capability matrix: venue → symbol → supported intervals.
/// Used by the session API and UI to present only valid (venue, symbol, interval) combinations.
/// </summary>
public interface IVenueCapabilities
{
    /// <summary>All registered venue identifiers.</summary>
    IReadOnlyList<string> Venues { get; }

    /// <summary>Symbols supported by the given venue.</summary>
    IReadOnlyList<string> SupportedSymbols(string venue);

    /// <summary>Intervals supported for the given (venue, symbol) pair.</summary>
    IReadOnlyList<string> SupportedIntervals(string venue, string symbol);

    /// <summary>Returns true when the (venue, symbol, interval) triple is configured.</summary>
    bool IsSupported(string venue, string symbol, string interval);
}

/// <summary>
/// Bundled Polymarket + BTC (5m, 15m) capability matrix. Additional venues/symbols
/// are registered via config extension; this static default covers the MVP target.
/// </summary>
public sealed class PolymarketBtcCapabilities : IVenueCapabilities
{
    private static readonly Dictionary<string, Dictionary<string, string[]>> _matrix = new(StringComparer.OrdinalIgnoreCase)
    {
        ["polymarket"] = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["BTCUSDT"] = ["5m", "15m"]
        }
    };

    public IReadOnlyList<string> Venues => [.. _matrix.Keys];

    public IReadOnlyList<string> SupportedSymbols(string venue)
        => _matrix.TryGetValue(venue, out var symbols) ? [.. symbols.Keys] : [];

    public IReadOnlyList<string> SupportedIntervals(string venue, string symbol)
        => _matrix.TryGetValue(venue, out var symbols) && symbols.TryGetValue(symbol, out var intervals)
            ? intervals
            : [];

    public bool IsSupported(string venue, string symbol, string interval)
        => _matrix.TryGetValue(venue, out var symbols)
        && symbols.TryGetValue(symbol, out var intervals)
        && intervals.Contains(interval, StringComparer.OrdinalIgnoreCase);
}
