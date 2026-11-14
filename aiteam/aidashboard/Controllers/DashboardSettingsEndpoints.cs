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

public sealed record ActiveStatusesRequest(List<string>? Statuses);

public interface IActiveStatusesStore
{
    void Save(IReadOnlyList<string> statuses);
}

public sealed class ActiveStatusesStore(IntegrationSettingsRepository repository) : IActiveStatusesStore
{
    public void Save(IReadOnlyList<string> statuses) => repository.SaveDashboardActiveStatuses(statuses);
}

// Read/write of the single setting Dashboard.AiLabels (client 'aidashboard').
public static class DashboardSettingsEndpoints
{
    public const int MaxLabels = 20;
    public const int MaxLabelLength = 255;

    // Shared across requests so persisting and publishing labels happen atomically.
    private static readonly object SaveLock = new();

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/settings/ai-labels", Get);
        app.MapPut("/api/settings/ai-labels", Put).RequireAuthorization();
        app.MapGet("/api/settings/active-statuses", GetStatuses);
        app.MapPut("/api/settings/active-statuses", PutStatuses).RequireAuthorization();
    }

    public const int MaxStatuses = 20;
    public const int MaxStatusLength = 255;

    public static IResult GetStatuses(IntegrationSettings settings) =>
        Results.Ok(new { statuses = settings.Dashboard.ActiveStatuses });

    public static IResult PutStatuses(
        IntegrationSettings settings, IActiveStatusesStore store, ActiveStatusesRequest? request)
    {
        if (request?.Statuses is null)
        {
            return Results.BadRequest(new { error = "statuses is required." });
        }

        if (request.Statuses.Any(s => s is not null && (s.Trim().Length > MaxStatusLength || s.Contains(','))))
        {
            return Results.BadRequest(new
            {
                error = $"Each status must be at most {MaxStatusLength} characters and contain no commas."
            });
        }

        var statuses = IntegrationSettingsRepository.NormalizeStatuses(request.Statuses.Select(s => s ?? string.Empty));
        if (statuses.Count > MaxStatuses)
        {
            return Results.BadRequest(new { error = $"At most {MaxStatuses} statuses are allowed." });
        }

        lock (SaveLock)
        {
            try
            {
                store.Save(statuses);
            }
            catch (Exception ex)
            {
                DFLogger.LogOutput(DFLogLevel.ERROR, "Settings", $"Saving active statuses failed ({ex.GetType().Name}).");
                return Results.Json(new { error = "Unable to save the statuses." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            settings.Dashboard.ActiveStatuses = statuses;
        }

        return Results.Ok(new { statuses });
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

        lock (SaveLock)
        {
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
        }

        return Results.Ok(new { labels });
    }
}
