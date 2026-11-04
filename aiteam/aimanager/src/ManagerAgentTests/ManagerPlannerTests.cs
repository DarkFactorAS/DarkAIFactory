using System.Net;
using System.Text.Json;
using AiTeam.Configuration;
using AiTeam.Tests;
using ManagerAgent.Repository;

namespace ManagerAgent.Tests;

public sealed class ManagerPlannerTests
{
    private static readonly AgentConfiguration Configuration = new(
        new Uri("https://example.atlassian.net"), "bot@example.com", "token", "gh", "repo",
        new Uri("https://github.com/owner/repo"), "project = TASK", "ai-manager-planned",
        "ai-agent-failed", "In Progress", "In Review", TimeSpan.FromSeconds(30));
    private static readonly JiraIssue Issue = new("TASK-1", "Title", "Description", "Selected for Development")
    {
        UpdatedAt = "2026-11-02T13:00:00Z", Labels = ["ai-manager-planned", "ai-analyzed"]
    };

    [Fact]
    public async Task PlanAsync_UnchangedRequirementsUseZeroPaidRunsAndZeroJiraWrites()
    {
        var events = new List<string>();
        var database = CreateDatabase(CurrentPlan(), events);
        var handler = new PlanningHandler(Issue, events);
        using var jira = new JiraClient(Configuration, handler);
        var paidRuns = 0;
        var planner = new ManagerPlanner(Configuration, new ManagerPlanRepository(database), jira, (_, _) =>
        {
            paidRuns++;
            return Task.FromResult("Unexpected plan");
        });

        await planner.PlanAsync(1, Issue, CancellationToken.None);
        await planner.PlanAsync(1, Issue, CancellationToken.None);

        Assert.Equal(0, paidRuns);
        Assert.Empty(events);
    }

    [Fact]
    public async Task PlanAsync_RebindsCurrentPlanWhenIssueMovesToAnotherMapping()
    {
        var events = new List<string>();
        var database = CreateDatabase(CurrentPlan(), events, managerProjectId: 1);
        var handler = new PlanningHandler(Issue, events);
        using var jira = new JiraClient(Configuration, handler);
        var paidRuns = 0;
        var planner = new ManagerPlanner(Configuration, new ManagerPlanRepository(database), jira, (_, _) =>
        {
            paidRuns++;
            return Task.FromResult("Unexpected plan");
        });

        await planner.PlanAsync(2, Issue, CancellationToken.None);

        Assert.Equal(0, paidRuns);
        Assert.Equal(["rebind"], events);
        Assert.Equal(2L, database.Rows.Rows[0]["manager_project_id"]);
    }

