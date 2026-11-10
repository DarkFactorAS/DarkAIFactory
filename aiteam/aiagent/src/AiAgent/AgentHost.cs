using AiAgent.Capabilities;
using AiAgent.LanguageModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiAgent;

public static class AgentHost
{
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        var environment = builder.Environment.EnvironmentName;
        builder.Configuration.AddJsonFile("Config/appsettings.json", optional: true, reloadOnChange: false);
        builder.Configuration.AddJsonFile($"Config/appsettings.{environment}.json", optional: true, reloadOnChange: false);
        builder.Services.Configure<AgentSettings>(builder.Configuration);
        builder.Services.AddSingleton<ICapabilityRegistry, CapabilityRegistry>();
        builder.Services.AddSingleton<ILanguageModelRegistry, LanguageModelRegistry>();
        return builder;
    }

    /// <summary>Runs all enabled capabilities until they finish or cancellation is requested.</summary>
    public static async Task<int> RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("AiAgent");
        var settings = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentSettings>>().Value;
        var enabled = services.GetRequiredService<ICapabilityRegistry>().Resolve(settings.Capabilities);
        if (settings.LanguageModel is { Length: > 0 } modelName)
        {
            services.GetRequiredService<ILanguageModelRegistry>().Get(modelName);
        }

        if (enabled.Count == 0)
        {
            logger.LogInformation("{AppName}: no capabilities enabled, exiting.", settings.AppName);
            return 0;
        }

        logger.LogInformation("{AppName}: starting {Count} capabilities.", settings.AppName, enabled.Count);
        try
        {
            await Task.WhenAll(enabled.Select(c => c.RunAsync(cancellationToken)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        return 0;
    }
}
