using System.Text.Json.Serialization;
using FrostAura.Foresight.Api.Endpoints;
using FrostAura.Foresight.Api.Middleware;
using FrostAura.Foresight.Infrastructure;
using FrostAura.Foresight.Infrastructure.Persistence;

// Load a local `.env` (dev convenience) BEFORE building configuration, so its KEY=VALUE pairs are in
// the process environment and ASP.NET's environment-variable config provider picks them up. Existing
// environment variables are NEVER overridden — real env (docker compose, shell, launch profile)
// always wins. No-op in containers where no .env is present (compose injects the vars directly).
// This means a plain `dotnet run` has the full config (Telegram token, wallet, etc.) without manual
// exports — preventing channels/secrets from silently dropping on a restart.
LoadDotEnv();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();
builder.Services.AddCors(opts => opts.AddDefaultPolicy(p => p
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.ConfigureHttpJsonOptions(opts =>
{
    opts.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    opts.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddForesightInfrastructure(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();
app.UseMiddleware<TenantResolutionMiddleware>();

if (app.Configuration.GetValue("Database:AutoInitialize", true))
{
    try
    {
        await DatabaseInitializer.InitializeAsync(app.Services);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Database auto-init skipped — Postgres may not be reachable.");
    }
}

app.MapGet("/", () => Results.Ok(new
{
    name = "FrostAura Foresight API",
    version = "0.1.0",
    docs = "/openapi/v1.json"
}));
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "fa.foresight.api" }));

app.MapTenantEndpoints();
app.MapLiveEndpoints();
app.MapPaperEndpoints();
app.MapSessionsEndpoints();
app.MapSessionsTransferEndpoints();
app.MapModelsEndpoints();
app.MapBacktestsEndpoints();
app.MapStrategiesEndpoints();
app.MapFlowsEndpoints();
app.MapChaosEndpoints();
app.MapGoLiveEndpoints();
app.MapPlatformConnectionsEndpoints();
app.MapAccountEndpoints();
app.MapNotificationsEndpoints();

app.Run();

// Walks up from the working directory to the first `.env` and loads KEY=VALUE pairs into the
// environment, skipping comments/blanks and never overriding an already-set variable. Value is taken
// literally after the first `=` (so connection strings with `;`/`=` are safe); surrounding quotes are
// stripped. Silently does nothing if no `.env` is found.
static void LoadDotEnv()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        var path = Path.Combine(dir.FullName, ".env");
        if (File.Exists(path))
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                var val = line[(eq + 1)..].Trim();
                if (val.Length >= 2 && ((val[0] == '"' && val[^1] == '"') || (val[0] == '\'' && val[^1] == '\'')))
                    val = val[1..^1];
                if (Environment.GetEnvironmentVariable(key) is null)
                    Environment.SetEnvironmentVariable(key, val);
            }
            return;
        }
        dir = dir.Parent;
    }
}

public partial class Program;
