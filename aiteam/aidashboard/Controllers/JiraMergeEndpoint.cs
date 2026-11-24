using System.Text.Json;
using System.Text.RegularExpressions;
using AiDashboard.Tasks;
using DFCommonLib.Logger;

namespace AiDashboard.Tasks;

// Requests the merge of a ready-to-merge pull request by adding a label to the Jira issue.
// The dashboard never calls GitHub; the agent performs the merge when it sees the label.
public static partial class JiraMergeEndpoint
{
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*-[0-9]+$")]
    private static partial Regex KeyPattern();

    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/tasks/{key}/merge", HandleAsync).RequireAuthorization();

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

            var labels = labelSource.Get();
            var taskLabels = task.Labels ?? [];
            if (!taskLabels.Contains(labels.ReadyToMergeLabel, StringComparer.OrdinalIgnoreCase) ||
                taskLabels.Contains(labels.FailureLabel, StringComparer.OrdinalIgnoreCase))
            {
                return Results.Conflict(new { error = "The task is not ready to merge." });
            }

            if (!taskLabels.Contains(labels.MergeRequestedLabel, StringComparer.OrdinalIgnoreCase))
            {
                await client.AddLabelAsync(task.Key, labels.MergeRequestedLabel, cancellationToken);
            }

            return Results.Ok(new { key = task.Key, label = labels.MergeRequestedLabel });
        }
        catch (Exception ex) when (
            ex is HttpRequestException or InvalidOperationException or InvalidDataException or
                JsonException or KeyNotFoundException ||
            ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            DFLogger.LogOutput(DFLogLevel.ERROR, "JiraMerge", ex.Message);
            return Results.Problem("Unable to request the merge in Jira.", statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
