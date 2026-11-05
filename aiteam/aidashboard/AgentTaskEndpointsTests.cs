using AiDashboard.Tasks;
using AiTeam.Configuration;
using Microsoft.AspNetCore.Http;

namespace AiDashboard.Tests;

public sealed class AgentTaskEndpointsTests
{
    private static int Status(IResult result) => ((IStatusCodeHttpResult)result).StatusCode!.Value;

    private static T Value<T>(IResult result) => (T)((IValueHttpResult)result).Value!;

    [Fact]
    public void Create_StoresTrimmedTaskAsNew()
    {
        var repository = new FakeRepository();
        var result = AgentTaskEndpoints.Create(
            repository, new AgentTaskRequest(" DFAI ", " Title ", " Body ", null, null));

        Assert.Equal(201, Status(result));
        var task = Value<AgentTask>(result);
        Assert.Equal("DFAI", task.ProjectKey);
        Assert.Equal("Title", task.Title);
        Assert.Equal("Body", task.Description);
        Assert.Equal(AgentTaskStatus.New, task.Status);
    }

    [Theory]
    [InlineData(null, "t", "d")]
    [InlineData(" ", "t", "d")]
    [InlineData("P", "", "d")]
    [InlineData("P", "t", null)]
    [InlineData("P", "t", "  ")]
    public void Create_RejectsMissingFields(string? project, string? title, string? description)
    {
        var repository = new FakeRepository();
        var result = AgentTaskEndpoints.Create(repository, new AgentTaskRequest(project, title, description, null, null));

        Assert.Equal(400, Status(result));
        Assert.Empty(repository.Tasks);
    }

    [Fact]
    public void Create_RejectsNullBodyAndOversizedValues()
    {
        var repository = new FakeRepository();
        Assert.Equal(400, Status(AgentTaskEndpoints.Create(repository, null)));
        var longTitle = new string('x', AgentTaskEndpoints.MaxTitleLength + 1);
        Assert.Equal(400, Status(AgentTaskEndpoints.Create(
            repository, new AgentTaskRequest("P", longTitle, "d", null, null))));
        Assert.Empty(repository.Tasks);
    }

    [Fact]
    public void List_FiltersByStatusAndRejectsUnknownStatus()
    {
        var repository = new FakeRepository();
        repository.Create("P", "a", "d");
        repository.Update(1, "P", "a", "d", AgentTaskStatus.Failed, null);
        repository.Create("P", "b", "d");

        Assert.Equal(2, Value<IReadOnlyList<AgentTask>>(AgentTaskEndpoints.List(repository)).Count);
        Assert.Single(Value<IReadOnlyList<AgentTask>>(AgentTaskEndpoints.List(repository, "Failed")));
        Assert.Equal(400, Status(AgentTaskEndpoints.List(repository, "Bogus")));
    }

    [Fact]
    public void Update_ChangesTaskAndValidatesStatus()
    {
        var repository = new FakeRepository();
        repository.Create("P", "a", "d");

        var ok = AgentTaskEndpoints.Update(
            repository, 1, new AgentTaskRequest("P", "new", "d2", "Created", " DFAI-1 "));
        Assert.Equal(200, Status(ok));
        var task = Value<AgentTask>(ok);
        Assert.Equal("Created", task.Status);
        Assert.Equal("DFAI-1", task.JiraKey);

        Assert.Equal(400, Status(AgentTaskEndpoints.Update(
            repository, 1, new AgentTaskRequest("P", "new", "d2", "Done", null))));
        Assert.Equal(404, Status(AgentTaskEndpoints.Update(
            repository, 99, new AgentTaskRequest("P", "new", "d2", "New", null))));
    }

    [Fact]
    public void Get_ReturnsTaskOrNotFound()
    {
        var repository = new FakeRepository();
        repository.Create("P", "a", "d");

        Assert.Equal(200, Status(AgentTaskEndpoints.Get(repository, 1)));
        Assert.Equal(404, Status(AgentTaskEndpoints.Get(repository, 2)));
    }

    [Fact]
    public void RepositoryFailure_Returns500WithoutDetails()
    {
        var result = AgentTaskEndpoints.List(new ThrowingRepository());

        Assert.Equal(500, Status(result));
        Assert.DoesNotContain("secret", Value<object>(result).ToString()!);
    }

    private sealed class FakeRepository : IAgentTaskRepository
    {
        public List<AgentTask> Tasks { get; } = [];

        public IReadOnlyList<AgentTask> List(string? status = null) =>
            Tasks.Where(t => status is null || t.Status == status).ToList();

        public AgentTask? Find(long id) => Tasks.FirstOrDefault(t => t.Id == id);

        public AgentTask Create(string projectKey, string title, string description)
        {
            var task = new AgentTask(Tasks.Count + 1, projectKey, title, description, AgentTaskStatus.New,
                null, DateTime.UtcNow, DateTime.UtcNow);
            Tasks.Add(task);
            return task;
        }

        public AgentTask? Update(
            long id, string projectKey, string title, string description, string status, string? jiraKey)
        {
            var index = Tasks.FindIndex(t => t.Id == id);
            if (index < 0)
            {
                return null;
            }

            Tasks[index] = Tasks[index] with
            {
                ProjectKey = projectKey, Title = title, Description = description, Status = status, JiraKey = jiraKey
            };
            return Tasks[index];
        }
    }

    private sealed class ThrowingRepository : IAgentTaskRepository
    {
        public IReadOnlyList<AgentTask> List(string? status = null) => throw new InvalidOperationException("secret");
        public AgentTask? Find(long id) => throw new InvalidOperationException("secret");
        public AgentTask Create(string projectKey, string title, string description) =>
            throw new InvalidOperationException("secret");
        public AgentTask? Update(long id, string projectKey, string title, string description, string status,
            string? jiraKey) => throw new InvalidOperationException("secret");
    }
}
