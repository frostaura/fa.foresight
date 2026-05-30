using System.Globalization;

namespace FrostAura.Foresight.Infrastructure.Live;

/// <summary>
/// Shared formatter for the resolved-bet "card": a bold header (🟢 WIN / 🔴 LOSS · SYM int · time)
/// over a monospace block (this bet's P&amp;L + stake→payout, the session P&amp;L, hit rate, and the new
/// balance with a direction arrow). Used by the text notifier AND as the caption under the per-bet
/// chart photo so both read identically.
/// </summary>
public static class BetCardFormatter
{
    public static (string Title, string Body) Format(
        string symbol, string interval, long targetOpenTimeMs,
        decimal stake, decimal payout, bool won,
        decimal balanceAfter, decimal initialBalance,
        int betsWon, int betsPlaced)
    {
        var net          = payout - stake;
        var overall      = balanceAfter - initialBalance;
        var overallPct   = initialBalance > 0m ? overall / initialBalance * 100m : 0m;
        var hitRate      = betsPlaced > 0 ? (decimal)betsWon / betsPlaced * 100m : 0m;
        var time         = DateTimeOffset.FromUnixTimeMilliseconds(targetOpenTimeMs).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        var overallArrow = overall >= 0m ? "▲" : "▼";

        var title = $"{(won ? "🟢 WIN" : "🔴 LOSS")} · {ShortSymbol(symbol)} {interval} · {time}";

        static string Row(string label, string value) => $"{label.PadRight(12)}  :  {value}";
        var lines = new[]
        {
            Row("This bet", $"{Signed(net),-8} ({Money(stake)} → {Money(payout)})"),
            Row("This session", $"{Signed(overall),-8} ({SignedPct(overallPct)})"),
            Row("Hit rate", $"{hitRate.ToString("0", CultureInfo.InvariantCulture)}%"),
            "────────────────────────────",
            Row("Balance",  $"{Money(balanceAfter)}  {overallArrow} {Signed(overall)} ({SignedPct(overallPct)})"),
        };
        var width = lines.Max(l => l.Length) + 3; // common width + 3-space right gutter (clears the copy icon)
        var body  = string.Join("\n", lines.Select(l => l.PadRight(width)));
        return (title, body);
    }

    /// <summary>The card as a Telegram-HTML string (bold title + monospace block) — e.g. a photo caption.</summary>
    public static string Html(
        string symbol, string interval, long targetOpenTimeMs,
        decimal stake, decimal payout, bool won,
        decimal balanceAfter, decimal initialBalance,
        int betsWon, int betsPlaced)
    {
        var (title, body) = Format(symbol, interval, targetOpenTimeMs, stake, payout, won, balanceAfter, initialBalance, betsWon, betsPlaced);
        return $"<b>{Escape(title)}</b>\n<pre>{Escape(body)}</pre>";
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    private static string Money(decimal v) => "$" + v.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Signed(decimal v) => (v >= 0 ? "+$" : "−$") + Math.Abs(v).ToString("0.00", CultureInfo.InvariantCulture);
    private static string SignedPct(decimal v) => (v >= 0 ? "+" : "−") + Math.Abs(v).ToString("0.0", CultureInfo.InvariantCulture) + "%";
    private static string ShortSymbol(string s) =>
        s.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ? s[..^4]
        : s.EndsWith("USD", StringComparison.OrdinalIgnoreCase) ? s[..^3]
        : s;
}
