using System.Runtime.CompilerServices;
using FluentAssertions;
using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Application.Markets;
using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Application.Trading;
using FrostAura.Foresight.Domain.Live;
using FrostAura.Foresight.Domain.Markets;
using FrostAura.Foresight.Domain.Paper;
using FrostAura.Foresight.Domain.Trading;
using FrostAura.Foresight.Infrastructure.Adapters;
using FrostAura.Foresight.Infrastructure.Live;
using FrostAura.Foresight.Infrastructure.Paper;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FrostAura.Foresight.Tests.Infrastructure;

/// <summary>
/// Placement-chokepoint coverage for the paper engine (the faithful dry-run of live placement):
///   • SERVED == VALIDATED — the side is decided on the model's emitted pUp; the legacy rescaler is
///     telemetry only. Instrumented with a fake rescaler that INVERTS probabilities: if the rescaler
///     leaked into the decision the side would flip.
///   • EV gate — no bet when the chosen side's win probability does not strictly exceed the price.
///   • Whole-dollar quantization at placement (floor; sub-$1 = clean abstain, session stays alive).
///   • Concurrent-exposure cap — skip (not bust) when tenant-wide unresolved exposure + stake would
///     exceed MaxTotalExposurePctBankroll of the combined active-session bankroll.
/// Uses the real PaperTradingService over an in-memory DbContext; Binance is wired to a throwing
/// handler proving the placement-only path never touches the network.
/// </summary>
public class PaperPlacementGuardrailTests
{
    private const string Symbol = "BTCUSDT";
    private const string Interval = "1h";
    private static readonly long IntervalMsValue = BinanceMarketDataClient.IntervalMs(Interval);

    // ── Served == validated ─────────────────────────────────────────────────────

    [Fact]
    public async Task Side_is_decided_on_served_pUp_not_the_rescaler()
    {
        // The inverting rescaler maps 0.65 → 0.35 (which would pick DOWN). The bet must be UP.
        var h = Harness.Build(stake: 10m);
        var session = await h.AddSessionAsync(balance: 1000m);
        var slot = await FormingSlotAsync();
        await h.AddPredictionAsync(slot, pUp: 0.65m);

        var after = await h.Service.ProcessAsync(session, default);

        var bet = after!.Bets.Should().ContainSingle().Subject;
        bet.Side.Should().Be("UP", "the decision must run off the served pUp (0.65), not the inverted rescaler output (0.35)");
        bet.PredictedProbUp.Should().Be(0.65m);
        bet.NotesJson.Should().Contain("rescalerPUp", "the rescaler output is persisted as telemetry");
    }

    // ── EV gate ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EV_gate_abstains_when_side_probability_does_not_clear_the_price()
    {
        // Side = UP (0.54 ≥ 0.5) but 0.54 ≤ entry price 0.55 ⇒ negative EV ⇒ no bet, session alive.
        var h = Harness.Build(stake: 10m);
        var session = await h.AddSessionAsync(balance: 1000m);
        var slot = await FormingSlotAsync();
        await h.AddPredictionAsync(slot, pUp: 0.54m);

        var after = await h.Service.ProcessAsync(session, default);

        after!.Bets.Should().BeEmpty();
        after.Bust.Should().BeFalse();
        after.StoppedAt.Should().BeNull();
    }

    // ── Whole-dollar quantization at placement ──────────────────────────────────

    [Fact]
    public async Task Stake_is_floored_to_whole_dollars_at_placement()
    {
        var h = Harness.Build(stake: 10.7m);
        var session = await h.AddSessionAsync(balance: 1000m);
        var slot = await FormingSlotAsync();
        await h.AddPredictionAsync(slot, pUp: 0.65m);

        var after = await h.Service.ProcessAsync(session, default);

        after!.Bets.Should().ContainSingle().Which.Size.Should().Be(10m, "stakes floor to whole dollars, never round up");
    }

    [Fact]
    public async Task Sub_dollar_stake_is_a_clean_abstain_not_a_bust()
    {
        var h = Harness.Build(stake: 0.9m);
        var session = await h.AddSessionAsync(balance: 1000m);
        var slot = await FormingSlotAsync();
        await h.AddPredictionAsync(slot, pUp: 0.65m);

        var after = await h.Service.ProcessAsync(session, default);

        after!.Bets.Should().BeEmpty();
        after.Bust.Should().BeFalse();
        after.StoppedAt.Should().BeNull();
    }

