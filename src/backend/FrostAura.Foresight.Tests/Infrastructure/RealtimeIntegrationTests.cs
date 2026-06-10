using FluentAssertions;
using FrostAura.Foresight.Application.Backtesting;
using FrostAura.Foresight.Application.Markets;
using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Domain.Markets;
using FrostAura.Foresight.Domain.Models;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Domain.Tenancy;
using FrostAura.Foresight.Infrastructure.Adapters;
using FrostAura.Foresight.Infrastructure.Live;
using FrostAura.Foresight.Infrastructure.Persistence;
using FrostAura.Foresight.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FrostAura.Foresight.Tests.Infrastructure;

/// <summary>
/// End-to-end coverage for the two service-level publish paths that drive the no-polling SSE
/// migration: <see cref="ModelTrainingService"/> emitting model-lifecycle events, and
/// <see cref="LiveSessionEngine"/> emitting live-session-changed events. These are the integration
/// points the hub-only unit tests (RealtimeEventHubTests) don't exercise — here we drive the real
/// services and assert the events actually reach a subscriber, which is exactly what the frontend's
/// RealtimeSync relies on instead of polling.
///
/// The subscribe ordering is deterministic, not timing-based: the first <c>MoveNextAsync()</c> runs
/// the hub's async iterator synchronously up to the point where it registers the subscriber channel,
/// so any publish AFTER that call is guaranteed to be observed.
/// </summary>
public sealed class RealtimeIntegrationTests
{
    // ── Model training lifecycle → ModelEventHub ────────────────────────────────────────────────

