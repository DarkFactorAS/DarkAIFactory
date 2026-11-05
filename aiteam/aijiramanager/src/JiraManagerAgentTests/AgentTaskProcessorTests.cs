using AiTeam.Configuration;

namespace JiraManagerAgent.Tests;

public sealed class AgentTaskProcessorTests
{
    private static AgentTaskProcessor Create(FakeTaskRepository tasks, FakeJiraClient jira, int maxAttempts = 3) =>
        new(tasks, jira, "Selected for Development", maxAttempts);

    [Fact]
    public async Task NewTask_CreatesIssue_TransitionsAndMarksCreated()
    {
        var tasks = new FakeTaskRepository();
        tasks.Add("DFAI", "Build page", "Details");
        var jira = new FakeJiraClient();

        await Create(tasks, jira).ProcessNewTasksAsync(CancellationToken.None);

        Assert.Equal(("DFAI", "Build page", "Details"), Assert.Single(jira.Created));
        Assert.Equal(("DFAI-1", "Selected for Development"), Assert.Single(jira.Transitions));
        var task = tasks.Get(1);
        Assert.Equal(AgentTaskStatus.Created, task.Status);
        Assert.Equal("DFAI-1", task.JiraKey);
    }

    [Fact]
    public async Task NonNewTasks_AreIgnored()
    {
        var tasks = new FakeTaskRepository();
        tasks.Add();
        tasks.Update(1, "DFAI", "T", "D", AgentTaskStatus.Created, "DFAI-9");
        var jira = new FakeJiraClient();

        await Create(tasks, jira).ProcessNewTasksAsync(CancellationToken.None);

        Assert.Empty(jira.Created);
        Assert.Empty(jira.Transitions);
    }

    [Fact]
    public async Task CreateFailure_IsRetriedOnNextPoll()
    {
        var tasks = new FakeTaskRepository();
        tasks.Add();
        var jira = new FakeJiraClient();
        jira.CreateFailures.Enqueue(new HttpRequestException("boom"));
        var processor = Create(tasks, jira);

        await processor.ProcessNewTasksAsync(CancellationToken.None);
        Assert.Equal(AgentTaskStatus.New, tasks.Get(1).Status);

        await processor.ProcessNewTasksAsync(CancellationToken.None);
        Assert.Equal(AgentTaskStatus.Created, tasks.Get(1).Status);
    }

    [Fact]
    public async Task TransitionFailure_KeepsJiraKey_AndDoesNotCreateDuplicate()
    {
        var tasks = new FakeTaskRepository();
        tasks.Add();
        var jira = new FakeJiraClient();
        jira.TransitionFailures.Enqueue(new InvalidOperationException("no transition"));
        var processor = Create(tasks, jira);

        await processor.ProcessNewTasksAsync(CancellationToken.None);
        Assert.Equal(AgentTaskStatus.New, tasks.Get(1).Status);
        Assert.Equal("DFAI-1", tasks.Get(1).JiraKey);

        await processor.ProcessNewTasksAsync(CancellationToken.None);
        Assert.Single(jira.Created);
        Assert.Equal(AgentTaskStatus.Created, tasks.Get(1).Status);
    }

    [Fact]
    public async Task DashboardEditsDuringProcessing_ArePreserved()
    {
        var tasks = new FakeTaskRepository();
        tasks.Add();
        var jira = new FakeJiraClient
        {
            OnCreate = () => tasks.Tasks[0] = tasks.Tasks[0] with { Title = "Edited in dashboard" },
            OnTransition = () => tasks.Tasks[0] = tasks.Tasks[0] with { Description = "Updated description" }
        };

        await Create(tasks, jira).ProcessNewTasksAsync(CancellationToken.None);

        var task = tasks.Get(1);
        Assert.Equal("Edited in dashboard", task.Title);
        Assert.Equal("Updated description", task.Description);
        Assert.Equal(AgentTaskStatus.Created, task.Status);
        Assert.Equal("DFAI-1", task.JiraKey);
    }

    [Fact]
    public async Task DashboardStatusChangeDuringProcessing_IsNotOverwritten()
    {
        var tasks = new FakeTaskRepository();
        tasks.Add();
        var jira = new FakeJiraClient
        {
            OnTransition = () => tasks.Tasks[0] = tasks.Tasks[0] with { Status = AgentTaskStatus.Failed }
        };

        await Create(tasks, jira).ProcessNewTasksAsync(CancellationToken.None);

        Assert.Equal(AgentTaskStatus.Failed, tasks.Get(1).Status);
    }

    [Fact]
    public async Task TerminalTransitionFailure_PreservesPersistedJiraKey()
    {
        var tasks = new FakeTaskRepository();
        tasks.Add();
        var jira = new FakeJiraClient();
        jira.CreateFailures.Enqueue(new HttpRequestException("first create failed"));
        jira.CreateFailures.Enqueue(new HttpRequestException("second create failed"));
        jira.TransitionFailures.Enqueue(new InvalidOperationException("no transition"));
        var processor = Create(tasks, jira);

        await processor.ProcessNewTasksAsync(CancellationToken.None);
        await processor.ProcessNewTasksAsync(CancellationToken.None);
        await processor.ProcessNewTasksAsync(CancellationToken.None);

        Assert.Equal(AgentTaskStatus.Failed, tasks.Get(1).Status);
        Assert.Equal("DFAI-1", tasks.Get(1).JiraKey);
        Assert.Single(jira.Created);
    }

    [Fact]
    public async Task RepeatedFailures_MarkTaskFailed()
    {
        var tasks = new FakeTaskRepository();
        tasks.Add();
        var jira = new FakeJiraClient();
        for (var i = 0; i < 5; i++)
        {
            jira.CreateFailures.Enqueue(new HttpRequestException("down"));
        }

        var processor = Create(tasks, jira, maxAttempts: 2);
        await processor.ProcessNewTasksAsync(CancellationToken.None);
        Assert.Equal(AgentTaskStatus.New, tasks.Get(1).Status);
        await processor.ProcessNewTasksAsync(CancellationToken.None);

        Assert.Equal(AgentTaskStatus.Failed, tasks.Get(1).Status);
        await processor.ProcessNewTasksAsync(CancellationToken.None);
        Assert.Empty(jira.Created);
    }

    [Fact]
    public async Task OneFailingTask_DoesNotBlockOthers()
    {
        var tasks = new FakeTaskRepository();
        tasks.Add();
        tasks.Add();
        var jira = new FakeJiraClient();
        jira.CreateFailures.Enqueue(new HttpRequestException("first fails"));

        await Create(tasks, jira).ProcessNewTasksAsync(CancellationToken.None);

        Assert.Equal(AgentTaskStatus.New, tasks.Get(1).Status);
        Assert.Equal(AgentTaskStatus.Created, tasks.Get(2).Status);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var tasks = new FakeTaskRepository();
        tasks.Add();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Create(tasks, new FakeJiraClient()).ProcessNewTasksAsync(cancellation.Token));
    }
}
