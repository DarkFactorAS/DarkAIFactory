using DFCommonLib.Logger;

namespace AiAgent.Capabilities.Jira;

/// <summary>
/// Turns New agent_tasks rows of the agent's projects into Jira issues. A task is retried on later polls until
/// maxAttempts is reached, then marked Failed. The Jira key is stored as soon as the issue exists so a retry
/// after a failed transition never creates a duplicate issue.
/// </summary>
internal sealed class AgentTaskIntake(
    AiAgent.Core.IAgentTaskProcessorRepository tasks, IJiraClient jira, string jiraStatusName, int maxAttempts,
    Dictionary<long, int>? attempts = null)
{
    private const string Source = "JiraIntake";
    private readonly Dictionary<long, int> _attempts = attempts ?? [];

    public async Task ProcessNewTasksAsync(IReadOnlyCollection<string> projectKeys, CancellationToken cancellationToken)
    {
        var scope = new HashSet<string>(projectKeys, StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks.List(AiAgent.Core.AgentTaskStatus.New)
                     .Where(task => scope.Contains(task.ProjectKey)).OrderBy(task => task.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ProcessAsync(task, cancellationToken);
                _attempts.Remove(task.Id);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskUpdateConflictException exception)
            {
                _attempts.Remove(task.Id);
                DFLogger.LogOutput(DFLogLevel.WARNING, Source, exception.Message);
            }
            catch (Exception exception)
            {
                HandleFailure(task, exception);
            }
        }
    }

    private async Task ProcessAsync(AiAgent.Core.AgentTask task, CancellationToken cancellationToken)
    {
        var jiraKey = task.JiraKey;
        if (string.IsNullOrWhiteSpace(jiraKey))
        {
            jiraKey = await jira.CreateIssueAsync(task.ProjectKey, task.Title, task.Description, cancellationToken);
            if (!tasks.TrySetJiraKey(task.Id, AiAgent.Core.AgentTaskStatus.New, jiraKey))
            {
                throw new TaskUpdateConflictException(
                    $"Task {task.Id} changed while its Jira issue was being created; leaving it unchanged.");
            }

            DFLogger.LogOutput(DFLogLevel.INFO, Source, $"Created Jira issue {jiraKey} for task {task.Id}.");
        }

        await jira.TransitionToAsync(jiraKey, jiraStatusName, cancellationToken);
        if (!tasks.TrySetStatus(task.Id, AiAgent.Core.AgentTaskStatus.New, jiraKey, AiAgent.Core.AgentTaskStatus.Created))
        {
            throw new TaskUpdateConflictException(
                $"Task {task.Id} changed while Jira issue {jiraKey} was being prepared; leaving it unchanged.");
        }

        DFLogger.LogOutput(DFLogLevel.INFO, Source, $"Task {task.Id} is ready as {jiraKey} ({jiraStatusName}).");
    }

    private void HandleFailure(AiAgent.Core.AgentTask task, Exception exception)
    {
        var attemptCount = _attempts.GetValueOrDefault(task.Id) + 1;
        if (attemptCount < maxAttempts)
        {
            _attempts[task.Id] = attemptCount;
            DFLogger.LogOutput(
                DFLogLevel.WARNING, Source,
                $"Task {task.Id} failed (attempt {attemptCount} of {maxAttempts}): {exception.Message}. Retrying next cycle.");
            return;
        }

        _attempts.Remove(task.Id);
        DFLogger.LogOutput(
            DFLogLevel.ERROR, Source,
            $"Task {task.Id} failed after {maxAttempts} attempts: {exception.Message}. Marking it Failed.");
        try
        {
            var current = tasks.Find(task.Id);
            if (current is null ||
                !tasks.TrySetStatus(task.Id, AiAgent.Core.AgentTaskStatus.New, current.JiraKey, AiAgent.Core.AgentTaskStatus.Failed))
            {
                DFLogger.LogOutput(
                    DFLogLevel.WARNING, Source,
                    $"Task {task.Id} changed while being processed; leaving its status unchanged.");
            }
        }
        catch (Exception updateException)
        {
            DFLogger.LogOutput(DFLogLevel.ERROR, Source, $"Could not mark task {task.Id} Failed: {updateException.Message}.");
        }
    }

    internal sealed class TaskUpdateConflictException(string message) : Exception(message)
    {
    }
}
