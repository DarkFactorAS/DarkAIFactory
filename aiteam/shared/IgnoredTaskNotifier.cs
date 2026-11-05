namespace AiTeam.Configuration;

internal static class IgnoredTaskNotifier
{
    public static string BuildLine(string agentName, string reason) =>
        $"[{agentName}] Not picked up: {reason}";

    // Posts a single-line Jira comment unless an identical one already exists.
    // Failures are reported through onWarning and never thrown.
    public static async Task<bool> NotifyAsync(
        string agentName,
        string issueKey,
        string reason,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> getComments,
        Func<string, string, CancellationToken, Task> addComment,
        Action<string> onWarning,
        CancellationToken cancellationToken)
    {
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