    // ── Concurrent-exposure cap ─────────────────────────────────────────────────

    [Fact]
    public async Task Exposure_cap_skips_placement_when_tenant_wide_open_exposure_would_exceed_it()
    {
        // Two active sessions of $100 ⇒ combined bankroll $200, cap = 10% = $20. A sibling session
        // already has $15 unresolved; a new $10 stake ⇒ $25 > $20 ⇒ SKIP (not bust, not stop).
        var h = Harness.Build(stake: 10m);
        var target = await h.AddSessionAsync(balance: 100m);
        var sibling = await h.AddSessionAsync(balance: 100m, label: "sibling");
        var slot = await FormingSlotAsync();
        await h.AddOpenBetAsync(sibling, size: 15m, targetOpenTime: slot);
        await h.AddPredictionAsync(slot, pUp: 0.65m);

        var after = await h.Service.ProcessAsync(target, default);

        after!.Bets.Should().BeEmpty("the cap must skip the bet");
        after.Bust.Should().BeFalse("the cap is a skip, never a bust");
        after.StoppedAt.Should().BeNull("the session keeps running and retries later candles");
        h.Hub.Events.Should().NotContain(e => e.Kind == PaperTradingEventKind.BetPlaced);
    }

    [Fact]
    public async Task Exposure_cap_allows_placement_when_under_the_cap()
    {
        // Same shape but the sibling's open exposure is $5: $5 + $10 = $15 ≤ $20 ⇒ the bet places.
        var h = Harness.Build(stake: 10m);
        var target = await h.AddSessionAsync(balance: 100m);
        var sibling = await h.AddSessionAsync(balance: 100m, label: "sibling");
        var slot = await FormingSlotAsync();
        await h.AddOpenBetAsync(sibling, size: 5m, targetOpenTime: slot);
        await h.AddPredictionAsync(slot, pUp: 0.65m);

        var after = await h.Service.ProcessAsync(target, default);

        after!.Bets.Should().ContainSingle().Which.Size.Should().Be(10m);
        h.Hub.Events.Should().Contain(e => e.Kind == PaperTradingEventKind.BetPlaced);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The open time of the currently-forming candle. When the wall clock sits inside the engine's
    /// late-placement window at the end of the candle (placement would be skipped), waits for the
    /// next boundary so the test is deterministic instead of boundary-flaky.
    /// </summary>
    private static async Task<long> FormingSlotAsync()
    {
        while (true)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var slot = nowMs / IntervalMsValue * IntervalMsValue;
            var msLeft = slot + IntervalMsValue - nowMs;
            if (msLeft >= 15_000) return slot;
            await Task.Delay((int)msLeft + 100);
        }
    }

    private sealed class Harness
    {
        public required PaperTradingService Service { get; init; }
        public required ForesightDbContext Db { get; init; }
        public required Guid TenantId { get; init; }
        public required FakeHub Hub { get; init; }

        public static Harness Build(decimal stake, decimal entryPrice = 0.55m)
        {
            var db = new ForesightDbContext(new DbContextOptionsBuilder<ForesightDbContext>()
                .UseInMemoryDatabase($"paper-guardrail-{Guid.NewGuid()}")
                .Options);
            var tenantId = Guid.NewGuid();
            var tenant = new TenantContext();
            tenant.Set(tenantId, "default");
            var hub = new FakeHub();
            var service = new PaperTradingService(
                db,
                tenant,
                new BinanceMarketDataClient(new HttpClient(new ThrowingHandler())),
                hub,
                new InvertingRescaler(),
                new FixedVenuePrices(entryPrice),
                new TradingNotifier(new CapturingChannelAdapter(), NullLogger<TradingNotifier>.Instance),
                new FixedStakeEvaluator(stake),
                new NoPredictionService(),
                Options.Create(new TradingGuardrailOptions()),
                NullLogger<PaperTradingService>.Instance);
            return new Harness { Service = service, Db = db, TenantId = tenantId, Hub = hub };
        }

