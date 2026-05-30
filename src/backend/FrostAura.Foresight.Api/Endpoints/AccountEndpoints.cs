using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Domain.Ports;

namespace FrostAura.Foresight.Api.Endpoints;

/// <summary>
/// Account-level live-trading balance view for the current tenant.
///
/// GET /api/account/balance → { walletPusd, reserved, free }.
///   - walletPusd : on-chain pUSD in the configured wallet (0 when no wallet / unconfirmed).
///   - reserved   : Σ current_balance of active LIVE sessions (always meaningful, even pre-funding).
///   - free       : walletPusd − reserved.
/// Paper sessions are excluded — this is the real-money account view.
/// </summary>
public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/account").WithTags("account");

        g.MapGet("/balance", async (ITenantContext tc, IAccountLedger ledger, CancellationToken ct) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var tenantId = tc.TenantId!.Value;

            var walletPusd = await ledger.GetWalletPusdAsync(tenantId, ct);
            var free       = await ledger.GetFreeAsync(tenantId, ct);
            // reserved = walletPusd − free holds even when walletPusd is 0/unconfirmed (free goes negative).
            var reserved   = walletPusd - free;

            return Results.Ok(new AccountBalanceView(
                WalletPusd: walletPusd,
                Reserved: reserved,
                Free: free));
        });
    }

    private sealed record AccountBalanceView(decimal WalletPusd, decimal Reserved, decimal Free);
}
