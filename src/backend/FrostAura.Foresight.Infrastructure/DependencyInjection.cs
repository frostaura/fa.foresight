using FrostAura.Foresight.Application.Backtesting;
using FrostAura.Foresight.Application.Trading;
using FrostAura.Foresight.Application.Chaos;
using FrostAura.Foresight.Application.Flow;
using FrostAura.Foresight.Application.Flow.Nodes;
using FrostAura.Foresight.Application.Markets;
using FrostAura.Foresight.Application.Models;
using FrostAura.Foresight.Application.Tenancy;
using FrostAura.Foresight.Domain.Ports;
using FrostAura.Foresight.Infrastructure.Adapters;
using FrostAura.Foresight.Infrastructure.Ledger;
using FrostAura.Foresight.Infrastructure.Live;
using FrostAura.Foresight.Infrastructure.Markets;
using FrostAura.Foresight.Infrastructure.Paper;
using FrostAura.Foresight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FrostAura.Foresight.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddForesightInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connStr = config.GetConnectionString("Postgres")
            ?? "Host=localhost;Port=5432;Database=foresight;Username=foresight;Password=foresight_dev_pw";

        services.AddDbContext<ForesightDbContext>(opts => opts.UseNpgsql(connStr));

        services.Configure<PolymarketOptions>(config.GetSection("Polymarket"));
        services.Configure<PolymarketExecutionOptions>(config.GetSection("Polymarket"));
        services.Configure<KeyVaultOptions>(config.GetSection("KeyVault"));
        services.Configure<Ledger.PolygonOptions>(config.GetSection("Polygon"));
        services.Configure<TelegramBotOptions>(config.GetSection("TelegramBot"));
        services.Configure<DiscordBotOptions>(config.GetSection("DiscordBot"));

        services.AddHttpClient<IPredictionMarketProvider, PolymarketProvider>();

        // Key custody: a real Nethereum signer when a private key is configured, else the throwing stub.
        var hasWalletKey = !string.IsNullOrWhiteSpace(config.GetSection("KeyVault")["PrivateKey"]);
        if (hasWalletKey)
            services.AddSingleton<IKeyVault, NethereumKeyVault>();
        else
            services.AddSingleton<IKeyVault, LocalKeyVault>();

        // Execution: live Polymarket CLOB only when LiveTrading=true AND a wallet key is present.
        // Any other configuration stays in shadow (NullExecutionProvider logs [SHADOW]). This is the
        // hard config gate beneath the /golive runtime confirmation.
        var liveTrading = string.Equals(config.GetSection("Polymarket")["LiveTrading"], "true", StringComparison.OrdinalIgnoreCase);
        if (liveTrading && hasWalletKey)
        {
            services.AddHttpClient("polymarket-clob");
            services.AddSingleton<IExecutionProvider>(sp => new PolymarketExecutionProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("polymarket-clob"),
                sp.GetRequiredService<IKeyVault>(),
                sp.GetRequiredService<IOptions<PolymarketExecutionOptions>>(),
                sp.GetRequiredService<IOptions<KeyVaultOptions>>(),
                sp.GetRequiredService<ILogger<PolymarketExecutionProvider>>()));
        }
        else
        {
            services.AddSingleton<IExecutionProvider, NullExecutionProvider>();
        }

        // Trading arm (Phase 2). Runs in shadow while NullExecutionProvider is the registered
        // execution adapter; the same wiring carries live trades once a real adapter lands.
        services.Configure<Live.TradingGuardrailOptions>(config.GetSection("Trading"));
        services.AddSingleton<Live.ILiveTradingArm, Live.LiveTradingArm>();
        services.AddSingleton<Domain.Sizing.IPositionSizer, Domain.Sizing.KellyPositionSizer>();
        services.AddScoped<Live.MarketTradeExecutor>();

        // Channel adapters: register each channel whose token is configured. Outbound notifications
        // fan out to all of them via the composite IChannelAdapter; a single channel resolves directly,
        // and none falls back to the log channel so dev/test stays green.
        var hasTelegram = !string.IsNullOrWhiteSpace(config.GetSection("TelegramBot")["Token"]);
        var hasDiscord = !string.IsNullOrWhiteSpace(config.GetSection("DiscordBot")["Token"]);

        if (hasTelegram)
        {
            services.AddHttpClient("telegram");
            services.AddSingleton<TelegramChannelAdapter>(sp => new TelegramChannelAdapter(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("telegram"),
                sp.GetRequiredService<IOptions<TelegramBotOptions>>(),
                sp.GetRequiredService<ILogger<TelegramChannelAdapter>>()));
        }
        if (hasDiscord)
        {
            services.AddHttpClient("discord");
            services.AddSingleton<DiscordChannelAdapter>(sp => new DiscordChannelAdapter(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("discord"),
                sp.GetRequiredService<IOptions<DiscordBotOptions>>(),
                sp.GetRequiredService<ILogger<DiscordChannelAdapter>>()));
        }

        services.AddSingleton<IChannelAdapter>(sp =>
        {
            var children = new List<IChannelAdapter>();
            var tg = sp.GetService<TelegramChannelAdapter>();
            if (tg is not null) children.Add(tg);
            var dc = sp.GetService<DiscordChannelAdapter>();
            if (dc is not null) children.Add(dc);
            return children.Count switch
            {
                0 => new LogChannelAdapter(sp.GetRequiredService<ILogger<LogChannelAdapter>>()),
                1 => children[0],
                _ => new CompositeChannelAdapter(children)
            };
        });

        services.AddScoped<ITenantContext, TenantContext>();

        services.AddHttpClient<BinanceMarketDataClient>();
        services.AddSingleton<ILivePredictionEventHub, LivePredictionEventHub>();
        services.AddScoped<ILivePredictionService, LivePredictionService>();
        services.AddScoped<ICalibrationRescaler, CalibrationRescaler>();
        // Continuously fills horizon=1 prediction coverage independent of whether any UI is open,
        // so the (prediction, outcome) dataset stays unbiased for offline analysis. Opt-out via
        // Background:GapFiller=false (e.g. dev) so it doesn't continuously page Binance in the background.
        if (config.GetValue("Background:GapFiller", true))
            services.AddHostedService<LivePredictionGapFillerService>();

        // Server-side paper trading. The processor BackgroundService is what lets sessions keep
        // trading while the browser is closed — clients are now thin views over server state.
        services.AddSingleton<IPaperTradingEventHub, PaperTradingEventHub>();
        services.AddScoped<IPaperTradingService, PaperTradingService>();
        services.AddHostedService<PaperTradingProcessorService>();

        // iter-4 — flow execution engine and node catalogue. Every IFlowNode registration becomes
        // discoverable via NodeRegistry → the validator, executor, and AI assistant all rely on
        // the same DI surface.
        services.AddScoped<IFlowExecutor, FlowExecutor>();
        services.AddScoped<FlowValidator>();
        // NodeRegistry is Scoped so it can compose IFlowNodes that themselves consume scoped
        // services. Building the dict per request is microsecond-cost — negligible.
        services.AddScoped<NodeRegistry>();
        services.AddSingleton<IFlowNode, BinanceKlinesNode>();
        services.AddSingleton<IFlowNode, TechPackNode>();
        services.AddSingleton<IFlowNode, FeaturePackNode>();
        services.AddSingleton<IFlowNode, VolumePackNode>();
        services.AddSingleton<IFlowNode, NormPackNode>();
        services.AddSingleton<IFlowNode, MomentumPackNode>();
        services.AddSingleton<IFlowNode, CrossPackNode>();
        services.AddSingleton<IFlowNode, TemporalPackNode>();
        services.AddSingleton<IFlowNode, HtfRegimePackNode>();
        services.AddSingleton<IFlowNode, SubBarPackNode>();
        services.AddSingleton<IFlowNode, MicrostructureSourceNode>();
        services.AddSingleton<IFlowNode, OrderFlowPackNode>();
        services.AddSingleton<IFlowNode, MicroFlowPackNode>();
        services.AddSingleton<IFlowNode, DerivativesPackNode>();
        services.AddSingleton<IFlowNode, MatrixBuilderNode>();
        services.AddSingleton<IFlowNode, LinearRegressionNode>();
        services.AddSingleton<IFlowNode, LogisticRegressionNode>();
        services.AddSingleton<IFlowNode, GradientBoostedTreesNode>();
        services.AddSingleton<IFlowNode, MajorityVoteNode>();
        services.AddSingleton<IFlowNode, FlatBaselineNode>();
        services.AddSingleton<IFlowNode, OutputPredictionNode>();
        // Strategy-DAG nodes (pure) + the strategy terminal — strategies authored as flows.
        services.AddSingleton<IFlowNode, OutputStakeNode>();
        services.AddSingleton<IFlowNode, FlatStrategyNode>();
        services.AddSingleton<IFlowNode, MartingaleStepNode>();
        services.AddSingleton<IFlowNode, KellyStrategyNode>();
        services.AddSingleton<IFlowNode, EdgeAwareKellyNode>();
        services.AddSingleton<IFlowNode, ClampRoundNode>();
        services.AddSingleton<IFlowNode, GateNode>();
        // Executable code node — delegates to the Python sandbox sidecar. Scoped because it depends
        // on the (typed-HttpClient) ISandboxExecutor; NodeRegistry is scoped too.
        services.AddScoped<IFlowNode, CodePythonNode>();
        services.AddHttpClient<ISandboxExecutor, SandboxExecutor>();

        // Historical-candle layer: Postgres-backed Binance cache for backtests. The scoped registration
        // gives the adapter its own DbContext per request — backtest runs hold their own scope.
        services.AddScoped<IHistoricalCandleProvider, BinanceHistoricalCandleAdapter>();
        // Historical order-flow microstructure: reconstructed from data.binance.vision daily
        // aggTrades dumps + Postgres-cached. Typed HttpClient points at the public dump host (a
        // different origin than api.binance.com). Backtests/training/live pick it up via the optional
        // IHistoricalMicrostructureProvider ctor param — no other wiring needed.
        services.AddHttpClient<IHistoricalMicrostructureProvider, BinanceHistoricalMicrostructureAdapter>(c =>
        {
            c.BaseAddress = new Uri("https://data.binance.vision");
            c.Timeout = TimeSpan.FromMinutes(5);
        });
        // Cache-only reader for the LIVE predict path (never downloads on the request thread).
        services.AddScoped<Adapters.CachedMicrostructureReader>();
        // Keeps the microstructure cache current at the live edge via REST (opt-in: FORESIGHT_MICRO_FOLLOWER=true).
        services.AddHostedService<Live.MicrostructureFollowerService>();
        // Proactively hydrates the cache for every supported (symbol, interval) on startup so the
        // first 365-day backtest finds its data already in Postgres instead of paging Binance live.
        // Opt-out via Background:CacheWarmer=false (e.g. dev) to avoid a startup Binance backfill storm.
        if (config.GetValue("Background:CacheWarmer", true))
            services.AddHostedService<Live.HistoricalCacheWarmerService>();

        // Active-model resolver — 30s in-memory cache for the per-(tenant, symbol, interval) →
        // ModelId mapping. Singleton so cached values survive across DI scopes; the resolver opens
        // its own scope internally when it has to read from the DbContext.
        services.AddSingleton<IActiveModelResolver, Live.ActiveModelResolver>();
        // OpenRouter client for AI model description generation. Registered as a typed HttpClient
        // and no-ops (returns null) when OpenRouter:ApiKey is absent — safe to have in every env.
        services.AddHttpClient<Adapters.OpenRouterClient>();
        // ModelDescriber is singleton: it holds no per-request state, uses IServiceScopeFactory
        // for its background DB writes, and is injected into the scoped ModelsService safely via
        // the singleton-captures-scoped-through-factory pattern.
        services.AddSingleton<Live.ModelDescriber>();
        // StrategyDescriber mirrors ModelDescriber: singleton, background scope factory pattern.
        services.AddSingleton<Live.StrategyDescriber>();
        services.AddScoped<IModelsService, Live.ModelsService>();
        services.AddScoped<Live.ActiveModelsService>();

        // Venue price store — resolves anti-look-ahead entry quotes; falls back to synthetic-flat.
        services.AddScoped<IVenuePriceStore, VenuePriceStore>();

        // Strategy evaluator — unified built-in + custom-DAG path. Scoped because it consumes the
        // scoped FlowExecutor and ForesightDbContext.
        services.AddScoped<IStrategyEvaluator, Live.StrategyEvaluator>();

        // Backtesting + training services. The runner + trainer are scoped because they consume the
        // executor (scoped) and a per-request historical candle provider scope.
        services.AddScoped<BacktestRunner>();
        services.AddScoped<ModelTrainer>();
        services.AddScoped<WalkForwardEvaluator>();
        services.AddScoped<IBacktestsService, Live.BacktestsService>();
        services.AddSingleton<Live.IBacktestEventHub, Live.BacktestEventHub>();
        services.AddScoped<IModelTrainingService, Live.ModelTrainingService>();
        services.AddScoped<IWalkForwardService, Live.WalkForwardService>();

        // Workstream D: chaos/bust test engine.
        services.AddScoped<IChaosService, Infrastructure.Chaos.ChaosService>();
        services.AddSingleton<Infrastructure.Chaos.IChaosEventHub, Infrastructure.Chaos.ChaosEventHub>();

        // Workstream E: live sessions + reservation ledger + venue capabilities.
        // AccountLedger uses a typed HttpClient for Polygon RPC calls (on-chain pUSD balance).
        services.AddHttpClient<IAccountLedger, AccountLedger>(c =>
        {
            // Base address not set — AccountLedger uses the full RpcUrl from PolygonOptions per call.
            c.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddScoped<ILiveSessionEngine, LiveSessionEngine>();
        services.AddHostedService<LiveSessionProcessorService>();
        services.AddSingleton<IVenueCapabilities, PolymarketBtcCapabilities>();

        // Outbound notifications: thin best-effort facade over IChannelAdapter.
        // Scoped so it shares the same channel singleton across a request/tick scope.
        services.AddScoped<TradingNotifier>();

        // Scheduled reservation reconcile loop (Phase E).
        services.AddHostedService<AccountReconciliationService>();

        return services;
    }
}