        public async Task<PaperSession> AddSessionAsync(decimal balance, string label = "")
        {
            var session = new PaperSession
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                Symbol = Symbol,
                Interval = Interval,
                Label = label,
                StartedAt = DateTimeOffset.UtcNow,
                InitialBalance = balance,
                InitialBetSize = 2m,
                StrategyId = "flat",
                Gated = false,
                CurrentBalance = balance,
                CurrentBetSize = 2m,
            };
            Db.PaperSessions.Add(session);
            await Db.SaveChangesAsync();
            return session;
        }

        public async Task AddPredictionAsync(long targetOpenTime, decimal pUp)
        {
            Db.LivePredictions.Add(new LivePrediction
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                Symbol = Symbol,
                Interval = Interval,
                TargetOpenTime = targetOpenTime,
                AnchorClose = 50_000m,
                PredictedClose = 50_100m,
                PredictedChangePct = 0.2m,
                DirectionUpProbability = pUp,
                Confidence = Math.Abs(pUp - 0.5m) * 2m,
                Model = "test-model",
            });
            await Db.SaveChangesAsync();
        }

        public async Task AddOpenBetAsync(PaperSession session, decimal size, long targetOpenTime)
        {
            Db.PaperBets.Add(new PaperBet
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                SessionId = session.Id,
                TargetOpenTime = targetOpenTime,
                Side = "UP",
                PredictedProbUp = 0.6m,
                AnchorClose = 50_000m,
                Size = size,
                BalanceBefore = session.CurrentBalance,
                PlacedAt = DateTimeOffset.FromUnixTimeMilliseconds(targetOpenTime),
                EntryPrice = 0.55m,
            });
            await Db.SaveChangesAsync();
        }
    }

    // ── Fakes ───────────────────────────────────────────────────────────────────

    private sealed class FakeHub : IPaperTradingEventHub
    {
        public List<PaperTradingEvent> Events { get; } = new();
        public void Publish(PaperTradingEvent evt) => Events.Add(evt);
        public async IAsyncEnumerable<PaperTradingEvent> Subscribe([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>Adversarial legacy rescaler: returns 1−p. Any decision leak flips the side.</summary>
    private sealed class InvertingRescaler : ICalibrationRescaler
    {
        public Task<decimal> RescaleAsync(Guid tenantId, string interval, decimal rawP, CancellationToken ct)
            => Task.FromResult(1m - rawP);
        public void Invalidate(Guid tenantId) { }
    }

    private sealed class FixedVenuePrices : IVenuePriceStore
    {
        private readonly decimal _price;
        public FixedVenuePrices(decimal price) => _price = price;
        public Task<EntryQuote?> GetEntryAsync(string venue, string symbol, string interval, long targetOpenTime, CancellationToken ct)
            => Task.FromResult<EntryQuote?>(new EntryQuote(_price, _price, false, "mkt-test"));
        public Task<EntryQuote> EnsureEntryAsync(string venue, string symbol, string interval, long targetOpenTime, decimal pUp, CancellationToken ct)
            => Task.FromResult(new EntryQuote(_price, _price, false, "mkt-test"));
    }

    private sealed class FixedStakeEvaluator : IStrategyEvaluator
    {
        private readonly decimal _stake;
        public FixedStakeEvaluator(decimal stake) => _stake = stake;
        public Task<decimal> NextStakeAsync(string strategyId, StrategyStep step, FlowContext flowCtx, CancellationToken ct)
            => Task.FromResult(_stake);
        public bool IsBuiltIn(string strategyId) => true;
    }

    private sealed class NoPredictionService : ILivePredictionService
    {
        public Task<LivePrediction> PredictAsync(string symbol, string interval, int horizon, CancellationToken ct)
            => throw new NotSupportedException("Placement tests pre-seed predictions.");
        public Task<IReadOnlyList<LivePrediction>> ListAsync(string symbol, string interval, int take, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<LivePrediction>>(Array.Empty<LivePrediction>());
        public Task<int> ResolveMaturedAsync(string symbol, string interval, CancellationToken ct)
            => Task.FromResult(0);
        public Task<int> BackfillHistoryAsync(string symbol, string interval, int candleCount, CancellationToken ct)
            => Task.FromResult(0);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("Binance must not be called on the placement-only path.");
    }
}
