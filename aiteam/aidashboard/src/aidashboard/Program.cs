using System.Text.Json;
using AiDashboard.Stats;
using AiDashboard.Tasks;
using AiTeam.Configuration;
using DFCommonLib.Config;
using DFCommonLib.Logger;
using DFCommonLib.Utils;

const string Html = "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>DarkAIFactoy - AI Team workflow</title></head><body><h1>DarkAIFactoy - AI Team workflow</h1></body></html>";

Directory.SetCurrentDirectory(AppContext.BaseDirectory);
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IConfigurationHelper, ConfigurationHelper<AppSettings>>();
new DFServices(builder.Services)
    .SetupLogger()
    .SetupMySql()
    .LogToConsole(DFLogLevel.INFO);

builder.Services.AddScoped<IntegrationSettingsRepository>();
builder.Services.AddSingleton(services =>
{
    using var scope = services.CreateScope();
    return scope.ServiceProvider.GetRequiredService<IntegrationSettingsRepository>().Load("aidashboard");
});

builder.Services.AddScoped<WorkflowLabelRepository>();
builder.Services.AddScoped<IAgentTaskRepository, AgentTaskRepository>();
builder.Services.AddHttpClient();
builder.Services.AddScoped<IActiveAiTaskService>(services =>
{
    var repository = services.GetRequiredService<WorkflowLabelRepository>();
    var httpClient = services.GetRequiredService<IHttpClientFactory>().CreateClient();
    return new ActiveAiTaskService(
        services.GetRequiredService<IntegrationSettings>(),
        repository.Load,
        httpClient);
});

builder.Services.AddSingleton<IJiraSolvedClient>(services =>
    new JiraSolvedClient(services.GetRequiredService<IntegrationSettings>()));

var app = builder.Build();

var appSettings = app.Services.GetRequiredService<IConfigurationHelper>().Settings;
DFLogger.LogOutput(
    DFLogLevel.INFO,
    "Startup",
    $"Loading configuration for environment '{builder.Environment.EnvironmentName}': " +
    $"Config/appsettings.{builder.Environment.EnvironmentName}.json");
DFLogger.LogOutput(
    DFLogLevel.INFO,
    "Startup",
    $"Connecting to database '{appSettings.DatabaseConnection.Database}' " +
    $"at {appSettings.DatabaseConnection.Server}:{appSettings.DatabaseConnection.Port}.");

// Load credentials server-side only; never expose them in page responses.
_ = app.Services.GetRequiredService<IntegrationSettings>();

app.MapGet("/api/health", () => Results.Ok(new { status = "healthy" }));

app.MapGet("/api/tasks/active-ai", async (IActiveAiTaskService service, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.GetActiveAsync(cancellationToken));
    }
    catch (Exception ex) when (
        ex is HttpRequestException or InvalidOperationException or InvalidDataException or
            JsonException or KeyNotFoundException ||
        ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
    {
        DFLogger.LogOutput(DFLogLevel.ERROR, "ActiveAiTasks", ex.Message);
        return Results.Problem("Unable to load active AI tasks.", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/stats/solved-by-project", SolvedByProjectEndpoint.HandleAsync);
AgentTaskEndpoints.Map(app);

app.UseDefaultFiles();
app.UseStaticFiles();

// SPA fallback; unknown /api routes stay 404.
app.MapFallback(context =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }

    var index = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
    if (!index.Exists)
    {
        context.Response.ContentType = "text/html";
        return context.Response.WriteAsync(Html);
    }

    context.Response.ContentType = "text/html";
    return context.Response.SendFileAsync(index);
});

app.Run();
