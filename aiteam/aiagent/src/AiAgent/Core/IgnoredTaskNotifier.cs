namespace AiAgent.Core;

internal static class IgnoredTaskNotifier
{
    public static string BuildLine(string agentName, string reason) =>
        $"[{agentName}] Not picked up: {reason}";

    // Generic visual marker; it never encodes the reason the task is ignored.
    public const string IgnoredLabel = "ai-ignored";

    // Removes the ignored label when it is present. Failures are reported through onWarning and never thrown.
    public static async Task ClearAsync(
        string issueKey,
        IReadOnlyCollection<string> issueLabels,
        Func<string, string, CancellationToken, Task> removeLabel,
        Action<string> onWarning,
        CancellationToken cancellationToken)
    {
        if (!issueLabels.Contains(IgnoredLabel))
        {
            return;
        }
        try
        {
            await removeLabel(issueKey, IgnoredLabel, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            onWarning($"Could not remove the \"{IgnoredLabel}\" label from {issueKey}: {exception.Message}");
        }
    }

    // Always labels the issue, then posts a single-line Jira comment unless an identical one already exists.
    // Failures are reported through onWarning and never thrown.
    public static async Task<bool> NotifyAsync(
        string agentName,
        string issueKey,
        string reason,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> getComments,
        Func<string, string, CancellationToken, Task> addComment,
        Func<string, string, CancellationToken, Task> addLabel,
        Action<string> onWarning,
        CancellationToken cancellationToken)
    {
        try
        {
            await addLabel(issueKey, IgnoredLabel, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            onWarning($"Could not add the \"{IgnoredLabel}\" label to {issueKey}: {exception.Message}");
        }

        var line = BuildLine(agentName, reason);
        try
        {
            var existing = await getComments(issueKey, cancellationToken);
            if (existing.Any(comment => comment.Contains(line, StringComparison.Ordinal)))
            {
                return false;
            }

            await addComment(issueKey, line, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            onWarning($"Could not post the not-picked-up reason for {issueKey}: {exception.Message}");
            return false;
        }
    }
}
