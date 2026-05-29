using System.Text.Json;
using FrostAura.Foresight.Domain.Backtesting;
using FrostAura.Foresight.Domain.Bankroll;
using FrostAura.Foresight.Domain.Chaos;
using FrostAura.Foresight.Domain.Ledger;
using FrostAura.Foresight.Domain.Live;
using FrostAura.Foresight.Domain.MarketData;
using FrostAura.Foresight.Domain.Markets;
using FrostAura.Foresight.Domain.Models;
using FrostAura.Foresight.Domain.Paper;
using FrostAura.Foresight.Domain.Positions;
using FrostAura.Foresight.Domain.Strategies;
using FrostAura.Foresight.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;


namespace FrostAura.Foresight.Infrastructure.Persistence;

public sealed class ForesightDbContext : DbContext
{
    public ForesightDbContext(DbContextOptions<ForesightDbContext> options) : base(options) { }

    public DbSet<Strategy> Strategies => Set<Strategy>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Market> Markets => Set<Market>();
    public DbSet<BankrollEntry> Bankrolls => Set<BankrollEntry>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<LivePrediction> LivePredictions => Set<LivePrediction>();
    public DbSet<PaperSession> PaperSessions => Set<PaperSession>();
    public DbSet<PaperBet> PaperBets => Set<PaperBet>();
    public DbSet<Model> Models => Set<Model>();
    public DbSet<ActiveModel> ActiveModels => Set<ActiveModel>();
    public DbSet<HistoricalCandle> HistoricalCandles => Set<HistoricalCandle>();
    public DbSet<MicrostructureBar> HistoricalMicrostructure => Set<MicrostructureBar>();
    public DbSet<Backtest> Backtests => Set<Backtest>();
    public DbSet<BacktestBet> BacktestBets => Set<BacktestBet>();
    public DbSet<VenueMarketPrice> VenueMarketPrices => Set<VenueMarketPrice>();
    public DbSet<ChaosRun> ChaosRuns => Set<ChaosRun>();
    public DbSet<ChaosSample> ChaosSamples => Set<ChaosSample>();
    public DbSet<LiveSession> LiveSessions => Set<LiveSession>();
    public DbSet<LiveBet> LiveBets => Set<LiveBet>();
    public DbSet<AccountLedgerEntry> AccountLedger => Set<AccountLedgerEntry>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        mb.Entity<Strategy>(b =>
        {
            b.ToTable("strategies");
            b.HasKey(s => s.Id);
            b.Property(s => s.Name).HasMaxLength(200).IsRequired();
            b.Property(s => s.Description).HasMaxLength(2000);
            b.Property(s => s.Definition).HasColumnType("jsonb");
            b.Property(s => s.Params).HasColumnType("jsonb");
            b.HasIndex(s => new { s.TenantId, s.Name }).IsUnique();
        });

        mb.Entity<Tenant>(b =>
        {
            b.ToTable("tenants");
            b.HasKey(t => t.Id);
            b.Property(t => t.Name).HasMaxLength(200).IsRequired();
            b.Property(t => t.Slug).HasMaxLength(80).IsRequired();
            b.HasIndex(t => t.Slug).IsUnique();
            b.Property(t => t.CreatedAt);
            b.Property(t => t.Settings).HasConversion(
                v => JsonSerializer.Serialize(v, jsonOptions),
                v => JsonSerializer.Deserialize<TenantSettings>(v, jsonOptions) ?? new TenantSettings())
                .HasColumnType("jsonb");
        });

