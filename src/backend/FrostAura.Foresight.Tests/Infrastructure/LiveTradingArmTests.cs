using FluentAssertions;
using FrostAura.Foresight.Infrastructure.Live;
using Xunit;

namespace FrostAura.Foresight.Tests.Infrastructure;

/// <summary>
/// Unit tests for <see cref="LiveTradingArm"/> state machine.
///
/// Covers:
///   - RequestCode → Confirm with the correct code arms the tenant.
///   - Confirm with a wrong code does NOT arm.
///   - Confirm without requesting a code first returns false and does not arm.
///   - Disarm clears the armed flag and pending code.
///   - Kill-switch (Disarm) on an already-disarmed tenant is safe.
///   - Per-tenant isolation: arming tenant A does not arm tenant B.
///   - After Disarm, a new RequestCode + Confirm cycle re-arms correctly.
/// </summary>
public class LiveTradingArmTests
{
    private static LiveTradingArm Build() => new();

    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    // ── IsArmed default ───────────────────────────────────────────────────────────

    [Fact]
    public void IsArmed_returns_false_before_any_interaction()
    {
        var arm = Build();

        arm.IsArmed(TenantA).Should().BeFalse();
    }

    // ── Correct code arms ────────────────────────────────────────────────────────

    [Fact]
    public void Confirm_with_correct_code_returns_true_and_arms()
    {
        var arm  = Build();
        var code = arm.RequestCode(TenantA);

        var result = arm.Confirm(TenantA, code);

        result.Should().BeTrue();
        arm.IsArmed(TenantA).Should().BeTrue();
    }

    [Fact]
    public void Confirm_with_correct_code_removes_pending_code()
    {
        var arm  = Build();
        var code = arm.RequestCode(TenantA);

        arm.Confirm(TenantA, code);

        // A second Confirm with the same code must return false (code consumed).
        arm.Confirm(TenantA, code).Should().BeFalse();
    }

    // ── Wrong code does NOT arm ───────────────────────────────────────────────────

    [Fact]
    public void Confirm_with_wrong_code_returns_false_and_does_not_arm()
    {
        var arm  = Build();
        arm.RequestCode(TenantA);

        var result = arm.Confirm(TenantA, "000000");

        result.Should().BeFalse();
        arm.IsArmed(TenantA).Should().BeFalse();
    }

    [Fact]
    public void Confirm_without_requesting_a_code_returns_false()
    {
        var arm = Build();

        var result = arm.Confirm(TenantA, "123456");

        result.Should().BeFalse();
        arm.IsArmed(TenantA).Should().BeFalse();
    }

    // ── Disarm / kill-switch ──────────────────────────────────────────────────────

    [Fact]
    public void Disarm_clears_armed_state()
    {
        var arm  = Build();
        var code = arm.RequestCode(TenantA);
        arm.Confirm(TenantA, code);
        arm.IsArmed(TenantA).Should().BeTrue("pre-condition: was armed");

        arm.Disarm(TenantA);

        arm.IsArmed(TenantA).Should().BeFalse();
    }

    [Fact]
    public void Disarm_clears_pending_code_so_subsequent_confirm_fails()
    {
        var arm  = Build();
        var code = arm.RequestCode(TenantA);

        arm.Disarm(TenantA); // disarm before confirming

        arm.Confirm(TenantA, code).Should().BeFalse("pending code must be cleared by Disarm");
        arm.IsArmed(TenantA).Should().BeFalse();
    }

    [Fact]
    public void Disarm_on_already_disarmed_tenant_does_not_throw()
    {
        var arm = Build();

        var act = () => arm.Disarm(TenantA);

        act.Should().NotThrow();
        arm.IsArmed(TenantA).Should().BeFalse();
    }

    // ── Re-arm after Disarm ───────────────────────────────────────────────────────

    [Fact]
    public void Re_arm_cycle_after_disarm_works_correctly()
    {
        var arm  = Build();

        var code1 = arm.RequestCode(TenantA);
        arm.Confirm(TenantA, code1);
        arm.IsArmed(TenantA).Should().BeTrue();

        arm.Disarm(TenantA);
        arm.IsArmed(TenantA).Should().BeFalse();

        var code2 = arm.RequestCode(TenantA);
        arm.Confirm(TenantA, code2).Should().BeTrue();
        arm.IsArmed(TenantA).Should().BeTrue();
    }

    // ── Per-tenant isolation ──────────────────────────────────────────────────────

    [Fact]
    public void Arming_tenant_A_does_not_arm_tenant_B()
    {
        var arm   = Build();
        var codeA = arm.RequestCode(TenantA);
        arm.Confirm(TenantA, codeA);

        arm.IsArmed(TenantA).Should().BeTrue();
        arm.IsArmed(TenantB).Should().BeFalse("arming A must not affect B");
    }

    [Fact]
    public void Code_for_tenant_A_does_not_arm_tenant_B()
    {
        var arm   = Build();
        var codeA = arm.RequestCode(TenantA);
        arm.RequestCode(TenantB); // B has its own pending code

        // Attempt to arm B using A's code.
        var result = arm.Confirm(TenantB, codeA);

        result.Should().BeFalse();
        arm.IsArmed(TenantB).Should().BeFalse();
    }

    [Fact]
    public void Disarming_tenant_A_does_not_affect_tenant_B()
    {
        var arm   = Build();
        var codeA = arm.RequestCode(TenantA);
        arm.Confirm(TenantA, codeA);
        var codeB = arm.RequestCode(TenantB);
        arm.Confirm(TenantB, codeB);

        arm.Disarm(TenantA);

        arm.IsArmed(TenantA).Should().BeFalse();
        arm.IsArmed(TenantB).Should().BeTrue("B must remain armed");
    }

    // ── RequestCode always returns a 6-digit numeric string ──────────────────────

    [Fact]
    public void RequestCode_returns_six_digit_numeric_string()
    {
        var arm  = Build();
        var code = arm.RequestCode(TenantA);

        code.Should().MatchRegex(@"^\d{6}$", "code must be a 6-digit decimal string");
    }
}
