using AiTeam.Configuration;
using DFCommonLib.Logger;

namespace JiraManagerAgent;

// Turns New agent_tasks rows into Jira issues. A task is retried on later polls until
// maxAttempts is reached, then marked Failed. The Jira key is stored as soon as the issue exists so a
// retry after a failed transition never creates a duplicate issue.
internal sealed class AgentTaskProcessor(
    IAgentTaskRepository tasks, IJiraClient jira, string inProgressStatus, int maxAttempts,
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
            tasks.Update(task.Id, task.ProjectKey, task.Title, task.Description, AgentTaskStatus.New, jiraKey);
            DFLogger.LogOutput(DFLogLevel.INFO, "JiraManager", $"Created Jira issue {jiraKey} for task {task.Id}.");
        }

        await jira.TransitionToAsync(jiraKey, inProgressStatus, cancellationToken);
        tasks.Update(task.Id, task.ProjectKey, task.Title, task.Description, AgentTaskStatus.Created, jiraKey);
        DFLogger.LogOutput(DFLogLevel.INFO, "JiraManager", $"Task {task.Id} is ready as {jiraKey} ({inProgressStatus}).");
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
            tasks.Update(task.Id, task.ProjectKey, task.Title, task.Description, AgentTaskStatus.Failed, task.JiraKey);
        }
        catch (Exception updateException)
        {
            DFLogger.LogOutput(
                DFLogLevel.ERROR, "JiraManager",
                $"Could not mark task {task.Id} Failed: {updateException.Message}.");
        }
    }
}
