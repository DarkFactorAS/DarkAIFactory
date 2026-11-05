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
        "ai-agent-failed", "In Progress", "In Review", TimeSpan.FromSeconds(30))
    {
        AnalyzedLabel = "ai-analyzed", WorkerPickupLabel = "ai-agent-picked",
        ClarificationLabel = "ai-needs-clarification", ApprovalLabel = "ai-awaiting-approval",
        ApprovedLabel = "ai-approved"
    };
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
    public async Task PlanAsync_DoesNotPlanClaimedOrFailedIssuesAndMarksThemIgnored(string label)
    {
        var events = new List<string>();
        var database = CreateDatabase(CurrentPlan(), events);
        var handler = new PlanningHandler(Issue with { Summary = "Changed", Labels = [label] }, events);
        using var jira = new JiraClient(Configuration, handler);
        var planner = new ManagerPlanner(Configuration, new ManagerPlanRepository(database), jira,
            (_, _) => throw new InvalidOperationException("Must not generate"));

        await planner.PlanAsync(1, Issue, CancellationToken.None);

        Assert.Empty(database.Commands);
        Assert.Equal(["add:ai-ignored"], events);
        Assert.Contains("ai-ignored", handler.Current.Labels);
    }

    [Fact]
    public async Task ReportExcludedIssuesAsync_ReportsPickupAndFailureLabelsWithoutPlanning()
    {
        var events = new List<string>();
        var issues = new[]
        {
            Issue with { Key = "TASK-CLAIMED", Labels = ["ai-agent-picked"] },
            Issue with { Key = "TASK-FAILED", Labels = ["ai-agent-failed"] },
            Issue with { Key = "TASK-OTHER", Labels = [] }
        };
        var handler = new ExcludedIssuesHandler(issues);
        using var jira = new JiraClient(Configuration, handler);
        var database = CreateDatabase(null, events);
        var planner = new ManagerPlanner(Configuration, new ManagerPlanRepository(database), jira,
            (_, _) => throw new InvalidOperationException("Must not generate"));

        await planner.ReportExcludedIssuesAsync("TASK", CancellationToken.None);

        Assert.Equal(2, handler.PostedComments.Count);
        Assert.Contains(handler.PostedComments, comment => comment.Contains(
            "[AIManager] Not picked up: it already has the \"ai-agent-picked\" label, so the worker owns it.",
            StringComparison.Ordinal));
        Assert.Contains(handler.PostedComments, comment => comment.Contains(
            "[AIManager] Not picked up: it has the \"ai-agent-failed\" label. Remove the label to retry.",
            StringComparison.Ordinal));
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

    private const string ClarifyJson = """{"decision":"clarify","questions":["Which API?","What is the limit?"]}""";
    private const string AnalysisJson = """{"decision":"analysis_only","analysis":"The cache is safe to remove."}""";
    private const string SmallJson = """{"decision":"small","plan":"Fix the typo in README.md."}""";
    private const string ApprovalJson =
        """{"decision":"needs_approval","plan":"Change the login flow.","riskAreas":["authentication"]}""";

    private static readonly JiraIssue FreshIssue = Issue with { Labels = [] };

    private static ManagerPlanner TriagePlanner(DatabaseStub database, JiraClient jira, string output, Action? onGenerate = null) =>
        new(Configuration, new ManagerPlanRepository(database), jira, (_, _) =>
        {
            onGenerate?.Invoke();
            return Task.FromResult(output);
        });

    [Fact]
    public async Task PlanAsync_ClarifyPostsQuestionsAndLabelsWithoutHandoff()
    {
        var events = new List<string>();
        var database = CreateDatabase(null, events);
        using var jira = new JiraClient(Configuration, new PlanningHandler(FreshIssue, events));

        await TriagePlanner(database, jira, ClarifyJson).PlanAsync(1, FreshIssue, CancellationToken.None);

        Assert.Equal(["remove:ai-analyzed", "remove:ai-manager-planned", "save", "comment", "posted",
            "add:ai-needs-clarification"], events);
    }

    [Fact]
    public async Task PlanAsync_ClarifyDoesNotReplanUntilRequirementsChange()
    {
        var events = new List<string>();
        var database = CreateDatabase(null, events);
        var handler = new PlanningHandler(FreshIssue, events);
        using var jira = new JiraClient(Configuration, handler);
        var paidRuns = 0;
        var planner = TriagePlanner(database, jira, ClarifyJson, () => paidRuns++);

        await planner.PlanAsync(1, FreshIssue, CancellationToken.None);
        events.Clear();
        await planner.PlanAsync(1, handler.Current, CancellationToken.None);

        Assert.Equal(1, paidRuns);
        Assert.Equal(["add:ai-ignored"], events);
    }

    [Fact]
    public async Task PlanAsync_AnalysisOnlyPostsAnalysisAndTransitionsWithoutLabels()
    {
        var events = new List<string>();
        var database = CreateDatabase(null, events);
        using var jira = new JiraClient(Configuration, new PlanningHandler(FreshIssue, events));
        var planner = TriagePlanner(database, jira, AnalysisJson);

        await planner.PlanAsync(1, FreshIssue, CancellationToken.None);

        Assert.Equal(["remove:ai-analyzed", "remove:ai-manager-planned", "save", "comment", "posted", "transition"], events);
    }

    [Fact]
    public async Task PlanAsync_ResumesAnalysisOnlyAndClearsIgnoredMarkerWithoutReplanning()
    {
        var events = new List<string>();
        var database = CreateDatabase(null, events);
        var handler = new PlanningHandler(FreshIssue, events);
        using var jira = new JiraClient(Configuration, handler);
        var paidRuns = 0;
        var planner = TriagePlanner(database, jira, AnalysisJson, () => paidRuns++);

        await planner.PlanAsync(1, FreshIssue, CancellationToken.None);
        events.Clear();
        handler.Current = handler.Current with
        {
            StatusName = Configuration.AnalysisOnlyStatus,
            Labels = [.. handler.Current.Labels, "ai-ignored"]
        };
        await planner.PlanAsync(1, handler.Current, CancellationToken.None);

        Assert.Equal(1, paidRuns);
        Assert.Equal(["remove:ai-ignored"], events);
    }

    [Fact]
    public async Task PlanAsync_SmallChangeIsHandedOffImmediately()
    {
        var events = new List<string>();
        var database = CreateDatabase(null, events);
        using var jira = new JiraClient(Configuration, new PlanningHandler(FreshIssue, events));

        await TriagePlanner(database, jira, SmallJson).PlanAsync(1, FreshIssue, CancellationToken.None);

        Assert.Equal(["remove:ai-analyzed", "remove:ai-manager-planned", "save", "comment", "posted",
            "add:ai-manager-planned", "add:ai-analyzed"], events);
    }

    [Fact]
    public async Task PlanAsync_NeedsApprovalHoldsUntilApprovedLabelThenHandsOff()
    {
        var events = new List<string>();
        var database = CreateDatabase(null, events);
        var handler = new PlanningHandler(FreshIssue, events);
        using var jira = new JiraClient(Configuration, handler);
        var paidRuns = 0;
        var planner = TriagePlanner(database, jira, ApprovalJson, () => paidRuns++);

        await planner.PlanAsync(1, FreshIssue, CancellationToken.None);
        Assert.DoesNotContain("add:ai-analyzed", events);
        Assert.Contains("add:ai-awaiting-approval", events);
        Assert.Contains("[triage:needs_approval]", (string)database.Rows.Rows[0]["plan"]);

        events.Clear();
        await planner.PlanAsync(1, handler.Current, CancellationToken.None);
        Assert.Equal(["add:ai-ignored"], events);
        events.Clear();

        handler.Current = handler.Current with
        {
            Labels = handler.Current.Labels.Where(label => label != "ai-awaiting-approval").ToArray()
        };
        var renamedApprovalLabel = new ManagerPlanner(
            Configuration with { ApprovalLabel = "ai-awaiting-approval-renamed" },
            new ManagerPlanRepository(database), jira, (_, _) =>
            {
                paidRuns++;
                return Task.FromResult("Unexpected plan");
            });
        await renamedApprovalLabel.PlanAsync(1, handler.Current, CancellationToken.None);
        Assert.Equal(["add:ai-ignored"], events);
        Assert.DoesNotContain("ai-analyzed", handler.Current.Labels);

        events.Clear();
        handler.Current = handler.Current with { Labels = [.. handler.Current.Labels, "ai-approved"] };
        await renamedApprovalLabel.PlanAsync(1, handler.Current, CancellationToken.None);

        Assert.Equal(1, paidRuns);
        Assert.Equal(["remove:ai-ignored", "add:ai-manager-planned", "add:ai-analyzed", "replace-plan",
            "remove:ai-awaiting-approval-renamed", "remove:ai-approved"], events);
    }

    [Fact]
    public async Task PlanAsync_SmallChangeNeedsApprovalWhenAutoApproveIsDisabled()
    {
        var events = new List<string>();
        var database = CreateDatabase(null, events);
        using var jira = new JiraClient(Configuration, new PlanningHandler(FreshIssue, events));
        var strict = new ManagerPlanner(
            Configuration with { AutoApproveSmallChanges = false },
            new ManagerPlanRepository(database), jira, (_, _) => Task.FromResult(SmallJson));

        await strict.PlanAsync(1, FreshIssue, CancellationToken.None);

        Assert.Contains("add:ai-awaiting-approval", events);
        Assert.DoesNotContain("add:ai-analyzed", events);
    }

    [Fact]
    public async Task PlanAsync_MalformedTriageJsonFails()
    {
        var events = new List<string>();
        var database = CreateDatabase(null, events);
        using var jira = new JiraClient(Configuration, new PlanningHandler(FreshIssue, events));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            TriagePlanner(database, jira, """{"decision":"small",""").PlanAsync(1, FreshIssue, CancellationToken.None));

        Assert.DoesNotContain("save", events);
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
            else if (command.CommandText.StartsWith("UPDATE manager_plans SET plan", StringComparison.Ordinal))
            {
                database.Rows.Rows[0]["plan"] = command.Values["@plan"];
                events.Add("replace-plan");
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
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/transitions", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"transitions":[{"id":"31","name":"Review","to":{"name":"Code Review"}}]}""")
                };
            }
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
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/transitions", StringComparison.Ordinal))
            {
                events.Add("transition");
            }
            else if (request.Method == HttpMethod.Post)
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

    private sealed class ExcludedIssuesHandler(IReadOnlyList<JiraIssue> issues) : HttpMessageHandler
    {
        public List<string> PostedComments { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath == "/rest/api/3/search/jql")
            {
                var response = JsonSerializer.Serialize(new
                {
                    issues = issues.Select(issue => new
                    {
                        key = issue.Key,
                        fields = new
                        {
                            summary = issue.Summary,
                            description = (string?)null,
                            status = new { name = issue.StatusName },
                            updated = issue.UpdatedAt,
                            labels = issue.Labels
                        }
                    })
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(response)
                };
            }
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"comments":[],"total":0,"isLast":true}""")
                };
            }
            if (request.Method == HttpMethod.Post)
            {
                using var document = JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync(cancellationToken));
                PostedComments.Add(string.Concat(document.RootElement.GetProperty("body")
                    .GetProperty("content").EnumerateArray()
                    .SelectMany(paragraph => paragraph.GetProperty("content").EnumerateArray())
                    .Where(node => node.TryGetProperty("text", out _))
                    .Select(node => node.GetProperty("text").GetString() ?? string.Empty)));
                return new HttpResponseMessage(HttpStatusCode.Created);
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        }
    }
}
