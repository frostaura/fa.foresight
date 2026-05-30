using FrostAura.Foresight.Domain.Positions;

namespace FrostAura.Foresight.Domain.Sizing;

/// <summary>
/// Fractional-Kelly sizing for a binary prediction market priced in [0,1] per share. Pure math —
/// no IO — so it unit-tests trivially and is shared by the autonomous loop and manual /forcebuy.
///
/// For a YES share bought at price p that pays $1 on YES: profit on win = (1−p) per p staked, so the
/// net odds b = (1−p)/p with win-probability = the model's q. Kelly's f* = (b·q − (1−q))/b reduces to
/// (q − p)/(1 − p). The mirror holds for NO at price p_no with win-probability (1−q):
/// f* = ((1−q) − p_no)/(1 − p_no). We bet whichever side carries positive edge, scaled by a
/// fractional multiplier (default ¼) to blunt the variance and estimation error in q.
/// </summary>
public interface IPositionSizer
{
    PositionSizing Size(decimal modelProbYes, decimal yesPrice, decimal noPrice, decimal availableUsd, decimal kellyFraction);
}

public sealed record PositionSizing(
    bool ShouldTrade,
    PositionSide Side,
    decimal StakeUsd,
    decimal Shares,
    decimal Edge,
    decimal LimitPrice,
    string Reason);

public sealed class KellyPositionSizer : IPositionSizer
{
    public PositionSizing Size(decimal modelProbYes, decimal yesPrice, decimal noPrice, decimal availableUsd, decimal kellyFraction)
    {
        var q = Math.Clamp(modelProbYes, 0m, 1m);

        // Edge on each side relative to the price you'd pay. Prices can be stale/illiquid; guard the
        // degenerate p≈1 case (division by ~0) by refusing to size into it.
        var yesEdge = q - yesPrice;                 // buying YES profits when q > yesPrice
        var noEdge = (1m - q) - noPrice;            // buying NO profits when (1−q) > noPrice

        PositionSide side;
        decimal price, winProb, edge;
        if (yesEdge >= noEdge && yesEdge > 0m)
        {
            side = PositionSide.Yes; price = yesPrice; winProb = q; edge = yesEdge;
        }
        else if (noEdge > 0m)
        {
            side = PositionSide.No; price = noPrice; winProb = 1m - q; edge = noEdge;
        }
        else
        {
            return new PositionSizing(false, PositionSide.Yes, 0m, 0m, Math.Max(yesEdge, noEdge), 0m, "no positive edge on either side");
        }

        if (price <= 0m || price >= 1m)
            return new PositionSizing(false, side, 0m, 0m, edge, price, $"unusable price {price}");

        // f* = (winProb − price)/(1 − price); fractional Kelly, clamped to [0,1].
        var fullKelly = (winProb - price) / (1m - price);
        var frac = Math.Clamp(fullKelly * kellyFraction, 0m, 1m);
        var stake = Math.Round(frac * availableUsd, 2, MidpointRounding.AwayFromZero);
        if (stake <= 0m)
            return new PositionSizing(false, side, 0m, 0m, edge, price, "kelly stake rounded to zero");

        var shares = Math.Round(stake / price, 4, MidpointRounding.ToZero);
        if (shares <= 0m)
            return new PositionSizing(false, side, 0m, 0m, edge, price, "share size rounded to zero");

        return new PositionSizing(true, side, stake, shares, edge, price, "sized");
    }
}
