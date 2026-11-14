using AiTeam.Configuration;
using DFCommonLib.Logger;

namespace AiDashboard.Tasks;

public sealed record AiLabelsRequest(List<string>? Labels);

public interface IAiLabelsStore
{
    void Save(IReadOnlyList<string> labels);
}

public sealed class AiLabelsStore(IntegrationSettingsRepository repository) : IAiLabelsStore
{
    public void Save(IReadOnlyList<string> labels) => repository.SaveDashboardAiLabels(labels);
}

// Read/write of the single setting Dashboard.AiLabels (client 'aidashboard').
public static class DashboardSettingsEndpoints
{
    public const int MaxLabels = 20;
    public const int MaxLabelLength = 255;

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/settings/ai-labels", Get);
        app.MapPut("/api/settings/ai-labels", Put).RequireAuthorization();
    }

    public static IResult Get(IntegrationSettings settings) =>
        Results.Ok(new { labels = settings.Dashboard.AiLabels });

    public static IResult Put(IntegrationSettings settings, IAiLabelsStore store, AiLabelsRequest? request)
    {
        if (request?.Labels is null)
        {
            return Results.BadRequest(new { error = "labels is required." });
        }

        var labels = IntegrationSettingsRepository.NormalizeAiLabels(request.Labels.Select(l => l ?? string.Empty));
        if (labels.Count > MaxLabels)
        {
            return Results.BadRequest(new { error = $"At most {MaxLabels} labels are allowed." });
        }

        if (request.Labels.Any(l => l is not null && (l.Length > MaxLabelLength || l.Trim().Any(c => char.IsWhiteSpace(c) || c == ','))))
        {
            return Results.BadRequest(new
            {
                error = $"Each label must be at most {MaxLabelLength} characters and contain no whitespace or commas."
            });
        }

        try
        {
            store.Save(labels);
        }
        catch (Exception ex)
        {
            DFLogger.LogOutput(DFLogLevel.ERROR, "Settings", $"Saving AI labels failed ({ex.GetType().Name}).");
            return Results.Json(new { error = "Unable to save the AI labels." },
                statusCode: StatusCodes.Status500InternalServerError);
        }

        settings.Dashboard.AiLabels = labels;
        return Results.Ok(new { labels });
    }
}
