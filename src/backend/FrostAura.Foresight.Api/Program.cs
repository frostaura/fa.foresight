using System.Text.Json.Serialization;
using FrostAura.Foresight.Api.Endpoints;
using FrostAura.Foresight.Api.Middleware;
using FrostAura.Foresight.Infrastructure;
using FrostAura.Foresight.Infrastructure.Persistence;

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
app.MapModelsEndpoints();
app.MapBacktestsEndpoints();
app.MapStrategiesEndpoints();
app.MapFlowsEndpoints();
app.MapChaosEndpoints();
app.MapGoLiveEndpoints();

app.Run();

public partial class Program;
