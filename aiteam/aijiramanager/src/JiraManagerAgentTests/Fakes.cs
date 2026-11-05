using AiTeam.Configuration;

namespace JiraManagerAgent.Tests;

internal sealed class FakeJiraClient : IJiraClient
{
    public List<(string Project, string Title, string Description)> Created { get; } = [];
    public List<(string Key, string Status)> Transitions { get; } = [];
    public Queue<Exception> CreateFailures { get; } = new();
    public Queue<Exception> TransitionFailures { get; } = new();

    public Task<string> CreateIssueAsync(string projectKey, string title, string description, CancellationToken cancellationToken)
    {
        if (CreateFailures.TryDequeue(out var failure))
        {
            throw failure;
        }

        Created.Add((projectKey, title, description));
        return Task.FromResult($"{projectKey}-{Created.Count}");
    }

    public Task TransitionToAsync(string issueKey, string statusName, CancellationToken cancellationToken)
    {
        if (TransitionFailures.TryDequeue(out var failure))
        {
            throw failure;
        }

        Transitions.Add((issueKey, statusName));
        return Task.CompletedTask;
    }
}

internal sealed class FakeTaskRepository : IAgentTaskRepository
{
    public List<AgentTask> Tasks { get; } = [];

    public AgentTask Add(string projectKey = "DFAI", string title = "Title", string description = "Description")
    {
        var task = new AgentTask(Tasks.Count + 1, projectKey, title, description, AgentTaskStatus.New, null,
            DateTime.UtcNow, DateTime.UtcNow);
        Tasks.Add(task);
        return task;
    }

    public AgentTask Get(long id) => Tasks.Single(task => task.Id == id);

    public IReadOnlyList<AgentTask> List(string? status = null) =>
        Tasks.Where(task => status is null || task.Status == status).ToList();

    public AgentTask? Find(long id) => Tasks.SingleOrDefault(task => task.Id == id);

    public AgentTask Create(string projectKey, string title, string description) => Add(projectKey, title, description);

    public AgentTask? Update(long id, string projectKey, string title, string description, string status, string? jiraKey)
    {
        var index = Tasks.FindIndex(task => task.Id == id);
        Tasks[index] = Tasks[index] with
        {
            ProjectKey = projectKey, Title = title, Description = description, Status = status, JiraKey = jiraKey
        };
        return Tasks[index];
    }
}
