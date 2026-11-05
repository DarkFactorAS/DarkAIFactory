using AiTeam.Configuration;
using DFCommonLib.Logger;

namespace JiraManagerAgent;

// Turns New agent_tasks rows into Jira issues. A task is retried on later polls until
// maxAttempts is reached, then marked Failed. The Jira key is stored as soon as the issue exists so a
// retry after a failed transition never creates a duplicate issue.
internal sealed class AgentTaskProcessor(
    IAgentTaskProcessorRepository tasks, IJiraClient jira, string jiraStatusName, int maxAttempts,
    Dictionary<long, int>? attempts = null)
{
    private readonly Dictionary<long, int> _attempts = attempts ?? [];

    public async Task ProcessNewTasksAsync(CancellationToken cancellationToken)
    {
        foreach (var task in tasks.List(AgentTaskStatus.New).OrderBy(task => task.Id))
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
                DFLogger.LogOutput(DFLogLevel.WARNING, "JiraManager", exception.Message);
            }
            catch (Exception exception)
            {
                HandleFailure(task, exception);
            }
        }
    }

    private async Task ProcessAsync(AgentTask task, CancellationToken cancellationToken)
    {
        var jiraKey = task.JiraKey;
        if (string.IsNullOrWhiteSpace(jiraKey))
        {
            jiraKey = await jira.CreateIssueAsync(task.ProjectKey, task.Title, task.Description, cancellationToken);
            if (!tasks.TrySetJiraKey(task.Id, AgentTaskStatus.New, jiraKey))
            {
                throw new TaskUpdateConflictException(
                    $"Task {task.Id} changed while its Jira issue was being created; leaving it unchanged.");
            }

            DFLogger.LogOutput(DFLogLevel.INFO, "JiraManager", $"Created Jira issue {jiraKey} for task {task.Id}.");
        }

        await jira.TransitionToAsync(jiraKey, jiraStatusName, cancellationToken);
        if (!tasks.TrySetStatus(task.Id, AgentTaskStatus.New, jiraKey, AgentTaskStatus.Created))
        {
            throw new TaskUpdateConflictException(
                $"Task {task.Id} changed while Jira issue {jiraKey} was being prepared; leaving it unchanged.");
        }

        DFLogger.LogOutput(DFLogLevel.INFO, "JiraManager", $"Task {task.Id} is ready as {jiraKey} ({jiraStatusName}).");
    }

    private void HandleFailure(AgentTask task, Exception exception)
    {
        var attemptCount = _attempts.GetValueOrDefault(task.Id) + 1;
        if (attemptCount < maxAttempts)
        {
            _attempts[task.Id] = attemptCount;
            DFLogger.LogOutput(
                DFLogLevel.WARNING, "JiraManager",
                $"Task {task.Id} failed (attempt {attemptCount} of {maxAttempts}): {exception.Message}. Retrying next cycle.");
            return;
        }

        _attempts.Remove(task.Id);
        DFLogger.LogOutput(
            DFLogLevel.ERROR, "JiraManager",
            $"Task {task.Id} failed after {maxAttempts} attempts: {exception.Message}. Marking it Failed.");
        try
        {
            var current = tasks.Find(task.Id);
            if (current is null ||
                !tasks.TrySetStatus(task.Id, AgentTaskStatus.New, current.JiraKey, AgentTaskStatus.Failed))
            {
                DFLogger.LogOutput(
                    DFLogLevel.WARNING, "JiraManager",
                    $"Task {task.Id} changed while being processed; leaving its status unchanged.");
            }
        }
        catch (Exception updateException)
        {
            DFLogger.LogOutput(
                DFLogLevel.ERROR, "JiraManager",
                $"Could not mark task {task.Id} Failed: {updateException.Message}.");
        }
    }

    internal sealed class TaskUpdateConflictException(string message) : Exception(message)
    {
    }
}