    [Fact]
    public async Task StartTraining_publishes_training_then_trained_on_success()
    {
        var (svc, hub, modelId) = BuildTrainingService(fitSucceeds: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await using var sub = hub.Subscribe(cts.Token).GetAsyncEnumerator();
        var move = sub.MoveNextAsync(); // registers the subscriber channel before we publish

        await svc.StartTrainingAsync(modelId, "BTCUSDT", 0, "5m", cts.Token);

        // First frame is the synchronous "training" transition.
        (await move).Should().BeTrue();
        sub.Current.ModelId.Should().Be(modelId);
        sub.Current.Kind.Should().Be(ModelEventKind.Training);

        // The background fit (a no-op success here) publishes the terminal "trained".
        var terminal = await NextFor(sub, modelId);
        terminal.Kind.Should().Be(ModelEventKind.Trained);
    }

    [Fact]
    public async Task StartTraining_publishes_training_then_failed_when_fit_throws()
    {
        var (svc, hub, modelId) = BuildTrainingService(fitSucceeds: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await using var sub = hub.Subscribe(cts.Token).GetAsyncEnumerator();
        var move = sub.MoveNextAsync();

        await svc.StartTrainingAsync(modelId, "BTCUSDT", 0, "5m", cts.Token);

        (await move).Should().BeTrue();
        sub.Current.Kind.Should().Be(ModelEventKind.Training);

        var terminal = await NextFor(sub, modelId);
        terminal.Kind.Should().Be(ModelEventKind.Failed);
        terminal.Error.Should().NotBeNullOrEmpty("a failed fit carries its error message");
    }

    // ── Live session lifecycle → LiveEventHub ───────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_and_StopAsync_each_publish_a_session_changed_event()
    {
        var hub = new LiveEventHub();
        var tenantId = Guid.NewGuid();
        var (engine, _) = BuildLiveEngine(hub, tenantId, walletPusd: 1000m);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await using var sub = hub.Subscribe(cts.Token).GetAsyncEnumerator();
        var move = sub.MoveNextAsync();

        var started = await engine.StartAsync(new LiveSessionStartRequest(
            Venue: "polymarket", Symbol: "BTCUSDT", Interval: "5m",
            InitialBalance: 100m, InitialBetSize: 10m, StrategyId: "flat", Gated: false), cts.Token);

        // Start publishes a session-changed event for the new session.
        (await move).Should().BeTrue();
        sub.Current.Kind.Should().Be(LiveEventKind.SessionChanged);
        sub.Current.TenantId.Should().Be(tenantId);
        sub.Current.SessionId.Should().Be(started.Id);

        // Stopping the same session publishes another.
        var stopped = await engine.StopAsync(started.Id, cts.Token);
        stopped.Should().NotBeNull();

        var next = await NextSession(sub, started.Id);
        next.Kind.Should().Be(LiveEventKind.SessionChanged);
        next.SessionId.Should().Be(started.Id);
    }

    // ── Builders ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Wires a real <see cref="ModelTrainingService"/> whose background fit is delegated to a fake
    /// <see cref="IModelTrainingService"/> resolved from the DI scope — so the synchronous "training"
    /// publish AND the terminal "trained"/"failed" publish both run through the real code, while the
    /// heavy fit is replaced by a deterministic no-op/throw. The model carries a real candle-only flow
    /// (a matrix_builder node) so StartTrainingAsync's trainability validation passes.
    /// </summary>
    private static (ModelTrainingService Svc, ModelEventHub Hub, Guid ModelId) BuildTrainingService(bool fitSucceeds)
    {
        var hub = new ModelEventHub();
        var dbName = $"model-sse-{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<ForesightDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddSingleton<IModelEventHub>(hub);
        // The background task resolves IModelTrainingService and calls TrainAsync — give it the fake.
        services.AddScoped<IModelTrainingService>(_ => new FakeTrainer(fitSucceeds));
        var sp = services.BuildServiceProvider();

        var tenantId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using (var seed = sp.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<ForesightDbContext>();
            db.Models.Add(new Model
            {
                Id = modelId,
                TenantId = tenantId,
                Name = "sse-train-target",
                Kind = "deterministic",
                SupportsBacktesting = true,
                IsBuiltIn = false,
                Definition = BuiltInModels.BuildForesight5mV1Flow(),
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.SaveChanges();
        }

        // The service under test gets its own scope/context with the tenant pinned.
        var scope = sp.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();
        ((TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>()).Set(tenantId, "default");
        var scopes = sp.GetRequiredService<IServiceScopeFactory>();

        // trainer is unused on this instance — the background fit is the DI-resolved FakeTrainer.
        var svc = new ModelTrainingService(db2, (ITenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>(),
            trainer: null!, scopes, hub, NullLogger<ModelTrainingService>.Instance);
        return (svc, hub, modelId);
    }

    private static (LiveSessionEngine Engine, CapturingLedger Ledger) BuildLiveEngine(
        ILiveEventHub hub, Guid tenantId, decimal walletPusd)
    {
        var db = new ForesightDbContext(new DbContextOptionsBuilder<ForesightDbContext>()
            .UseInMemoryDatabase($"live-sse-{Guid.NewGuid()}").Options);
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", Slug = "default", CreatedAt = DateTimeOffset.UtcNow });
        db.SaveChanges();

        var tenant = new TenantContext();
        tenant.Set(tenantId, "default");

        var ledger = new CapturingLedger(walletPusd: walletPusd, otherActiveBalance: 0m);
        var notifier = new TradingNotifier(new LogChannelAdapter(NullLogger<LogChannelAdapter>.Instance),
            NullLogger<TradingNotifier>.Instance);
        var engine = new LiveSessionEngine(
            db, tenant,
            new StubConnectorFactory(),
            new LiveTradingArm(),
            ledger,
            new StubCalibration(),
            new StubVenuePrices(),
            new BinanceMarketDataClient(new HttpClient()),
            Options.Create(new TradingGuardrailOptions()),
            notifier,
            hub,
            NullLogger<LiveSessionEngine>.Instance);
        return (engine, ledger);
    }

    // ── Subscription helpers ────────────────────────────────────────────────────────────────────

    private static async Task<ModelEvent> NextFor(IAsyncEnumerator<ModelEvent> sub, Guid modelId)
    {
        while (await sub.MoveNextAsync())
            if (sub.Current.ModelId == modelId && sub.Current.Kind != ModelEventKind.Training)
                return sub.Current;
        throw new InvalidOperationException("stream ended before a terminal model event arrived");
    }

    private static async Task<LiveEvent> NextSession(IAsyncEnumerator<LiveEvent> sub, Guid sessionId)
    {
        while (await sub.MoveNextAsync())
            if (sub.Current.Kind == LiveEventKind.SessionChanged && sub.Current.SessionId == sessionId)
                return sub.Current;
        throw new InvalidOperationException("stream ended before the session-changed event arrived");
    }

    // ── Fakes / stubs ───────────────────────────────────────────────────────────────────────────

    /// <summary>Stands in for the background fit: succeeds (no-op) or throws, deterministically.</summary>
    private sealed class FakeTrainer : IModelTrainingService
    {
        private readonly bool _succeeds;
        public FakeTrainer(bool succeeds) => _succeeds = succeeds;

        public Task<ModelTrainResult> TrainAsync(Guid modelId, string symbol, int holdoutDays, string? interval, CancellationToken ct)
            => _succeeds
                ? Task.FromResult<ModelTrainResult>(null!) // return value is discarded by the background runner
                : throw new InvalidOperationException("synthetic training failure");

        public Task StartTrainingAsync(Guid modelId, string symbol, int holdoutDays, string? interval, CancellationToken ct)
            => throw new NotSupportedException("the fake is only resolved for the background TrainAsync call");
    }

    // Start/Stop never touch the connector, calibration or venue-price stubs — they exist only to
    // satisfy the constructor. Each throws if unexpectedly invoked so a regression is loud.
    private sealed class StubConnectorFactory : IPlatformConnectorFactory
    {
        public Task<IPlatformConnector> GetForTenantAsync(Guid tenantId, CancellationToken ct)
            => throw new NotSupportedException("connector is only used by ProcessAsync, not Start/Stop");
        public void Invalidate(Guid tenantId) { }
    }

    private sealed class StubCalibration : ICalibrationRescaler
    {
        public Task<decimal> RescaleAsync(Guid tenantId, string interval, decimal rawP, CancellationToken ct)
            => throw new NotSupportedException("calibration is only used by ProcessAsync");
        public void Invalidate(Guid tenantId) { }
    }

    private sealed class StubVenuePrices : IVenuePriceStore
    {
        public Task<EntryQuote?> GetEntryAsync(string venue, string symbol, string interval, long targetOpenTime, CancellationToken ct)
            => throw new NotSupportedException("venue prices are only used by ProcessAsync");
        public Task<EntryQuote> EnsureEntryAsync(string venue, string symbol, string interval, long targetOpenTime, decimal pUp, CancellationToken ct)
            => throw new NotSupportedException("venue prices are only used by ProcessAsync");
    }
}
