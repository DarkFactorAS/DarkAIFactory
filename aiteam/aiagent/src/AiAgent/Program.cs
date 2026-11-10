using AiAgent;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "Config", "appsettings.json"), optional: true);
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "Config", $"appsettings.{builder.Environment.EnvironmentName}.json"), optional: true);

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("AiAgent"));
builder.Services.AddSingleton<IAgentInfo, AgentInfo>();
builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapHealthChecks("/health");
app.MapGet("/", (IAgentInfo info) => Results.Ok(new { agent = info.Name }));
app.Run();