    [Theory]
    [InlineData("New title", "Description")]
    [InlineData("Title", "New description")]
    public async Task PlanAsync_RefreshesChangedRequirementsOnceAndPublishesAfterSaving(string title, string description)
    {
        var events = new List<string>();
        var database = CreateDatabase(CurrentPlan(), events);
        var handler = new PlanningHandler(Issue with { Summary = title, Description = description }, events);
        using var jira = new JiraClient(Configuration, handler);
        var paidRuns = 0;
        var planner = new ManagerPlanner(Configuration, new ManagerPlanRepository(database), jira, (snapshot, _) =>
        {
            paidRuns++;
            Assert.Equal(title, snapshot.Summary);
            Assert.DoesNotContain("ai-analyzed", handler.Current.Labels);
            events.Add("generate");
            return Task.FromResult("Refreshed plan");
        });

        await planner.PlanAsync(1, Issue, CancellationToken.None);
        await planner.PlanAsync(1, Issue, CancellationToken.None);

        Assert.Equal(1, paidRuns);
        Assert.Equal(["remove:ai-analyzed", "remove:ai-manager-planned", "generate", "save",
            "comment", "posted", "add:ai-manager-planned", "add:ai-analyzed"], events);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://github.com/owner/old-repository")]
    public async Task PlanAsync_ReplansLegacyPlansAndChangedRepositoryMappings(string? oldRepository)
    {
        var events = new List<string>();
        var stored = new ManagerPlan("Old plan", true, oldRepository is null
            ? null
            : ManagerPlan.Fingerprint(Issue.Summary, Issue.Description, new Uri(oldRepository)));
        var database = CreateDatabase(stored, events);
        var handler = new PlanningHandler(Issue, events);
        using var jira = new JiraClient(Configuration, handler);
        var paidRuns = 0;
        var planner = new ManagerPlanner(Configuration, new ManagerPlanRepository(database), jira, (_, _) =>
        {
            paidRuns++;
            return Task.FromResult("Refreshed plan");
        });

        await planner.PlanAsync(1, Issue, CancellationToken.None);
        await planner.PlanAsync(1, Issue, CancellationToken.None);

        Assert.Equal(1, paidRuns);
        Assert.Contains("save", events);
        Assert.Contains("add:ai-analyzed", events);
    }

    [Theory]
    [InlineData("ai-agent-picked")]
    [InlineData("ai-agent-failed")]
    public async Task PlanAsync_DoesNotPlanOrMutateClaimedOrFailedIssues(string label)
    {
        var events = new List<string>();
        var database = CreateDatabase(CurrentPlan(), events);
        var handler = new PlanningHandler(Issue with { Summary = "Changed", Labels = [label] }, events);
        using var jira = new JiraClient(Configuration, handler);
        var planner = new ManagerPlanner(Configuration, new ManagerPlanRepository(database), jira,
            (_, _) => throw new InvalidOperationException("Must not generate"));

        await planner.PlanAsync(1, Issue, CancellationToken.None);

        Assert.Empty(database.Commands);
        Assert.Empty(events);
    }

    [Fact]
    public async Task PlanAsync_DiscardsPlanIfIssueChangesDuringGeneration()
    {
        var events = new List<string>();
        var database = CreateDatabase(null, events);
        var handler = new PlanningHandler(Issue, events);
        using var jira = new JiraClient(Configuration, handler);
        var planner = new ManagerPlanner(Configuration, new ManagerPlanRepository(database), jira, (_, _) =>
        {
            handler.Current = handler.Current with { Description = "Edited while planning" };
            return Task.FromResult("Outdated plan");
        });

        await planner.PlanAsync(1, Issue, CancellationToken.None);

        Assert.DoesNotContain("save", events);
        Assert.DoesNotContain("comment", events);
        Assert.DoesNotContain("add:ai-analyzed", events);
    }

    [Fact]
    public async Task PlanAsync_DoesNotPublishHandoffWhenRequirementsChangeDuringCommentPosting()
    {
        var events = new List<string>();
        var database = CreateDatabase(null, events);
        var handler = new PlanningHandler(Issue, events);
        handler.OnComment = () => handler.Current = handler.Current with { Description = "Edited while posting" };
        using var jira = new JiraClient(Configuration, handler);
        var planner = new ManagerPlanner(Configuration, new ManagerPlanRepository(database), jira,
            (_, _) => Task.FromResult("Plan"));

        await planner.PlanAsync(1, Issue, CancellationToken.None);

        Assert.Contains("save", events);
        Assert.DoesNotContain("add:ai-analyzed", events);
        var stored = Assert.IsType<ManagerPlan>(new ManagerPlanRepository(database).Find(Issue.Key));
        Assert.False(stored.CanImplement(handler.Current.Summary, handler.Current.Description, Configuration.GitHubRepositoryUrl));
    }

    [Fact]
    public async Task PlanAsync_RejectsEmptyPlansWithoutSavingOrHandingOff()
    {
        var events = new List<string>();
        var database = CreateDatabase(null, events);
        var handler = new PlanningHandler(Issue, events);
        using var jira = new JiraClient(Configuration, handler);
        var planner = new ManagerPlanner(Configuration, new ManagerPlanRepository(database), jira,
            (_, _) => Task.FromResult(" "));

        await Assert.ThrowsAsync<InvalidDataException>(() => planner.PlanAsync(1, Issue, CancellationToken.None));

        Assert.DoesNotContain("save", events);
        Assert.DoesNotContain("add:ai-analyzed", events);
    }

    private static ManagerPlan CurrentPlan() => new("Plan", true,
        ManagerPlan.Fingerprint(Issue.Summary, Issue.Description, Configuration.GitHubRepositoryUrl),
        "2026-11-02T12:00:00Z");

    private static DatabaseStub CreateDatabase(ManagerPlan? plan, List<string> events, long managerProjectId = 1)
    {
        var database = new DatabaseStub();
        database.Rows.Columns.Add("manager_project_id", typeof(long));
        database.Rows.Columns.Add("plan", typeof(string));
        database.Rows.Columns.Add("posted_to_jira", typeof(bool));
        database.Rows.Columns.Add("requirements_hash", typeof(string));
        database.Rows.Columns.Add("source_updated", typeof(string));
        if (plan is not null)
        {
            database.Rows.Rows.Add(managerProjectId, plan.Plan, plan.PostedToJira,
                (object?)plan.RequirementsHash ?? DBNull.Value, (object?)plan.SourceUpdated ?? DBNull.Value);
        }
        database.OnNonQuery = command =>
        {
            if (command.CommandText.StartsWith("UPDATE manager_plans SET manager_project_id", StringComparison.Ordinal))
            {
                database.Rows.Rows[0]["manager_project_id"] = command.Values["@project_id"];
                events.Add("rebind");
            }
            else if (command.CommandText.StartsWith("INSERT", StringComparison.Ordinal))
            {
                database.Rows.Rows.Clear();
                database.Rows.Rows.Add(command.Values["@project_id"], command.Values["@plan"], false,
                    command.Values["@requirements_hash"], command.Values["@source_updated"]);
                events.Add("save");
            }
            else
            {
                database.Rows.Rows[0]["posted_to_jira"] = true;
                events.Add("posted");
            }
            return 1;
        };
        return database;
    }

    private sealed class PlanningHandler(JiraIssue issue, List<string> events) : HttpMessageHandler
    {
        public JiraIssue Current { get; set; } = issue;
        public Action? OnComment { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        key = Current.Key,
                        fields = new
                        {
                            summary = Current.Summary, updated = Current.UpdatedAt, labels = Current.Labels,
                            status = new { name = Current.StatusName },
                            description = new
                            {
                                type = "doc",
                                content = new[] { new { type = "paragraph",
                                    content = new[] { new { type = "text", text = Current.Description } } } }
                            }
                        }
                    }))
                };
            }
            if (request.Method == HttpMethod.Post)
            {
                events.Add("comment");
                OnComment?.Invoke();
            }
            else
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var operation = body.RootElement.GetProperty("update").GetProperty("labels")[0].EnumerateObject().Single();
                var label = operation.Value.GetString()!;
                events.Add($"{operation.Name}:{label}");
                Current = Current with
                {
                    Labels = operation.Name == "remove"
                        ? Current.Labels.Where(value => value != label).ToArray()
                        : Current.Labels.Append(label).Distinct().ToArray()
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
