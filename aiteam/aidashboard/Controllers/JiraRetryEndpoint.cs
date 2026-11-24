using System.Text.Json;
using System.Text.RegularExpressions;
using DFCommonLib.Logger;

namespace AiDashboard.Tasks;

// Retries a failed task by clearing ALL labels on the Jira issue.
public static partial class JiraRetryEndpoint
{
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*-[0-9]+$")]
    private static partial Regex KeyPattern();

    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/tasks/{key}/retry", HandleAsync).RequireAuthorization();

    public static async Task<IResult> HandleAsync(
        IJiraLabelClient client, IActiveAiTaskService activeTasks, IApprovalLabelSource labelSource,
        string key, CancellationToken cancellationToken)
    {
        if (!KeyPattern().IsMatch(key))
        {
            return Results.BadRequest(new { error = "key must be a valid Jira issue key." });
        }

        try
        {
            var task = (await activeTasks.GetActiveAsync(cancellationToken))
                .FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
            if (task is null)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var taskLabels = task.Labels ?? [];
            if (!taskLabels.Contains(labelSource.Get().FailureLabel, StringComparer.OrdinalIgnoreCase))
            {
                return Results.Conflict(new { error = "The task has not failed." });
            }

            await client.ClearLabelsAsync(task.Key, cancellationToken);
            return Results.Ok(new { key = task.Key });
        }
        catch (Exception ex) when (
            ex is HttpRequestException or InvalidOperationException or InvalidDataException or
                JsonException or KeyNotFoundException ||
            ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            DFLogger.LogOutput(DFLogLevel.ERROR, "JiraRetry", ex.Message);
            return Results.Problem("Unable to retry the task in Jira.", statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