        mb.Entity<Market>(b =>
        {
            b.ToTable("markets");
            b.HasKey(m => m.Id);
            b.Property(m => m.ProviderId).HasMaxLength(60).IsRequired();
            b.Property(m => m.ExternalId).HasMaxLength(200).IsRequired();
            b.Property(m => m.Question).HasMaxLength(1000).IsRequired();
            b.Property(m => m.Category).HasMaxLength(80).IsRequired();
            b.Property(m => m.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(m => m.ResolutionCriteria).HasMaxLength(2000);
            b.HasIndex(m => new { m.TenantId, m.ProviderId, m.ExternalId }).IsUnique();
        });

        mb.Entity<BankrollEntry>(b =>
        {
            b.ToTable("bankrolls");
            b.HasKey(x => x.Id);
            b.Property(x => x.ProviderId).HasMaxLength(60).IsRequired();
            b.Property(x => x.TotalUsd).HasColumnType("numeric(18,2)");
            b.Property(x => x.InFlightUsd).HasColumnType("numeric(18,2)");
            b.Ignore(x => x.AvailableUsd);
            b.HasIndex(x => new { x.TenantId, x.ProviderId, x.RecordedAt });
        });

        mb.Entity<Position>(b =>
        {
            b.ToTable("positions");
            b.HasKey(p => p.Id);
            b.Property(p => p.Mode).HasConversion<string>().HasMaxLength(10);
            b.Property(p => p.Side).HasConversion<string>().HasMaxLength(10);
            b.Property(p => p.Status).HasConversion<string>().HasMaxLength(15);
            b.Property(p => p.Shares).HasColumnType("numeric(18,4)");
            b.Property(p => p.AverageEntryPrice).HasColumnType("numeric(8,5)");
            b.Property(p => p.CurrentPrice).HasColumnType("numeric(8,5)");
            b.Property(p => p.RealizedPnlUsd).HasColumnType("numeric(18,2)");
            b.HasIndex(p => new { p.TenantId, p.MarketId, p.Status });
        });

        mb.Entity<LivePrediction>(b =>
        {
            b.ToTable("live_predictions");
            b.HasKey(p => p.Id);
            b.Property(p => p.Symbol).HasMaxLength(20).IsRequired();
            b.Property(p => p.Interval).HasMaxLength(10).IsRequired();
            b.Property(p => p.AnchorClose).HasColumnType("numeric(20,8)");
            b.Property(p => p.PredictedClose).HasColumnType("numeric(20,8)");
            b.Property(p => p.PredictedChangePct).HasColumnType("numeric(10,4)");
            b.Property(p => p.Confidence).HasColumnType("numeric(6,5)");
            b.Property(p => p.DirectionUpProbability).HasColumnType("numeric(6,5)");
            b.Property(p => p.TargetHitProbability).HasColumnType("numeric(6,5)").HasDefaultValue(0.5m);
            b.Property(p => p.ClosePercentile05).HasColumnType("numeric(20,8)").HasDefaultValue(0m);
            b.Property(p => p.ClosePercentile50).HasColumnType("numeric(20,8)").HasDefaultValue(0m);
            b.Property(p => p.ClosePercentile95).HasColumnType("numeric(20,8)").HasDefaultValue(0m);
            b.Property(p => p.ActualClose).HasColumnType("numeric(20,8)");
            b.Property(p => p.AbsoluteErrorPct).HasColumnType("numeric(10,4)");
            b.Property(p => p.Reasoning).HasMaxLength(8000);
            b.Property(p => p.Model).HasMaxLength(120);
            b.Property(p => p.SupportingDataJson).HasColumnType("jsonb");
            b.Property(p => p.PromptTraceJson).HasColumnType("jsonb");
            // Per-model dispatch lands in iter-4 — the unique key widens to include ModelId so two
            // models can predict the same candle in parallel (A/B is post-v1 but the schema is ready).
            b.HasIndex(p => new { p.TenantId, p.Symbol, p.Interval, p.TargetOpenTime, p.ModelId }).IsUnique();
            b.HasIndex(p => new { p.TenantId, p.Symbol, p.Interval, p.CreatedAt });
            b.HasIndex(p => new { p.TenantId, p.Symbol, p.Interval, p.ModelId, p.CreatedAt });
        });

        mb.Entity<PaperSession>(b =>
        {
            b.ToTable("paper_sessions");
            b.HasKey(s => s.Id);
            b.Property(s => s.Symbol).HasMaxLength(20).IsRequired();
            b.Property(s => s.Interval).HasMaxLength(10).IsRequired();
            b.Property(s => s.Label).HasMaxLength(60).IsRequired();
            b.Property(s => s.StrategyId).HasMaxLength(32).IsRequired();
            b.Property(s => s.ConfigHash).HasMaxLength(64);
            b.Property(s => s.InitialBalance).HasColumnType("numeric(20,4)");
            b.Property(s => s.InitialBetSize).HasColumnType("numeric(20,4)");
            b.Property(s => s.CurrentBalance).HasColumnType("numeric(20,4)");
            b.Property(s => s.CurrentBetSize).HasColumnType("numeric(20,4)");
            b.Property(s => s.PeakBorrowed).HasColumnType("numeric(20,4)");
            // Partial unique index: one active session per (tenant, symbol, interval, LABEL). Label=''
            // is the chart UI's primary session; non-empty labels let the bot run same-market parallel
            // strategy comparisons. Stopped sessions accumulate as history and may collide on the key.
            b.HasIndex(s => new { s.TenantId, s.Symbol, s.Interval, s.Label })
                .IsUnique()
                .HasFilter("\"StoppedAt\" IS NULL");
            b.HasIndex(s => new { s.TenantId, s.StartedAt });
            b.HasMany(s => s.Bets)
                .WithOne()
                .HasForeignKey(x => x.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<PaperBet>(b =>
        {
            b.ToTable("paper_bets");
            b.HasKey(x => x.Id);
            b.Property(x => x.Side).HasMaxLength(4).IsRequired();
            b.Property(x => x.Outcome).HasMaxLength(8);
            b.Property(x => x.PredictedProbUp).HasColumnType("numeric(6,5)");
            b.Property(x => x.AnchorClose).HasColumnType("numeric(20,8)");
            b.Property(x => x.Size).HasColumnType("numeric(20,4)");
            b.Property(x => x.BalanceBefore).HasColumnType("numeric(20,4)");
            b.Property(x => x.Payout).HasColumnType("numeric(20,4)");
            b.Property(x => x.BalanceAfter).HasColumnType("numeric(20,4)");
            b.Property(x => x.ActualClose).HasColumnType("numeric(20,8)");
            b.Property(x => x.NotesJson).HasColumnType("jsonb");
            b.Property(x => x.EntryPrice).HasColumnType("numeric(8,5)");
            b.Property(x => x.Shares).HasColumnType("numeric(18,6)");
            b.Property(x => x.MarketExternalId).HasMaxLength(200);
            // Never two bets on the same candle within a session.
            b.HasIndex(x => new { x.SessionId, x.TargetOpenTime }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.SessionId, x.Resolved });
        });

        mb.Entity<Model>(b =>
        {
            b.ToTable("models");
            b.HasKey(m => m.Id);
            b.Property(m => m.Name).HasMaxLength(200).IsRequired();
            b.Property(m => m.Description).HasMaxLength(2000);
            b.Property(m => m.Kind).HasMaxLength(20).IsRequired();
            b.Property(m => m.Definition).HasColumnType("jsonb").IsRequired();
            b.Property(m => m.TrainedState).HasColumnType("jsonb");
            b.Property(m => m.TrainingValidationAccuracy).HasColumnType("numeric(6,5)");
            b.Property(m => m.BacktestAccuracy).HasColumnType("numeric(6,5)");
            b.Property(m => m.TrainSymbol).HasMaxLength(20);
            b.Property(m => m.TrainInterval).HasMaxLength(10);
            b.Property(m => m.TrainingStatus).HasMaxLength(20);
            b.Property(m => m.TrainingError).HasMaxLength(2000);
            b.Property(m => m.SimpleDescription).HasMaxLength(500);
            b.Property(m => m.TechnicalDescription).HasMaxLength(1000);
            b.HasIndex(m => new { m.TenantId, m.Name }).IsUnique();
            b.HasIndex(m => new { m.TenantId, m.IsDefault });
        });

        mb.Entity<ActiveModel>(b =>
        {
            b.ToTable("active_models");
            b.HasKey(a => new { a.TenantId, a.Symbol, a.Interval });
            b.Property(a => a.Symbol).HasMaxLength(20).IsRequired();
            b.Property(a => a.Interval).HasMaxLength(10).IsRequired();
        });

        mb.Entity<HistoricalCandle>(b =>
        {
            b.ToTable("historical_candles");
            b.HasKey(c => new { c.Symbol, c.Interval, c.OpenTime });
            b.Property(c => c.Symbol).HasMaxLength(20).IsRequired();
            b.Property(c => c.Interval).HasMaxLength(10).IsRequired();
            b.Property(c => c.Open).HasColumnType("numeric(20,8)");
            b.Property(c => c.High).HasColumnType("numeric(20,8)");
            b.Property(c => c.Low).HasColumnType("numeric(20,8)");
            b.Property(c => c.Close).HasColumnType("numeric(20,8)");
            b.Property(c => c.Volume).HasColumnType("numeric(28,8)");
        });

        mb.Entity<MicrostructureBar>(b =>
        {
            b.ToTable("historical_microstructure");
            b.HasKey(c => new { c.Symbol, c.Interval, c.OpenTime });
            b.Property(c => c.Symbol).HasMaxLength(20).IsRequired();
            b.Property(c => c.Interval).HasMaxLength(10).IsRequired();
            b.Property(c => c.BuyVolume).HasColumnType("numeric(28,8)");
            b.Property(c => c.SellVolume).HasColumnType("numeric(28,8)");
            b.Property(c => c.LargeBuyVolume).HasColumnType("numeric(28,8)");
            b.Property(c => c.LargeSellVolume).HasColumnType("numeric(28,8)");
            b.Property(c => c.LateBuyVolume).HasColumnType("numeric(28,8)");
            b.Property(c => c.LateSellVolume).HasColumnType("numeric(28,8)");
            b.Property(c => c.EarlyBuyVolume).HasColumnType("numeric(28,8)");
            b.Property(c => c.EarlySellVolume).HasColumnType("numeric(28,8)");
            b.Property(c => c.OpenInterest).HasColumnType("numeric(28,8)");
            b.Property(c => c.OpenInterestValue).HasColumnType("numeric(28,4)");
            b.Property(c => c.TopTraderLongShortRatio).HasColumnType("numeric(18,8)");
            b.Property(c => c.LongShortRatio).HasColumnType("numeric(18,8)");
            b.Property(c => c.TakerLongShortVolRatio).HasColumnType("numeric(18,8)");
        });

        mb.Entity<Backtest>(b =>
        {
            b.ToTable("backtests");
            b.HasKey(x => x.Id);
            b.Property(x => x.Symbol).HasMaxLength(20).IsRequired();
            b.Property(x => x.Interval).HasMaxLength(10).IsRequired();
            b.Property(x => x.Status).HasMaxLength(20).IsRequired();
            b.Property(x => x.InitialBalance).HasColumnType("numeric(20,4)");
            b.Property(x => x.InitialBetSize).HasColumnType("numeric(20,4)");
            b.Property(x => x.HitRate).HasColumnType("numeric(6,5)");
            b.Property(x => x.FinalBalance).HasColumnType("numeric(20,4)");
            b.Property(x => x.PeakBalance).HasColumnType("numeric(20,4)");
            b.Property(x => x.TroughBalance).HasColumnType("numeric(20,4)");
            b.Property(x => x.MaxDrawdown).HasColumnType("numeric(20,4)");
            b.Property(x => x.PeakBorrowed).HasColumnType("numeric(20,4)");
            b.Property(x => x.MarkersJson).HasColumnType("jsonb");
            b.Property(x => x.Error).HasMaxLength(2000);
            b.Property(x => x.MaxMartingaleStep);
            b.Property(x => x.AllowBorrow);
            b.Property(x => x.BatchId);
            b.Property(x => x.StrategyId).HasMaxLength(32).IsRequired();
            b.Property(x => x.SyntheticBetFraction).HasColumnType("numeric(6,5)");
            b.HasIndex(x => new { x.TenantId, x.ModelId, x.StartedAt });
            b.HasIndex(x => x.BatchId);
            b.HasMany(x => x.Bets)
                .WithOne()
                .HasForeignKey(x => x.BacktestId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<BacktestBet>(b =>
        {
            b.ToTable("backtest_bets");
            b.HasKey(x => x.Id);
            b.Property(x => x.Side).HasMaxLength(4).IsRequired();
            b.Property(x => x.PUpRaw).HasColumnType("numeric(6,5)");
            b.Property(x => x.PUpCalibrated).HasColumnType("numeric(6,5)");
            b.Property(x => x.Size).HasColumnType("numeric(20,4)");
            b.Property(x => x.BalanceBefore).HasColumnType("numeric(20,4)");
            b.Property(x => x.BalanceAfter).HasColumnType("numeric(20,4)");
            b.Property(x => x.BorrowedShortfall).HasColumnType("numeric(20,4)");
            b.Property(x => x.EntryPrice).HasColumnType("numeric(8,5)");
            b.Property(x => x.Shares).HasColumnType("numeric(18,6)");
            b.Property(x => x.Payout).HasColumnType("numeric(20,4)");
            b.Property(x => x.MarketExternalId).HasMaxLength(200);
            b.HasIndex(x => new { x.BacktestId, x.TargetOpenTime }).IsUnique();
        });

        mb.Entity<VenueMarketPrice>(b =>
        {
            b.ToTable("venue_market_prices");
            b.HasKey(x => x.Id);
            b.Property(x => x.Venue).HasMaxLength(60).IsRequired();
            b.Property(x => x.Symbol).HasMaxLength(20).IsRequired();
            b.Property(x => x.Interval).HasMaxLength(10).IsRequired();
            b.Property(x => x.MarketExternalId).HasMaxLength(200).IsRequired();
            b.Property(x => x.YesPrice).HasColumnType("numeric(8,5)");
            b.Property(x => x.NoPrice).HasColumnType("numeric(8,5)");
            b.Property(x => x.Source).HasMaxLength(60).IsRequired();
            b.Property(x => x.ReferenceSource).HasMaxLength(120);
            // Unique: one observed-at snapshot per (tenant, venue, symbol, interval, target, observedAt, market).
            b.HasIndex(x => new { x.TenantId, x.Venue, x.Symbol, x.Interval, x.TargetOpenTime, x.ObservedAt, x.MarketExternalId }).IsUnique();
            // Lookup index for the anti-look-ahead query.
            b.HasIndex(x => new { x.Venue, x.Symbol, x.Interval, x.TargetOpenTime });
        });

        mb.Entity<ChaosRun>(b =>
        {
            b.ToTable("chaos_runs");
            b.HasKey(r => r.Id);
            b.Property(r => r.StrategyId).HasMaxLength(32).IsRequired();
            b.Property(r => r.Symbol).HasMaxLength(20).IsRequired();
            b.Property(r => r.Interval).HasMaxLength(10).IsRequired();
            b.Property(r => r.Status).HasMaxLength(20).IsRequired();
            b.Property(r => r.BustRate).HasColumnType("numeric(8,6)");
            b.Property(r => r.ProfitP5).HasColumnType("numeric(20,4)");
            b.Property(r => r.ProfitP50).HasColumnType("numeric(20,4)");
            b.Property(r => r.ProfitP95).HasColumnType("numeric(20,4)");
            b.Property(r => r.WorstDrawdown).HasColumnType("numeric(20,4)");
            b.Property(r => r.SyntheticBetFraction).HasColumnType("numeric(6,5)");
            b.Property(r => r.Error).HasMaxLength(2000);
            b.HasIndex(r => new { r.TenantId, r.BatchId });
            b.HasMany(r => r.Samples)
                .WithOne()
                .HasForeignKey(s => s.ChaosRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<ChaosSample>(b =>
        {
            b.ToTable("chaos_samples");
            b.HasKey(s => s.Id);
            b.Property(s => s.FinalBalance).HasColumnType("numeric(20,4)");
            b.Property(s => s.MaxDrawdown).HasColumnType("numeric(20,4)");
            b.HasIndex(s => s.ChaosRunId);
        });

        // ── Workstream E: live sessions ───────────────────────────────────────────

        mb.Entity<LiveSession>(b =>
        {
            b.ToTable("live_sessions");
            b.HasKey(s => s.Id);
            b.Property(s => s.Symbol).HasMaxLength(20).IsRequired();
            b.Property(s => s.Interval).HasMaxLength(10).IsRequired();
            b.Property(s => s.Venue).HasMaxLength(60).IsRequired();
            b.Property(s => s.Mode).HasMaxLength(10).IsRequired();
            b.Property(s => s.ConfigHash).HasMaxLength(64).IsRequired();
            b.Property(s => s.StrategyId).HasMaxLength(32).IsRequired();
            b.Property(s => s.InitialBalance).HasColumnType("numeric(20,4)");
            b.Property(s => s.InitialBetSize).HasColumnType("numeric(20,4)");
            b.Property(s => s.CurrentBalance).HasColumnType("numeric(20,4)");
            b.Property(s => s.CurrentBetSize).HasColumnType("numeric(20,4)");
            b.Property(s => s.PeakBorrowed).HasColumnType("numeric(20,4)");
            b.Property(s => s.ReservedAmount).HasColumnType("numeric(20,4)");
            // Partial unique index on config_hash: only one active session with the same hash at a time
            // (paper OR live — both count). Stopped sessions may reuse the same config.
            b.HasIndex(s => s.ConfigHash)
                .IsUnique()
                .HasFilter("\"StoppedAt\" IS NULL");
            b.HasIndex(s => new { s.TenantId, s.StartedAt });
            b.HasMany(s => s.Bets)
                .WithOne()
                .HasForeignKey(x => x.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<LiveBet>(b =>
        {
            b.ToTable("live_bets");
            b.HasKey(x => x.Id);
            b.Property(x => x.Side).HasMaxLength(4).IsRequired();
            b.Property(x => x.Outcome).HasMaxLength(8);
            b.Property(x => x.PredictedProbUp).HasColumnType("numeric(6,5)");
            b.Property(x => x.AnchorClose).HasColumnType("numeric(20,8)");
            b.Property(x => x.Size).HasColumnType("numeric(20,4)");
            b.Property(x => x.BalanceBefore).HasColumnType("numeric(20,4)");
            b.Property(x => x.Payout).HasColumnType("numeric(20,4)");
            b.Property(x => x.BalanceAfter).HasColumnType("numeric(20,4)");
            b.Property(x => x.EntryPrice).HasColumnType("numeric(8,5)");
            b.Property(x => x.Shares).HasColumnType("numeric(18,6)");
            b.Property(x => x.MarketExternalId).HasMaxLength(200);
            b.Property(x => x.ExternalOrderId).HasMaxLength(200);
            b.Property(x => x.DivergenceNote).HasMaxLength(2000);
            b.Property(x => x.NotesJson).HasColumnType("jsonb");
            // Idempotency: never two bets on the same candle within a session.
            b.HasIndex(x => new { x.SessionId, x.TargetOpenTime }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.SessionId, x.Resolved });
        });

        // ── Workstream E: account reservation ledger (append-only) ───────────────

        mb.Entity<AccountLedgerEntry>(b =>
        {
            b.ToTable("account_ledger");
            b.HasKey(x => x.Id);
            b.Property(x => x.Venue).HasMaxLength(60).IsRequired();
            b.Property(x => x.EntryKind).HasMaxLength(20).IsRequired();
            b.Property(x => x.Amount).HasColumnType("numeric(20,6)");
            b.Property(x => x.WalletPusd).HasColumnType("numeric(20,6)");
            b.Property(x => x.FreeAfter).HasColumnType("numeric(20,6)");
            b.Property(x => x.Drift).HasColumnType("numeric(20,6)");
            b.Property(x => x.Note).HasColumnType("jsonb");
            b.HasIndex(x => new { x.TenantId, x.Venue, x.CreatedAt });
        });
    }
}

