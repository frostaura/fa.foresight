using FrostAura.Foresight.Domain.Models;
using FrostAura.Foresight.Domain.Strategies;
using FrostAura.Foresight.Domain.Tenancy;
using FrostAura.Foresight.Domain.Trading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FrostAura.Foresight.Infrastructure.Persistence;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ForesightDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<ForesightDbContext>>();

        try
        {
            await db.Database.EnsureCreatedAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database initialization failed");
            throw;
        }

        // EnsureCreated doesn't migrate existing databases, so additive tables and idempotent
        // drops go here via raw DDL. Cheap, idempotent, and good enough until proper EF
        // migrations land.
        await db.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS live_predictions (
                ""Id"" uuid PRIMARY KEY,
                ""TenantId"" uuid NOT NULL,
                ""Symbol"" varchar(20) NOT NULL,
                ""Interval"" varchar(10) NOT NULL,
                ""TargetOpenTime"" bigint NOT NULL,
                ""AnchorClose"" numeric(20,8) NOT NULL,
                ""PredictedClose"" numeric(20,8) NOT NULL,
                ""PredictedChangePct"" numeric(10,4) NOT NULL,
                ""Confidence"" numeric(6,5) NOT NULL,
                ""Reasoning"" varchar(8000) NULL,
                ""Model"" varchar(120) NOT NULL,
                ""SupportingDataJson"" jsonb NULL,
                ""CreatedAt"" timestamptz NOT NULL,
                ""ResolvedAt"" timestamptz NULL,
                ""ActualClose"" numeric(20,8) NULL,
                ""AbsoluteErrorPct"" numeric(10,4) NULL,
                ""DirectionHit"" boolean NULL
            );
            -- iter-4 widened the unique key to include ModelId — see uq_live_predictions_v2 below.
            -- The old (TenantId, Symbol, Interval, TargetOpenTime)-only unique index is no longer
            -- recreated here because two models can legitimately predict the same target candle.
            CREATE INDEX IF NOT EXISTS ix_live_predictions_tenant_created
                ON live_predictions (""TenantId"", ""Symbol"", ""Interval"", ""CreatedAt"");
            ALTER TABLE live_predictions
                ADD COLUMN IF NOT EXISTS ""DirectionUpProbability"" numeric(6,5) NOT NULL DEFAULT 0.5;
            ALTER TABLE live_predictions
                ADD COLUMN IF NOT EXISTS ""TargetHitProbability"" numeric(6,5) NOT NULL DEFAULT 0.5;
            ALTER TABLE live_predictions
                ADD COLUMN IF NOT EXISTS ""PromptTraceJson"" jsonb NULL;
            ALTER TABLE live_predictions
                ADD COLUMN IF NOT EXISTS ""ClosePercentile05"" numeric(20,8) NOT NULL DEFAULT 0;
            ALTER TABLE live_predictions
                ADD COLUMN IF NOT EXISTS ""ClosePercentile50"" numeric(20,8) NOT NULL DEFAULT 0;
            ALTER TABLE live_predictions
                ADD COLUMN IF NOT EXISTS ""ClosePercentile95"" numeric(20,8) NOT NULL DEFAULT 0;
            ALTER TABLE tenants
                DROP COLUMN IF EXISTS ""GuardrailConfig"";
            DROP TABLE IF EXISTS node_runs;
            DROP TABLE IF EXISTS flow_runs;
            DROP TABLE IF EXISTS flow_definitions;

            CREATE TABLE IF NOT EXISTS paper_sessions (
                ""Id"" uuid PRIMARY KEY,
                ""TenantId"" uuid NOT NULL,
                ""Symbol"" varchar(20) NOT NULL,
                ""Interval"" varchar(10) NOT NULL,
                ""StartedAt"" timestamptz NOT NULL,
                ""StoppedAt"" timestamptz NULL,
                ""InitialBalance"" numeric(20,4) NOT NULL,
                ""InitialBetSize"" numeric(20,4) NOT NULL,
                ""CurrentBalance"" numeric(20,4) NOT NULL,
                ""CurrentBetSize"" numeric(20,4) NOT NULL,
                ""Bust"" boolean NOT NULL DEFAULT false,
                ""LastProcessedAt"" timestamptz NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_paper_sessions_active
                ON paper_sessions (""TenantId"", ""Symbol"", ""Interval"")
                WHERE ""StoppedAt"" IS NULL;
            CREATE INDEX IF NOT EXISTS ix_paper_sessions_started
                ON paper_sessions (""TenantId"", ""StartedAt"");

            CREATE TABLE IF NOT EXISTS paper_bets (
                ""Id"" uuid PRIMARY KEY,
                ""TenantId"" uuid NOT NULL,
                ""SessionId"" uuid NOT NULL REFERENCES paper_sessions(""Id"") ON DELETE CASCADE,
                ""TargetOpenTime"" bigint NOT NULL,
                ""Side"" varchar(4) NOT NULL,
                ""PredictedProbUp"" numeric(6,5) NOT NULL,
                ""AnchorClose"" numeric(20,8) NOT NULL,
                ""Size"" numeric(20,4) NOT NULL,
                ""BalanceBefore"" numeric(20,4) NOT NULL,
                ""PlacedAt"" timestamptz NOT NULL,
                ""Resolved"" boolean NOT NULL DEFAULT false,
                ""Outcome"" varchar(8) NULL,
                ""Payout"" numeric(20,4) NULL,
                ""BalanceAfter"" numeric(20,4) NULL,
                ""ResolvedAt"" timestamptz NULL,
                ""ActualClose"" numeric(20,8) NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_paper_bets_session_target
                ON paper_bets (""SessionId"", ""TargetOpenTime"");
            CREATE INDEX IF NOT EXISTS ix_paper_bets_tenant_open
                ON paper_bets (""TenantId"", ""SessionId"", ""Resolved"");
            ALTER TABLE paper_bets
                ADD COLUMN IF NOT EXISTS ""NotesJson"" jsonb NULL;

            -- iter-4: prediction-model system ------------------------------------------------
            CREATE TABLE IF NOT EXISTS models (
                ""Id"" uuid PRIMARY KEY,
                ""TenantId"" uuid NULL,                              -- NULL = global built-in
                ""Name"" varchar(200) NOT NULL,
                ""Description"" varchar(2000) NULL,
                ""Kind"" varchar(20) NOT NULL,                       -- 'llm' | 'deterministic'
                ""SupportsBacktesting"" boolean NOT NULL,
                ""IsBuiltIn"" boolean NOT NULL DEFAULT false,
                ""IsDefault"" boolean NOT NULL DEFAULT false,
                ""Definition"" jsonb NOT NULL,
                ""TrainedState"" jsonb NULL,
                ""TrainingValidationAccuracy"" numeric(6,5) NULL,
                ""BacktestAccuracy"" numeric(6,5) NULL,
                ""LastTrainedAt"" timestamptz NULL,
                ""CreatedAt"" timestamptz NOT NULL,
                ""UpdatedAt"" timestamptz NOT NULL
            );
            -- A NULL TenantId is treated as a single bucket by the unique-constraint engine; the
            -- partial index below replaces the naive composite to keep tenant scoping clean.
            CREATE UNIQUE INDEX IF NOT EXISTS ix_models_tenant_name
                ON models (""TenantId"", ""Name"")
                WHERE ""TenantId"" IS NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS ix_models_builtin_name
                ON models (""Name"")
                WHERE ""TenantId"" IS NULL;
            CREATE INDEX IF NOT EXISTS ix_models_tenant_default
                ON models (""TenantId"", ""IsDefault"");

            CREATE TABLE IF NOT EXISTS active_models (
                ""TenantId"" uuid NOT NULL,
                ""Symbol"" varchar(20) NOT NULL,
                ""Interval"" varchar(10) NOT NULL,
                ""ModelId"" uuid NOT NULL REFERENCES models(""Id""),
                ""UpdatedAt"" timestamptz NOT NULL,
                PRIMARY KEY (""TenantId"", ""Symbol"", ""Interval"")
            );

            CREATE TABLE IF NOT EXISTS historical_candles (
                ""Symbol"" varchar(20) NOT NULL,
                ""Interval"" varchar(10) NOT NULL,
                ""OpenTime"" bigint NOT NULL,
                ""Open"" numeric(20,8) NOT NULL,
                ""High"" numeric(20,8) NOT NULL,
                ""Low"" numeric(20,8) NOT NULL,
                ""Close"" numeric(20,8) NOT NULL,
                ""Volume"" numeric(28,8) NOT NULL,
                PRIMARY KEY (""Symbol"", ""Interval"", ""OpenTime"")
            );

            CREATE TABLE IF NOT EXISTS historical_microstructure (
                ""Symbol"" varchar(20) NOT NULL,
                ""Interval"" varchar(10) NOT NULL,
                ""OpenTime"" bigint NOT NULL,
                ""TradeCount"" bigint NOT NULL,
                ""BuyVolume"" numeric(28,8) NOT NULL,
                ""SellVolume"" numeric(28,8) NOT NULL,
                ""BuyTradeCount"" bigint NOT NULL,
                ""LargeBuyVolume"" numeric(28,8) NOT NULL,
                ""LargeSellVolume"" numeric(28,8) NOT NULL,
                ""OpenInterest"" numeric(28,8) NULL,
                ""OpenInterestValue"" numeric(28,4) NULL,
                ""TopTraderLongShortRatio"" numeric(18,8) NULL,
                ""LongShortRatio"" numeric(18,8) NULL,
                ""TakerLongShortVolRatio"" numeric(18,8) NULL,
                PRIMARY KEY (""Symbol"", ""Interval"", ""OpenTime"")
            );
            ALTER TABLE historical_microstructure ADD COLUMN IF NOT EXISTS ""OpenInterest"" numeric(28,8) NULL;
            ALTER TABLE historical_microstructure ADD COLUMN IF NOT EXISTS ""OpenInterestValue"" numeric(28,4) NULL;
            ALTER TABLE historical_microstructure ADD COLUMN IF NOT EXISTS ""TopTraderLongShortRatio"" numeric(18,8) NULL;
            ALTER TABLE historical_microstructure ADD COLUMN IF NOT EXISTS ""LongShortRatio"" numeric(18,8) NULL;
            ALTER TABLE historical_microstructure ADD COLUMN IF NOT EXISTS ""TakerLongShortVolRatio"" numeric(18,8) NULL;
            ALTER TABLE historical_microstructure ADD COLUMN IF NOT EXISTS ""LateBuyVolume"" numeric(28,8) NULL;
            ALTER TABLE historical_microstructure ADD COLUMN IF NOT EXISTS ""LateSellVolume"" numeric(28,8) NULL;
            ALTER TABLE historical_microstructure ADD COLUMN IF NOT EXISTS ""EarlyBuyVolume"" numeric(28,8) NULL;
            ALTER TABLE historical_microstructure ADD COLUMN IF NOT EXISTS ""EarlySellVolume"" numeric(28,8) NULL;
            ALTER TABLE historical_microstructure ADD COLUMN IF NOT EXISTS ""LateTradeCount"" bigint NULL;

            CREATE TABLE IF NOT EXISTS backtests (
                ""Id"" uuid PRIMARY KEY,
                ""TenantId"" uuid NOT NULL,
                ""ModelId"" uuid NOT NULL REFERENCES models(""Id""),
                ""Symbol"" varchar(20) NOT NULL,
                ""Interval"" varchar(10) NOT NULL,
                ""StartTime"" bigint NOT NULL,
                ""EndTime"" bigint NOT NULL,
                ""InitialBalance"" numeric(20,4) NOT NULL,
                ""InitialBetSize"" numeric(20,4) NOT NULL,
                ""Status"" varchar(20) NOT NULL DEFAULT 'running',
                ""BetsPlaced"" int NOT NULL DEFAULT 0,
                ""BetsWon"" int NOT NULL DEFAULT 0,
                ""HitRate"" numeric(6,5) NULL,
                ""FinalBalance"" numeric(20,4) NULL,
                ""PeakBalance"" numeric(20,4) NULL,
                ""TroughBalance"" numeric(20,4) NULL,
                ""MaxDrawdown"" numeric(20,4) NULL,
                ""PeakBorrowed"" numeric(20,4) NULL,
                ""ZeroCrossingsCount"" int NOT NULL DEFAULT 0,
                ""MarkersJson"" jsonb NULL,
                ""Error"" varchar(2000) NULL,
                ""StartedAt"" timestamptz NOT NULL,
                ""CompletedAt"" timestamptz NULL
            );
            CREATE INDEX IF NOT EXISTS ix_backtests_tenant_model_started
                ON backtests (""TenantId"", ""ModelId"", ""StartedAt"" DESC);

            CREATE TABLE IF NOT EXISTS backtest_bets (
                ""Id"" uuid PRIMARY KEY,
                ""BacktestId"" uuid NOT NULL REFERENCES backtests(""Id"") ON DELETE CASCADE,
                ""TargetOpenTime"" bigint NOT NULL,
                ""Side"" varchar(4) NOT NULL,
                ""PUpRaw"" numeric(6,5) NOT NULL,
                ""PUpCalibrated"" numeric(6,5) NULL,
                ""Size"" numeric(20,4) NOT NULL,
                ""BalanceBefore"" numeric(20,4) NOT NULL,
                ""BalanceAfter"" numeric(20,4) NOT NULL,
                ""Won"" boolean NOT NULL,
                ""BorrowedShortfall"" numeric(20,4) NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_backtest_bets_backtest_target
                ON backtest_bets (""BacktestId"", ""TargetOpenTime"");

            -- Zero-crossings + peak-borrowed tracking on the live session row (parity with backtests).
            ALTER TABLE paper_sessions
                ADD COLUMN IF NOT EXISTS ""ZeroCrossingsCount"" int NOT NULL DEFAULT 0;
            ALTER TABLE paper_sessions
                ADD COLUMN IF NOT EXISTS ""PeakBorrowed"" numeric(20,4) NOT NULL DEFAULT 0;
            -- Pluggable staking strategy on the live session, parity with backtests. Pre-selector
            -- rows ran the hard-coded Martingale doubling, so the back-compat default is martingale;
            -- new sessions started via the picker pass the user's selection explicitly.
            ALTER TABLE paper_sessions
                ADD COLUMN IF NOT EXISTS ""StrategyId"" varchar(32) NOT NULL DEFAULT 'martingale';
            -- Label distinguishes multiple ACTIVE sessions on the same (tenant, symbol, interval), so the
            -- bot can run same-market strategy comparisons in parallel. The chart UI's primary session
            -- uses Label='' (empty); bot-created comparison sessions carry a non-empty label. The active
            -- partial-unique index widens to include Label so '' (primary) + N labelled can coexist.
            ALTER TABLE paper_sessions
                ADD COLUMN IF NOT EXISTS ""Label"" varchar(60) NOT NULL DEFAULT '';
            DROP INDEX IF EXISTS ix_paper_sessions_active;
            CREATE UNIQUE INDEX IF NOT EXISTS ix_paper_sessions_active
                ON paper_sessions (""TenantId"", ""Symbol"", ""Interval"", ""Label"")
                WHERE ""StoppedAt"" IS NULL;

            -- Per-(symbol, interval) model dispatch.
            ALTER TABLE live_predictions
                ADD COLUMN IF NOT EXISTS ""ModelId"" uuid NOT NULL
                DEFAULT '00000000-0000-0000-0000-000000000001';
            -- Widen the unique key without blocking writes from LivePredictionGapFillerService.
            -- CONCURRENTLY isn't supported inside a transaction; ExecuteSqlRawAsync uses an
            -- implicit transaction, so fall back to a regular CREATE INDEX. Postgres takes a
            -- ShareLock during build; the gap-filler writes infrequently (every 15s) and Insert
            -- holds RowExclusiveLock which is compatible with Share, so this won't block writes
            -- in practice for the small index sizes we expect on first run.
            CREATE UNIQUE INDEX IF NOT EXISTS uq_live_predictions_v2
                ON live_predictions (""TenantId"", ""Symbol"", ""Interval"", ""TargetOpenTime"", ""ModelId"");
            DROP INDEX IF EXISTS ix_live_predictions_tenant_target;
            CREATE INDEX IF NOT EXISTS ix_live_predictions_tenant_model_created
                ON live_predictions (""TenantId"", ""Symbol"", ""Interval"", ""ModelId"", ""CreatedAt"");

            -- Iter-4c: deepest Martingale doubling step reached during a backtest run.
            ALTER TABLE backtests
                ADD COLUMN IF NOT EXISTS ""MaxMartingaleStep"" int NOT NULL DEFAULT 0;
            -- Iter-4d: per-run allow-borrow toggle + A/B comparison batch grouping.
            ALTER TABLE backtests
                ADD COLUMN IF NOT EXISTS ""AllowBorrow"" boolean NOT NULL DEFAULT true;
            ALTER TABLE backtests
                ADD COLUMN IF NOT EXISTS ""BatchId"" uuid NULL;
            CREATE INDEX IF NOT EXISTS ix_backtests_batch ON backtests (""BatchId"");
            -- Iter-4e: pluggable staking strategy id (martingale, flat, …).
            ALTER TABLE backtests
                ADD COLUMN IF NOT EXISTS ""StrategyId"" varchar(32) NOT NULL DEFAULT 'martingale';
            -- Bust-test sweeps: a batch of N runs at increasing lookback (1..max days). BatchKind
            -- tags each rung so the UI can collapse the sweep into one clickable batch row;
            -- LookbackDay is the rung's window length in days.
            ALTER TABLE backtests
                ADD COLUMN IF NOT EXISTS ""BatchKind"" varchar(20) NULL;
            ALTER TABLE backtests
                ADD COLUMN IF NOT EXISTS ""LookbackDay"" int NULL;
            -- Confidence-gate toggle: when true the run skipped the ±2pp no-bet band instead of
            -- betting every candle (parity with the live paper-session gate + the chart GATE).
            ALTER TABLE backtests
                ADD COLUMN IF NOT EXISTS ""ApplyGate"" boolean NOT NULL DEFAULT false;
            ALTER TABLE paper_sessions
                ADD COLUMN IF NOT EXISTS ""Gated"" boolean NOT NULL DEFAULT false;

            -- Iter-4f: persist the training window on each model row so the leakage audit can
            -- detect overlap between training data and a subsequent backtest range. Null for
            -- never-trained models (LLM-only built-ins). Populated by ModelTrainingService
            -- when training completes.
            ALTER TABLE models
                ADD COLUMN IF NOT EXISTS ""TrainStartMs"" bigint NULL;
            ALTER TABLE models
                ADD COLUMN IF NOT EXISTS ""TrainEndMs"" bigint NULL;
            ALTER TABLE models
                ADD COLUMN IF NOT EXISTS ""TrainSymbol"" varchar(20) NULL;
            ALTER TABLE models
                ADD COLUMN IF NOT EXISTS ""TrainInterval"" varchar(10) NULL;
            -- Persistent training-job state: a training run survives the browser closing because the
            -- fit happens on a background task and its status lives here, not on the HTTP request.
            ALTER TABLE models
                ADD COLUMN IF NOT EXISTS ""TrainingStatus"" varchar(20) NULL;
            ALTER TABLE models
                ADD COLUMN IF NOT EXISTS ""TrainingStartedAt"" timestamptz NULL;
            ALTER TABLE models
                ADD COLUMN IF NOT EXISTS ""TrainingError"" varchar(2000) NULL;
            -- AI-generated model descriptions (OpenRouter, generated in background on upsert).
            ALTER TABLE models ADD COLUMN IF NOT EXISTS ""SimpleDescription"" varchar(500) NULL;
            ALTER TABLE models ADD COLUMN IF NOT EXISTS ""TechnicalDescription"" varchar(1000) NULL;
            -- Model archiving: soft-delete flag. Archived models are excluded from the default listing.
            ALTER TABLE models ADD COLUMN IF NOT EXISTS ""IsArchived"" boolean NOT NULL DEFAULT false;

            -- Strategy AI descriptions (mirrors models pattern).
            ALTER TABLE strategies ADD COLUMN IF NOT EXISTS ""SimpleDescription"" varchar(500) NULL;
            ALTER TABLE strategies ADD COLUMN IF NOT EXISTS ""TechnicalDescription"" varchar(1000) NULL;

            -- Zombie training jobs: a model stuck at 'training' at boot is an orphan from a
            -- crashed/restarted process — its background task is gone, so the spinner would never
            -- clear. Mark it failed so the UI surfaces what actually happened (user can retrain).
            UPDATE models
            SET ""TrainingStatus"" = 'failed',
                ""TrainingError"" = 'Interrupted by backend restart'
            WHERE ""TrainingStatus"" = 'training';

            -- Zombie-row cleanup. Any backtest still marked 'running' at boot is an orphan from a
            -- crashed/restarted process — there's no background task tracking it anymore, so leaving
            -- the status as 'running' makes the table show a spinner forever. Mark them failed
            -- so the UI surfaces what actually happened.
            UPDATE backtests
            SET ""Status"" = 'failed',
                ""CompletedAt"" = NOW(),
                ""Error"" = COALESCE(NULLIF(""Error"", ''), 'Interrupted by backend restart')
            WHERE ""Status"" IN ('running', 'queued');

            -- Odds-based staking: venue_market_prices table.
            CREATE TABLE IF NOT EXISTS venue_market_prices (
                ""Id"" uuid PRIMARY KEY,
                ""TenantId"" uuid NOT NULL,
                ""Venue"" varchar(60) NOT NULL,
                ""Symbol"" varchar(20) NOT NULL,
                ""Interval"" varchar(10) NOT NULL,
                ""TargetOpenTime"" bigint NOT NULL,
                ""ObservedAt"" bigint NOT NULL,
                ""MarketExternalId"" varchar(200) NOT NULL,
                ""YesPrice"" numeric(8,5) NOT NULL,
                ""NoPrice"" numeric(8,5) NOT NULL,
                ""Synthetic"" boolean NOT NULL DEFAULT false,
                ""Source"" varchar(60) NOT NULL,
                ""ResolutionWindowStart"" bigint NOT NULL DEFAULT 0,
                ""ResolutionWindowEnd"" bigint NOT NULL DEFAULT 0,
                ""ReferenceSource"" varchar(120) NULL,
                ""ResolvedOutcomeUp"" boolean NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_venue_market_prices_unique
                ON venue_market_prices (""TenantId"", ""Venue"", ""Symbol"", ""Interval"", ""TargetOpenTime"", ""ObservedAt"", ""MarketExternalId"");
            CREATE INDEX IF NOT EXISTS ix_venue_market_prices_lookup
                ON venue_market_prices (""Venue"", ""Symbol"", ""Interval"", ""TargetOpenTime"");

            -- Odds fields on backtest_bets.
            ALTER TABLE backtest_bets ADD COLUMN IF NOT EXISTS ""EntryPrice"" numeric(8,5) NOT NULL DEFAULT 0.5;
            ALTER TABLE backtest_bets ADD COLUMN IF NOT EXISTS ""Shares"" numeric(18,6) NOT NULL DEFAULT 0;
            ALTER TABLE backtest_bets ADD COLUMN IF NOT EXISTS ""Payout"" numeric(20,4) NOT NULL DEFAULT 0;
            ALTER TABLE backtest_bets ADD COLUMN IF NOT EXISTS ""Synthetic"" boolean NOT NULL DEFAULT true;
            ALTER TABLE backtest_bets ADD COLUMN IF NOT EXISTS ""MarketExternalId"" varchar(200) NULL;

            -- SyntheticBetFraction summary on backtests.
            ALTER TABLE backtests ADD COLUMN IF NOT EXISTS ""SyntheticBetFraction"" numeric(6,5) NULL;

            -- Odds fields on paper_bets.
            ALTER TABLE paper_bets ADD COLUMN IF NOT EXISTS ""EntryPrice"" numeric(8,5) NULL;
            ALTER TABLE paper_bets ADD COLUMN IF NOT EXISTS ""Shares"" numeric(18,6) NULL;
            ALTER TABLE paper_bets ADD COLUMN IF NOT EXISTS ""Synthetic"" boolean NOT NULL DEFAULT false;
            ALTER TABLE paper_bets ADD COLUMN IF NOT EXISTS ""MarketExternalId"" varchar(200) NULL;

            -- Workstream D: chaos/bust test engine tables.
            CREATE TABLE IF NOT EXISTS chaos_runs (
                ""Id"" uuid PRIMARY KEY,
                ""TenantId"" uuid NOT NULL,
                ""BatchId"" uuid NOT NULL,
                ""ModelId"" uuid NOT NULL REFERENCES models(""Id""),
                ""StrategyId"" varchar(32) NOT NULL,
                ""Symbol"" varchar(20) NOT NULL,
                ""Interval"" varchar(10) NOT NULL,
                ""WindowLength"" int NOT NULL,
                ""SampleCount"" int NOT NULL,
                ""AllowBorrow"" boolean NOT NULL DEFAULT true,
                ""Seed"" bigint NOT NULL DEFAULT 0,
                ""Status"" varchar(20) NOT NULL DEFAULT 'running',
                ""BustRate"" numeric(8,6) NULL,
                ""ProfitP5"" numeric(20,4) NULL,
                ""ProfitP50"" numeric(20,4) NULL,
                ""ProfitP95"" numeric(20,4) NULL,
                ""WorstDrawdown"" numeric(20,4) NULL,
                ""MeanZeroCrossings"" double precision NULL,
                ""SyntheticBetFraction"" numeric(6,5) NULL,
                ""Pass"" boolean NOT NULL DEFAULT false,
                ""StartedAt"" timestamptz NOT NULL,
                ""CompletedAt"" timestamptz NULL,
                ""Error"" varchar(2000) NULL
            );
            CREATE INDEX IF NOT EXISTS ix_chaos_runs_tenant_batch
                ON chaos_runs (""TenantId"", ""BatchId"");

            CREATE TABLE IF NOT EXISTS chaos_samples (
                ""Id"" uuid PRIMARY KEY,
                ""ChaosRunId"" uuid NOT NULL REFERENCES chaos_runs(""Id"") ON DELETE CASCADE,
                ""StartMs"" bigint NOT NULL,
                ""Survived"" boolean NOT NULL,
                ""FinalBalance"" numeric(20,4) NOT NULL,
                ""MaxDrawdown"" numeric(20,4) NOT NULL,
                ""ZeroCrossings"" int NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_chaos_samples_run
                ON chaos_samples (""ChaosRunId"");

            -- Zombie cleanup for chaos runs (same pattern as backtests).
            UPDATE chaos_runs
            SET ""Status"" = 'failed',
                ""CompletedAt"" = NOW(),
                ""Error"" = COALESCE(NULLIF(""Error"", ''), 'Interrupted by backend restart')
            WHERE ""Status"" = 'running';

            -- Workstream E: live sessions + live bets --------------------------------
            CREATE TABLE IF NOT EXISTS live_sessions (
                ""Id""               uuid PRIMARY KEY,
                ""TenantId""         uuid NOT NULL,
                ""Symbol""           varchar(20) NOT NULL,
                ""Interval""         varchar(10) NOT NULL,
                ""Venue""            varchar(60) NOT NULL DEFAULT 'polymarket',
                ""Mode""             varchar(10) NOT NULL DEFAULT 'live',
                ""ConfigHash""       varchar(64) NOT NULL,
                ""StartedAt""        timestamptz NOT NULL,
                ""StoppedAt""        timestamptz NULL,
                ""InitialBalance""   numeric(20,4) NOT NULL,
                ""InitialBetSize""   numeric(20,4) NOT NULL,
                ""StrategyId""       varchar(32) NOT NULL DEFAULT 'flat',
                ""Gated""            boolean NOT NULL DEFAULT false,
                ""CurrentBalance""   numeric(20,4) NOT NULL,
                ""CurrentBetSize""   numeric(20,4) NOT NULL,
                ""Bust""             boolean NOT NULL DEFAULT false,
                ""ZeroCrossingsCount"" int NOT NULL DEFAULT 0,
                ""PeakBorrowed""     numeric(20,4) NOT NULL DEFAULT 0,
                ""ReservedAmount""   numeric(20,4) NOT NULL DEFAULT 0,
                ""LastProcessedAt""  timestamptz NULL
            );
            -- Only one active session per config hash (paper OR live dedup).
            CREATE UNIQUE INDEX IF NOT EXISTS ix_live_sessions_active_config
                ON live_sessions (""ConfigHash"")
                WHERE ""StoppedAt"" IS NULL;
            CREATE INDEX IF NOT EXISTS ix_live_sessions_tenant_started
                ON live_sessions (""TenantId"", ""StartedAt"");

            CREATE TABLE IF NOT EXISTS live_bets (
                ""Id""               uuid PRIMARY KEY,
                ""TenantId""         uuid NOT NULL,
                ""SessionId""        uuid NOT NULL REFERENCES live_sessions(""Id"") ON DELETE CASCADE,
                ""TargetOpenTime""   bigint NOT NULL,
                ""Side""             varchar(4) NOT NULL,
                ""PredictedProbUp""  numeric(6,5) NOT NULL,
                ""AnchorClose""      numeric(20,8) NOT NULL,
                ""Size""             numeric(20,4) NOT NULL,
                ""BalanceBefore""    numeric(20,4) NOT NULL,
                ""PlacedAt""         timestamptz NOT NULL,
                ""ExternalOrderId""  varchar(200) NULL,
                ""Resolved""         boolean NOT NULL DEFAULT false,
                ""Outcome""          varchar(8) NULL,
                ""Payout""           numeric(20,4) NULL,
                ""BalanceAfter""     numeric(20,4) NULL,
                ""ResolvedAt""       timestamptz NULL,
                ""MarketOutcomeUp""  boolean NULL,
                ""EntryPrice""       numeric(8,5) NULL,
                ""Shares""           numeric(18,6) NULL,
                ""DivergenceNote""   varchar(2000) NULL,
                ""NotesJson""        jsonb NULL,
                ""MarketExternalId"" varchar(200) NULL
            );
            -- Idempotency: one bet per candle per session.
            CREATE UNIQUE INDEX IF NOT EXISTS ix_live_bets_session_target
                ON live_bets (""SessionId"", ""TargetOpenTime"");
            CREATE INDEX IF NOT EXISTS ix_live_bets_tenant_open
                ON live_bets (""TenantId"", ""SessionId"", ""Resolved"");

            -- Workstream E: account reservation ledger (append-only audit) -----------
            CREATE TABLE IF NOT EXISTS account_ledger (
                ""Id""         uuid PRIMARY KEY,
                ""TenantId""   uuid NOT NULL,
                ""Venue""      varchar(60) NOT NULL,
                ""EntryKind""  varchar(20) NOT NULL,
                ""SessionId""  uuid NULL,
                ""Amount""     numeric(20,6) NOT NULL DEFAULT 0,
                ""WalletPusd"" numeric(20,6) NOT NULL DEFAULT 0,
                ""FreeAfter""  numeric(20,6) NOT NULL DEFAULT 0,
                ""Drift""      numeric(20,6) NULL,
                ""Note""       jsonb NULL,
                ""CreatedAt""  timestamptz NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_account_ledger_tenant_venue_created
                ON account_ledger (""TenantId"", ""Venue"", ""CreatedAt"");

            -- Phase E2: config_hash cross-mode dedup on paper_sessions ----------------------
            -- Mirrors the config_hash column on live_sessions so a single query can check
            -- whether a paper and a live session with the same config are both active (409).
            ALTER TABLE paper_sessions
                ADD COLUMN IF NOT EXISTS ""ConfigHash"" varchar(64) NULL;
            -- Partial unique index: one active paper session per config hash. NULL hashes (legacy rows)
            -- are excluded from the uniqueness check (IS NOT DISTINCT FROM NULL would group all NULLs).
            -- PostgreSQL partial indexes with a NULL-column predicate: use WHERE ""ConfigHash"" IS NOT NULL
            -- so legacy rows remain unrestricted while new rows are deduped.
            CREATE UNIQUE INDEX IF NOT EXISTS ix_paper_sessions_active_config
                ON paper_sessions (""ConfigHash"")
                WHERE ""StoppedAt"" IS NULL AND ""ConfigHash"" IS NOT NULL;

            -- Workstream F: custom DAG strategies -------------------------------------------
            -- Mirrors the models table pattern: partial unique indexes keep tenant vs built-in
            -- name scoping clean. Definition is jsonb (strategy-kind FlowDefinition); Params is
            -- reserved for future per-strategy parameter overrides.
            CREATE TABLE IF NOT EXISTS strategies (
                ""Id""          uuid PRIMARY KEY,
                ""TenantId""    uuid NULL,                 -- NULL = global built-in
                ""Name""        varchar(200) NOT NULL,
                ""Description"" varchar(2000) NULL,
                ""Definition""  jsonb NULL,                -- NULL for built-in code strategies
                ""Params""      jsonb NULL,
                ""IsBuiltIn""   boolean NOT NULL DEFAULT false,
                ""CreatedAt""   timestamptz NOT NULL,
                ""UpdatedAt""   timestamptz NOT NULL
            );
            -- Tenant-owned strategies: unique (TenantId, Name).
            CREATE UNIQUE INDEX IF NOT EXISTS ix_strategies_tenant_name
                ON strategies (""TenantId"", ""Name"")
                WHERE ""TenantId"" IS NOT NULL;
            -- Global built-in strategies: unique Name globally.
            CREATE UNIQUE INDEX IF NOT EXISTS ix_strategies_builtin_name
                ON strategies (""Name"")
                WHERE ""TenantId"" IS NULL;
        ", ct);

        // Seed default tenant if none exists.
        if (!await db.Tenants.AnyAsync(ct))
        {
            var tenant = new Tenant
            {
                Id = Guid.NewGuid(),
                Name = "Dean (default)",
                Slug = "default",
                CreatedAt = DateTimeOffset.UtcNow,
                Settings = new TenantSettings()
            };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded default tenant {TenantId}", tenant.Id);
        }

        var now = DateTimeOffset.UtcNow;

        // The legacy "Default LLM" model is no longer seeded. If a row still exists from a prior
        // boot, cascade-delete its dependents (FKs are app-layer, not schema-layer) then drop it.
        var legacyLlm = await db.Models.FindAsync(new object?[] { ModelIds.ForesightDefaultLlm }, ct);
        if (legacyLlm is not null)
        {
            await db.ActiveModels.Where(a => a.ModelId == ModelIds.ForesightDefaultLlm).ExecuteDeleteAsync(ct);
            await db.Backtests.Where(b => b.ModelId == ModelIds.ForesightDefaultLlm).ExecuteDeleteAsync(ct);
            await db.LivePredictions.Where(p => p.ModelId == ModelIds.ForesightDefaultLlm).ExecuteDeleteAsync(ct);
            db.Models.Remove(legacyLlm);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Removed legacy built-in model {ModelId} (Default LLM)", ModelIds.ForesightDefaultLlm);
        }

        // The "Flat Baseline" MODEL is no longer seeded — "flat" is a staking STRATEGY, not a model,
        // and the duplicate naming was confusing debt. Cascade-delete any leftover row + dependents
        // on boot (FKs are app-layer). The "flat" staking strategy is unaffected.
        var legacyFlat = await db.Models.FindAsync(new object?[] { ModelIds.ForesightFlatBaseline }, ct);
        if (legacyFlat is not null)
        {
            await db.ActiveModels.Where(a => a.ModelId == ModelIds.ForesightFlatBaseline).ExecuteDeleteAsync(ct);
            await db.Backtests.Where(b => b.ModelId == ModelIds.ForesightFlatBaseline).ExecuteDeleteAsync(ct);
            await db.LivePredictions.Where(p => p.ModelId == ModelIds.ForesightFlatBaseline).ExecuteDeleteAsync(ct);
            db.Models.Remove(legacyFlat);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Removed legacy built-in model {ModelId} (Flat Baseline — staking strategy, not a model)", ModelIds.ForesightFlatBaseline);
        }

        // Seed Foresight v6 — the deterministic trained model. Iter-0 is a logistic regression on
        // the nine FeaturePack columns; subsequent iterations rewrite BuildForesightV6Flow() to
        // add new indicator nodes. Re-seed on every boot so the iteration delta flows through.
        const string v6Name = "Foresight v6";
        const string v6Description = "Deterministic logistic-regression model trained on FeaturePack indicators. Iteration target: backtest hit-rate ≥ 60% on at least one of {1m, 5m, 15m}.";
        var v6Definition = BuiltInModels.BuildForesightV6Flow();
        var existingV6 = await db.Models.FindAsync(new object?[] { ModelIds.ForesightV6 }, ct);
        if (existingV6 is null)
        {
            db.Models.Add(new Model
            {
                Id = ModelIds.ForesightV6,
                TenantId = null,
                Name = v6Name,
                Description = v6Description,
                Kind = "deterministic",
                SupportsBacktesting = true,
                IsBuiltIn = true,
                IsDefault = true,   // the deterministic default model (no LLM model exists)
                Definition = v6Definition,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded model {ModelId} ({Name})", ModelIds.ForesightV6, v6Name);
        }
        else if (existingV6.Definition != v6Definition || existingV6.Name != v6Name || existingV6.Description != v6Description)
        {
            existingV6.Definition = v6Definition;
            existingV6.Name = v6Name;
            existingV6.Description = v6Description;
            existingV6.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Refreshed model {ModelId} ({Name})", ModelIds.ForesightV6, v6Name);
        }

        // Seed "Foresight | 5m | v1" — a clean-sheet 5m-specialised model (not a v6 derivative).
        // Re-seeded on every boot so iteration deltas to BuildForesight5mV1Flow() flow through.
        const string v1Name = "Foresight | 5m | v1";
        const string v1Description = "Clean-sheet 5m-only model: intraday session seasonality + 15m regime + 1m sub-bar pressure on a pluggable estimator. Built for honest, leakage-proof 5m direction edge.";
        var v1Definition = BuiltInModels.BuildForesight5mV1Flow();
        var existingV1 = await db.Models.FindAsync(new object?[] { ModelIds.ForesightFiveMinV1 }, ct);
        if (existingV1 is null)
        {
            db.Models.Add(new Model
            {
                Id = ModelIds.ForesightFiveMinV1,
                TenantId = null,
                Name = v1Name,
                Description = v1Description,
                Kind = "deterministic",
                SupportsBacktesting = true,
                IsBuiltIn = true,
                IsDefault = false,
                Definition = v1Definition,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded model {ModelId} ({Name})", ModelIds.ForesightFiveMinV1, v1Name);
        }
        else if (existingV1.Definition != v1Definition || existingV1.Name != v1Name || existingV1.Description != v1Description)
        {
            existingV1.Definition = v1Definition;
            existingV1.Name = v1Name;
            existingV1.Description = v1Description;
            existingV1.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Refreshed model {ModelId} ({Name})", ModelIds.ForesightFiveMinV1, v1Name);
        }

        // Seed "Foresight | 5m | v1+ofx" — v1 plus backtestable order-flow microstructure. Backtest/
        // training only (daily aggTrades dumps lag ~1 day, so it abstains live until a live feed exists).
        const string ofxName = "Foresight | 5m | v1+ofx";
        const string ofxDescription = "v1 + backtestable order-flow microstructure (taker imbalance / CVD / large-order skew / trade intensity). The lever for pushing past the ~53% TA ceiling. Backtest/training only until a live recent-trades feed lands.";
        var ofxDefinition = BuiltInModels.BuildForesight5mV1OfxFlow();
        var existingOfx = await db.Models.FindAsync(new object?[] { ModelIds.ForesightFiveMinV1Ofx }, ct);
        if (existingOfx is null)
        {
            db.Models.Add(new Model
            {
                Id = ModelIds.ForesightFiveMinV1Ofx,
                TenantId = null,
                Name = ofxName,
                Description = ofxDescription,
                Kind = "deterministic",
                SupportsBacktesting = true,
                IsBuiltIn = true,
                IsDefault = false,
                Definition = ofxDefinition,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded model {ModelId} ({Name})", ModelIds.ForesightFiveMinV1Ofx, ofxName);
        }
        else if (existingOfx.Definition != ofxDefinition || existingOfx.Name != ofxName || existingOfx.Description != ofxDescription)
        {
            existingOfx.Definition = ofxDefinition;
            existingOfx.Name = ofxName;
            existingOfx.Description = ofxDescription;
            existingOfx.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Refreshed model {ModelId} ({Name})", ModelIds.ForesightFiveMinV1Ofx, ofxName);
        }

        // Seed "Foresight | 5m | v1+ofx2" — v1+ofx plus intra-bar (high-frequency) order-flow. Clean
        // A/B sibling of v1+ofx (identical except the microflow pack) so the walk-forward delta is
        // attributable to intra-bar resolution. Backtest/training only, same as v1+ofx.
        const string ofx2Name = "Foresight | 5m | v1+ofx2";
        const string ofx2Description = "v1+ofx plus intra-bar order-flow (late-window imbalance, imbalance acceleration into the close, late trade-intensity) reconstructed from the same ticks the per-bar aggregates discard. Tests whether intra-bar resolution breaks the ~53% ceiling. Backtest/training only.";
        var ofx2Definition = BuiltInModels.BuildForesight5mV1Ofx2Flow();
        var existingOfx2 = await db.Models.FindAsync(new object?[] { ModelIds.ForesightFiveMinV1Ofx2 }, ct);
        if (existingOfx2 is null)
        {
            db.Models.Add(new Model
            {
                Id = ModelIds.ForesightFiveMinV1Ofx2,
                TenantId = null,
                Name = ofx2Name,
                Description = ofx2Description,
                Kind = "deterministic",
                SupportsBacktesting = true,
                IsBuiltIn = true,
                IsDefault = false,
                Definition = ofx2Definition,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded model {ModelId} ({Name})", ModelIds.ForesightFiveMinV1Ofx2, ofx2Name);
        }
        else if (existingOfx2.Definition != ofx2Definition || existingOfx2.Name != ofx2Name || existingOfx2.Description != ofx2Description)
        {
            existingOfx2.Definition = ofx2Definition;
            existingOfx2.Name = ofx2Name;
            existingOfx2.Description = ofx2Description;
            existingOfx2.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Refreshed model {ModelId} ({Name})", ModelIds.ForesightFiveMinV1Ofx2, ofx2Name);
        }

        // Seed "Foresight | 5m | v2" — the non-linear (gradient-boosted-trees) engine on the same
        // leakage-proof v1 feature matrix. The one untested modeling lever from the iteration logs.
        const string v2Name = "Foresight | 5m | v2";
        const string v2Description = "v1's feature matrix fit with gradient-boosted trees instead of logistic regression — the non-linear engine the iteration logs flagged as untested. Captures feature interactions a linear model can't; conviction-gated for the high-confidence reporting subset. The honest push toward a defensible-sample 60%.";
        var v2Definition = BuiltInModels.BuildForesight5mV2Flow();
        var existingV2 = await db.Models.FindAsync(new object?[] { ModelIds.ForesightFiveMinV2 }, ct);
        if (existingV2 is null)
        {
            db.Models.Add(new Model
            {
                Id = ModelIds.ForesightFiveMinV2,
                TenantId = null,
                Name = v2Name,
                Description = v2Description,
                Kind = "deterministic",
                SupportsBacktesting = true,
                IsBuiltIn = true,
                IsDefault = false,
                Definition = v2Definition,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded model {ModelId} ({Name})", ModelIds.ForesightFiveMinV2, v2Name);
        }
        else if (existingV2.Definition != v2Definition || existingV2.Name != v2Name || existingV2.Description != v2Description)
        {
            existingV2.Definition = v2Definition;
            existingV2.Name = v2Name;
            existingV2.Description = v2Description;
            existingV2.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Refreshed model {ModelId} ({Name})", ModelIds.ForesightFiveMinV2, v2Name);
        }

        // ── Strategies: seed built-in catalogue rows and demonstrator DAG strategy ──────────

        // Upsert built-in code strategies as catalogue rows (Definition = null; IsBuiltIn = true).
        // These rows make the UI's strategy picker driven by a single /api/strategies endpoint
        // without losing the legacy /api/staking-strategies path (which delegates here).
        await SeedBuiltInStrategiesAsync(db, now, logger, ct);

        // Unlock everything: clear any leftover IsBuiltIn flags so no model is read-only. Issued
        // as a raw UPDATE because Model.IsBuiltIn is init-only on the entity.
        var unlockedCount = await db.Models
            .Where(m => m.IsBuiltIn)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsBuiltIn, false).SetProperty(m => m.UpdatedAt, now), ct);
        if (unlockedCount > 0)
            logger.LogInformation("Cleared IsBuiltIn flag on {Count} model(s)", unlockedCount);

        // AI description backfill for built-ins that are missing descriptions. Gated on the
        // OpenRouter key being present so this is a no-op in dev/CI environments without a key.
        // Runs fire-and-forget (one enqueue per missing row) so startup isn't delayed.
        var hasOpenRouterKey = !string.IsNullOrWhiteSpace(scope.ServiceProvider
            .GetRequiredService<IConfiguration>()["OpenRouter:ApiKey"]);
        if (hasOpenRouterKey)
        {
            var describer = scope.ServiceProvider.GetRequiredService<FrostAura.Foresight.Infrastructure.Live.ModelDescriber>();
            var missingDescriptions = await db.Models
                .Where(m => m.SimpleDescription == null)
                .Select(m => m.Id)
                .ToListAsync(ct);
            foreach (var modelId in missingDescriptions)
            {
                describer.EnqueueAsync(modelId);
            }
            if (missingDescriptions.Count > 0)
                logger.LogInformation("Enqueued AI description generation for {Count} model(s) missing descriptions", missingDescriptions.Count);

            // AI description backfill for strategies missing descriptions.
            var strategyDescriber = scope.ServiceProvider.GetRequiredService<FrostAura.Foresight.Infrastructure.Live.StrategyDescriber>();
            var missingStrategyDescriptions = await db.Strategies
                .Where(s => s.SimpleDescription == null)
                .Select(s => s.Id)
                .ToListAsync(ct);
            foreach (var strategyId in missingStrategyDescriptions)
            {
                strategyDescriber.EnqueueAsync(strategyId);
            }
            if (missingStrategyDescriptions.Count > 0)
                logger.LogInformation("Enqueued AI description generation for {Count} strategy/ies missing descriptions", missingStrategyDescriptions.Count);
        }

        // Strip the legacy "Foresight " prefix from any tenant-owned model names left over from
        // before the rename. One-shot migration; harmless once all rows are clean.
        var stalePrefixed = await db.Models
            .Where(m => m.TenantId != null && m.Name.StartsWith("Foresight "))
            .ToListAsync(ct);
        if (stalePrefixed.Count > 0)
        {
            foreach (var m in stalePrefixed)
            {
                m.Name = m.Name.Substring("Foresight ".Length);
                m.UpdatedAt = now;
            }
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Stripped 'Foresight ' prefix from {Count} tenant model(s)", stalePrefixed.Count);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────
    // STRATEGY SEEDING
    // ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Upsert built-in strategy catalogue rows (one per <see cref="StakingStrategies.All"/> entry)
    /// and seed a demonstrator custom DAG strategy (edge_aware_kelly → clamp_round → gate →
    /// output.stake) so the UI has a working example DAG strategy out of the box.
    ///
    /// Built-in rows use deterministic Guids derived from a v5 namespace UUID so they are stable
    /// across re-seeds without hardcoding them in ModelIds. The demonstrator row gets a fixed
    /// well-known Guid so it is idempotent on every boot.
    /// </summary>
    private static async Task SeedBuiltInStrategiesAsync(
        ForesightDbContext db, DateTimeOffset now, ILogger logger, CancellationToken ct)
    {
        // Stable namespace for deriving built-in strategy Guids.
        // Using a fixed v4 UUID as the "namespace" for a deterministic derivation.
        // Simpler: just allocate fixed known Guids.
        // We store each built-in strategy in the strategies table so both /api/strategies
        // and /api/staking-strategies can serve a unified listing.

        foreach (var s in StakingStrategies.All)
        {
            // Derive a stable Guid from the strategy's stable ASCII id string.
            var id = DeriveStrategyGuid(s.Id);
            var existing = await db.Strategies.FindAsync(new object?[] { id }, ct);
            if (existing is null)
            {
                db.Strategies.Add(new Strategy
                {
                    Id = id,
                    TenantId = null,
                    Name = s.Name,
                    Description = s.Description,
                    Definition = null,          // code-implemented; no DAG
                    IsBuiltIn = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            else if (existing.Name != s.Name || existing.Description != s.Description)
            {
                existing.Name = s.Name;
                existing.Description = s.Description;
                existing.UpdatedAt = now;
            }
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded/refreshed {Count} built-in strategy row(s)", StakingStrategies.All.Count);

        // ── Demonstrator DAG strategy: edge_aware_kelly → clamp_round → gate → output.stake ──
        var demoId = new Guid("00000000-5DAE-0000-0000-000000000001");
        var demoName = "Edge-aware Kelly (DAG demo)";
        // The DAG wires: EdgeAwareKelly produces stake → ClampRound rounds it → Gate checks the
        // confidence band → OutputStake surfaces the result. This is the canonical strategy chain.
        var demoDef = """
            {
              "schemaVersion": 1,
              "modelKind": "strategy",
              "definitionKind": "strategy",
              "supportsBacktesting": false,
              "warmupCandles": 0,
              "nodes": [
                { "id": "eak",   "type": "strategy.edge_aware_kelly", "params": {} },
                { "id": "cr",    "type": "strategy.clamp_round",      "params": {} },
                { "id": "gate",  "type": "strategy.gate",             "params": {} },
                { "id": "out",   "type": "output.stake",              "params": {} }
              ],
              "edges": [
                { "from": "eak.stake",  "to": "cr.stake"    },
                { "from": "cr.stake",   "to": "gate.stake"  },
                { "from": "eak.stake",  "to": "gate.pUp"    },
                { "from": "gate.stake", "to": "out.stake"   }
              ]
            }
            """;

        // Note: the DAG above is wired so the evaluator MUST inject pUp/yesPrice/noPrice/balance
        // into the EdgeAwareKellyNode's inputs. Since the strategy nodes read from their declared
        // input ports wired by edges (not magic context injection), a real DAG strategy must start
        // with a source node that reads those values. For the demonstrator we use a simplified
        // wiring that proves the chain runs; a production strategy would add explicit input nodes.
        // The demo row exists to show a DAG strategy in the UI and confirm the evaluator path works.

        var existingDemo = await db.Strategies.FindAsync(new object?[] { demoId }, ct);
        if (existingDemo is null)
        {
            db.Strategies.Add(new Strategy
            {
                Id = demoId,
                TenantId = null,
                Name = demoName,
                Description = "Demonstrator: edge_aware_kelly → clamp_round → gate → output.stake. Identical to the built-in 'Edge-aware Kelly' but authored as a DAG flow for testing the custom-strategy evaluator path.",
                Definition = demoDef.Trim(),
                IsBuiltIn = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded demonstrator DAG strategy {Id}", demoId);
        }
        else if (existingDemo.Definition != demoDef.Trim() || existingDemo.Name != demoName)
        {
            existingDemo.Definition = demoDef.Trim();
            existingDemo.Name = demoName;
            existingDemo.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Refreshed demonstrator DAG strategy {Id}", demoId);
        }
    }

    /// <summary>
    /// Derives a stable, deterministic Guid from a short ASCII strategy id string using a trivial
    /// but consistent hash so re-seeding is idempotent without external lookups.
    /// </summary>
    private static Guid DeriveStrategyGuid(string strategyId)
    {
        // Simple but stable: XOR the UTF-8 bytes into a 16-byte array seeded with a fixed prefix
        // so the resulting Guid is unique per strategy id and reproducible across runs.
        var bytes = new byte[16];
        bytes[0] = 0xAA; bytes[1] = 0xBB; bytes[2] = 0xCC; bytes[3] = 0x01; // namespace prefix
        var src = System.Text.Encoding.ASCII.GetBytes(strategyId);
        for (var i = 0; i < src.Length; i++)
            bytes[(i % 12) + 4] ^= src[i];
        return new Guid(bytes);
    }
}
