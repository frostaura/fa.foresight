using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Infrastructure.Live;

namespace FrostAura.Foresight.Api.Endpoints;

/// <summary>
/// Human-gated arming flow for live execution.
///
/// Step 1: POST /api/golive/request-code  → returns a one-time 6-digit code (TTL 5 min).
/// Step 2: POST /api/golive/confirm       → echo the code back to arm live execution for this tenant.
/// Step 3: POST /api/golive/killswitch    → immediately disarm live execution.
///
/// The arm state is held in-memory (ILiveTradingArm singleton) AND mirrored to the live_arm_state
/// table, so an armed tenant RESUMES automated execution after a process restart (pending codes are
/// in-memory only and do not survive). Every real order additionally requires LiveTrading=true on the
/// tenant connection AND a wallet key configured — the arm is the final runtime layer. Without all
/// three, PolymarketExecutionProvider refuses the order.
/// </summary>
public static class GoLiveEndpoints
{
    public static void MapGoLiveEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/golive").WithTags("golive");

        // Step 1: request a one-time confirmation code.
        g.MapPost("/request-code", (ITenantContext tc, ILiveTradingArm arm) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var code = arm.RequestCode(tc.TenantId!.Value);
            return Results.Ok(new
            {
                message = "Confirmation code generated. Echo it to /api/golive/confirm within 5 minutes to arm live execution. WARNING: arming allows real money to be placed automatically.",
                code
            });
        });

        // Step 2: confirm the code to arm.
        g.MapPost("/confirm", (ConfirmRequest req, ITenantContext tc, ILiveTradingArm arm) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.Code))
                return Results.BadRequest(new { error = "code is required" });
            var ok = arm.Confirm(tc.TenantId!.Value, req.Code);
            if (!ok)
                return Results.BadRequest(new { error = "Invalid or expired confirmation code. Request a new code." });
            return Results.Ok(new
            {
                armed   = true,
                message = "Live execution is ARMED. Real orders will be placed on the next qualifying candle. Send /api/golive/killswitch to disarm at any time."
            });
        });

        // Step 3 / emergency: disarm immediately.
        g.MapPost("/killswitch", (ITenantContext tc, ILiveTradingArm arm) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            arm.Disarm(tc.TenantId!.Value);
            return Results.Ok(new { armed = false, message = "Live execution DISARMED. No real orders will be placed until you re-arm." });
        });

        // Status: check arm state without changing it.
        g.MapGet("/status", (ITenantContext tc, ILiveTradingArm arm) =>
        {
            if (!tc.IsResolved) return Results.NotFound();
            var armed = arm.IsArmed(tc.TenantId!.Value);
            return Results.Ok(new { armed });
        });
    }

    private sealed record ConfirmRequest(string Code);
}
