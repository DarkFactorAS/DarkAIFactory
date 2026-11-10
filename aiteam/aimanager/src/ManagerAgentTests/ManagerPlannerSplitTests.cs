using System.Data;
using System.Net;
using System.Text;
using System.Text.Json;
using AiTeam.Configuration;
using AiTeam.Tests;
using ManagerAgent.Repository;

namespace ManagerAgent.Tests;

public sealed class ManagerPlannerSplitTests
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

    private const string SplitJson = """
        {"decision":"split","plan":"Implement the work.","subtasks":[
          {"title":"Foundation","description":"Build the foundation","dependsOn":[]},
          {"title":"Follow-up","description":"Complete the feature","dependsOn":["Foundation"]},
          {"title":"Already complete","description":"This was done manually","dependsOn":["Foundation"]}]}
        """;

    [Fact]
    public async Task PlanAsync_ResumesSplitAndReleasesOnlyReadyIncompleteChildrenToWorkerStatus()
    {
        var database = CreateDatabase();
        var handler = new SplitHandler();
        using var jira = new JiraClient(Configuration, handler);
        var generated = 0;
        var planner = new ManagerPlanner(Configuration, new ManagerPlanRepository(database), jira, (_, _) =>
        {
            generated++;
            return Task.FromResult(SplitJson);
        });

        await planner.PlanAsync(1, handler.GetIssue("TASK-1"), CancellationToken.None);

        // The split plan is held for human approval: no sub-task exists yet.
        Assert.Equal(1, generated);
        Assert.Empty(handler.CreatedKeys);
        Assert.Contains("ai-awaiting-approval", handler.GetIssue("TASK-1").Labels);
        Assert.StartsWith("[triage:split_proposal]", new ManagerPlanRepository(database).Find("TASK-1")!.Plan);
        Assert.Contains(handler.Comments["TASK-1"], comment =>
            comment.Contains("Implement the work.") && comment.Contains("Build the foundation") &&
            comment.Contains("ai-agent/TASK-1"));
        await planner.PlanAsync(1, handler.GetIssue("TASK-1"), CancellationToken.None);
        Assert.Empty(handler.CreatedKeys);

        handler.AddLabel("TASK-1", "ai-approved");
        handler.FailNextParentComment = true;
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            planner.PlanAsync(1, handler.GetIssue("TASK-1"), CancellationToken.None));

        Assert.Equal(1, generated);
        Assert.Equal(3, handler.CreatedKeys.Count);
        Assert.False(new ManagerPlanRepository(database).Find("TASK-1")!.PostedToJira);
        handler.AddLabel("TASK-1", "ai-ignored");

        await planner.PlanAsync(1, handler.GetIssue("TASK-1"), CancellationToken.None);

        Assert.Equal(1, generated);
        Assert.Equal(3, handler.CreatedKeys.Count);
        Assert.DoesNotContain("ai-awaiting-approval", handler.GetIssue("TASK-1").Labels);
        Assert.DoesNotContain("ai-approved", handler.GetIssue("TASK-1").Labels);
        Assert.True(new ManagerPlanRepository(database).Find("TASK-1")!.PostedToJira);
        Assert.DoesNotContain("ai-ignored", handler.GetIssue("TASK-1").Labels);

        var rootKey = handler.CreatedKeys[0];
        var dependentKey = handler.CreatedKeys[1];
        var doneKey = handler.CreatedKeys[2];
        Assert.Equal("Selected for Development", handler.GetIssue(rootKey).StatusName);
        Assert.Contains("ai-analyzed", handler.GetIssue(rootKey).Labels);
        var rootDescription = handler.GetIssue(rootKey).Description;
        Assert.Contains("Create your branch off `ai-agent/TASK-1`", rootDescription);
        Assert.Contains("`ai-agent/TASK-1` as the base branch", rootDescription);
        Assert.Contains("ai-agent/TASK-1", new ManagerPlanRepository(database).Find(rootKey)!.Plan);
        Assert.StartsWith("[triage:dependency_hold]", new ManagerPlanRepository(database).Find(dependentKey)!.Plan);
        Assert.Equal("Done", handler.GetIssue(doneKey).StatusName);
        Assert.DoesNotContain("ai-analyzed", handler.GetIssue(doneKey).Labels);

        await planner.PlanAsync(1, handler.GetIssue(dependentKey), CancellationToken.None);
        Assert.DoesNotContain("ai-analyzed", handler.GetIssue(dependentKey).Labels);
        Assert.StartsWith("[triage:dependency_hold]", new ManagerPlanRepository(database).Find(dependentKey)!.Plan);

        handler.SetStatus(rootKey, "Done");
        await planner.PlanAsync(1, handler.GetIssue("TASK-1"), CancellationToken.None);

        Assert.Equal(1, generated);
        Assert.DoesNotContain("[triage:dependency_hold]", new ManagerPlanRepository(database).Find(dependentKey)!.Plan);
        Assert.Equal("Selected for Development", handler.GetIssue(dependentKey).StatusName);
        Assert.Contains("ai-analyzed", handler.GetIssue(dependentKey).Labels);
        Assert.Equal("Done", handler.GetIssue(doneKey).StatusName);
        Assert.DoesNotContain("ai-analyzed", handler.GetIssue(doneKey).Labels);
    }

    [Fact]
    public async Task PlanAsync_ReleasesDependencyHoldDirectlyWhenDependenciesAreDone()
    {
        var database = CreateDatabase();
        var handler = new SplitHandler();
        using var jira = new JiraClient(Configuration, handler);
        var planner = new ManagerPlanner(Configuration, new ManagerPlanRepository(database), jira,
            (_, _) => Task.FromResult(SplitJson));
        await planner.PlanAsync(1, handler.GetIssue("TASK-1"), CancellationToken.None);
        handler.AddLabel("TASK-1", "ai-approved");
        await planner.PlanAsync(1, handler.GetIssue("TASK-1"), CancellationToken.None);
        var rootKey = handler.CreatedKeys[0];
        var dependentKey = handler.CreatedKeys[1];

        await planner.PlanAsync(1, handler.GetIssue(dependentKey), CancellationToken.None);
        Assert.StartsWith("[triage:dependency_hold]", new ManagerPlanRepository(database).Find(dependentKey)!.Plan);

        handler.SetStatus(rootKey, "Done");
        await planner.PlanAsync(1, handler.GetIssue(dependentKey), CancellationToken.None);

        Assert.DoesNotContain("[triage:dependency_hold]", new ManagerPlanRepository(database).Find(dependentKey)!.Plan);
        Assert.Equal("Selected for Development", handler.GetIssue(dependentKey).StatusName);
        Assert.Contains("ai-manager-planned", handler.GetIssue(dependentKey).Labels);
        Assert.Contains("ai-analyzed", handler.GetIssue(dependentKey).Labels);
    }

    [Fact]
    public async Task PlanAsync_AnalyzesBlockedTaskButOnlyHandsItToWorkerWhenAllBlockersAreDone()
    {
        var database = CreateDatabase();
        var handler = new SplitHandler();
        handler.SetBlockers("TASK-1", new JiraBlocker("TASK-9", "In Progress"), new JiraBlocker("TASK-8", "Done"));
        using var jira = new JiraClient(Configuration, handler);
        var generated = 0;
        var planner = new ManagerPlanner(Configuration, new ManagerPlanRepository(database), jira, (_, _) =>
        {
            generated++;
            return Task.FromResult("Implement the work.");
        });

        await planner.PlanAsync(1, handler.GetIssue("TASK-1"), CancellationToken.None);

        // The tech design is produced and posted, but the worker handoff is withheld.
        Assert.Equal(1, generated);
        Assert.True(new ManagerPlanRepository(database).Find("TASK-1")!.PostedToJira);
        Assert.DoesNotContain("ai-analyzed", handler.GetIssue("TASK-1").Labels);
        Assert.DoesNotContain("ai-manager-planned", handler.GetIssue("TASK-1").Labels);
        Assert.Equal("Selected for Development", handler.GetIssue("TASK-1").StatusName);

        await planner.PlanAsync(1, handler.GetIssue("TASK-1"), CancellationToken.None);
        Assert.DoesNotContain("ai-analyzed", handler.GetIssue("TASK-1").Labels);

        handler.SetBlockers("TASK-1", new JiraBlocker("TASK-9", "Done"), new JiraBlocker("TASK-8", "done"));
        await planner.PlanAsync(1, handler.GetIssue("TASK-1"), CancellationToken.None);

        Assert.Equal(1, generated);
        Assert.Contains("ai-analyzed", handler.GetIssue("TASK-1").Labels);
        Assert.Contains("ai-manager-planned", handler.GetIssue("TASK-1").Labels);
    }

    [Fact]
    public async Task PlanAsync_KeepsDependencyHeldSubtaskBackWhileAnotherBlockerIsOpen()
    {
        var database = CreateDatabase();
        var handler = new SplitHandler();
        using var jira = new JiraClient(Configuration, handler);
        var planner = new ManagerPlanner(Configuration, new ManagerPlanRepository(database), jira,
            (_, _) => Task.FromResult(SplitJson));
        await planner.PlanAsync(1, handler.GetIssue("TASK-1"), CancellationToken.None);
        handler.AddLabel("TASK-1", "ai-approved");
        await planner.PlanAsync(1, handler.GetIssue("TASK-1"), CancellationToken.None);
        var rootKey = handler.CreatedKeys[0];
        var dependentKey = handler.CreatedKeys[1];
        handler.SetStatus(rootKey, "Done");
        handler.SetStatus(dependentKey, "To Do");
        handler.SetBlockers(dependentKey, new JiraBlocker("OTHER-1", "In Progress"));

        await planner.PlanAsync(1, handler.GetIssue(dependentKey), CancellationToken.None);

        Assert.Equal("To Do", handler.GetIssue(dependentKey).StatusName);
        Assert.StartsWith("[triage:dependency_hold]", new ManagerPlanRepository(database).Find(dependentKey)!.Plan);
        Assert.DoesNotContain("ai-analyzed", handler.GetIssue(dependentKey).Labels);

        handler.SetBlockers(dependentKey, new JiraBlocker("OTHER-1", "Done"));
        await planner.PlanAsync(1, handler.GetIssue(dependentKey), CancellationToken.None);

        Assert.Equal("Selected for Development", handler.GetIssue(dependentKey).StatusName);
        Assert.Contains("ai-analyzed", handler.GetIssue(dependentKey).Labels);
    }

    private static DatabaseStub CreateDatabase()
    {
        var database = new DatabaseStub();
        database.Rows.Columns.Add("manager_project_id", typeof(long));
        database.Rows.Columns.Add("plan", typeof(string));
        database.Rows.Columns.Add("posted_to_jira", typeof(bool));
        database.Rows.Columns.Add("requirements_hash", typeof(string));
        database.Rows.Columns.Add("source_updated", typeof(string));
        database.Rows.Columns.Add("jira_key", typeof(string));
        database.OnReader = command =>
        {
            var result = database.Rows.Clone();
            var key = (string)command.Values["@jira_key"];
            foreach (DataRow row in database.Rows.Rows)
            {
                if (Equals(row["jira_key"], key))
                {
                    result.ImportRow(row);
                }
            }

            return result.CreateDataReader();
        };
        database.OnNonQuery = command =>
        {
            var key = (string)command.Values["@jira_key"];
            var row = database.Rows.Rows.Cast<DataRow>().FirstOrDefault(candidate => Equals(candidate["jira_key"], key));
            if (command.CommandText.StartsWith("INSERT", StringComparison.Ordinal))
            {
                row ??= database.Rows.NewRow();
                if (row.RowState == DataRowState.Detached)
                {
                    database.Rows.Rows.Add(row);
                }

                row["manager_project_id"] = command.Values["@project_id"];
                row["plan"] = command.Values["@plan"];
                row["posted_to_jira"] = false;
                row["requirements_hash"] = command.Values["@requirements_hash"];
                row["source_updated"] = command.Values["@source_updated"];
                row["jira_key"] = key;
            }
            else if (command.CommandText.Contains("SET manager_project_id", StringComparison.Ordinal))
            {
                row!["manager_project_id"] = command.Values["@project_id"];
            }
            else if (command.CommandText.Contains("SET plan", StringComparison.Ordinal))
            {
                row!["plan"] = command.Values["@plan"];
            }
            else
            {
                row!["posted_to_jira"] = true;
            }

            return 1;
        };

        return database;
    }

    private sealed class SplitHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, JiraIssue> _issues = new()
        {
            ["TASK-1"] = new JiraIssue("TASK-1", "Title", "Description", "Selected for Development")
            {
                UpdatedAt = "2026-11-02T13:00:00Z"
            }
        };

        public List<string> CreatedKeys { get; } = [];
        public bool FailNextParentComment { get; set; }
        public Dictionary<string, List<string>> Comments { get; } = [];

        public JiraIssue GetIssue(string key) => _issues[key];

        public void SetStatus(string key, string status) =>
            _issues[key] = _issues[key] with { StatusName = status };

        public void SetBlockers(string key, params JiraBlocker[] blockers) =>
            _issues[key] = _issues[key] with { Blockers = blockers };

        public void AddLabel(string key, string label) =>
            _issues[key] = _issues[key] with { Labels = [.. _issues[key].Labels, label] };

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.EndsWith("/transitions", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, """{"transitions":[{"id":"selected","name":"Start","to":{"name":"Selected for Development"}}]}""");
            }

            if (request.Method == HttpMethod.Get)
            {
                return Json(HttpStatusCode.OK, SerializeIssue(GetIssue(IssueKey(path))));
            }

            if (request.Method == HttpMethod.Post && path == "/rest/api/3/issue")
            {
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var fields = document.RootElement.GetProperty("fields");
                var key = $"TASK-{CreatedKeys.Count + 2}";
                var title = fields.GetProperty("summary").GetString()!;
                var status = title == "Already complete" ? "Done" : "In Progress";
                var description = ReadAdfText(fields.GetProperty("description"));
                _issues[key] = new JiraIssue(key, title, description, status)
                {
                    UpdatedAt = "2026-11-02T13:00:00Z"
                };
                CreatedKeys.Add(key);
                return Json(HttpStatusCode.Created, JsonSerializer.Serialize(new { key }));
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/issueLink", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.NoContent, "{}");
            }

            var issueKey = IssueKey(path);
            if (request.Method == HttpMethod.Post && path.EndsWith("/comment", StringComparison.Ordinal))
            {
                if (issueKey == "TASK-1" && FailNextParentComment)
                {
                    FailNextParentComment = false;
                    return Json(HttpStatusCode.InternalServerError, "{}");
                }

                using var commentDocument = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                if (!Comments.TryGetValue(issueKey, out var comments))
                {
                    Comments[issueKey] = comments = [];
                }

                comments.Add(ReadAdfText(commentDocument.RootElement.GetProperty("body")));

                return Json(HttpStatusCode.Created, "{}");
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/transitions", StringComparison.Ordinal))
            {
                SetStatus(issueKey, "Selected for Development");
                return Json(HttpStatusCode.NoContent, "{}");
            }

            if (request.Method == HttpMethod.Put)
            {
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var operation = document.RootElement.GetProperty("update").GetProperty("labels")[0]
                    .EnumerateObject().Single();
                var label = operation.Value.GetString()!;
                var issue = GetIssue(issueKey);
                SetLabels(issueKey, operation.Name == "add"
                    ? issue.Labels.Append(label).Distinct().ToArray()
                    : issue.Labels.Where(value => value != label).ToArray());
                return Json(HttpStatusCode.NoContent, "{}");
            }

            throw new InvalidOperationException($"Unexpected Jira request: {request.Method} {path}");
        }

        private void SetLabels(string key, string[] labels) =>
            _issues[key] = _issues[key] with { Labels = labels };

        private static string IssueKey(string path) =>
            path.Split("/issue/", StringSplitOptions.None)[1].Split('/')[0];

        private static string SerializeIssue(JiraIssue issue) => JsonSerializer.Serialize(new
        {
            key = issue.Key,
            fields = new
            {
                summary = issue.Summary,
                description = new
                {
                    type = "doc",
                    content = new[] { new { type = "paragraph", content = new[] { new { type = "text", text = issue.Description } } } }
                },
                status = new { name = issue.StatusName },
                updated = issue.UpdatedAt,
                labels = issue.Labels,
                issuelinks = issue.Blockers.Select(blocker => new
                {
                    type = new { name = "Blocks", inward = "is blocked by", outward = "blocks" },
                    inwardIssue = new { key = blocker.Key, fields = new { status = new { name = blocker.StatusName } } }
                })
            }
        });

        private static string ReadAdfText(JsonElement element)
        {
            var text = new StringBuilder();
            AppendAdfText(element, text);
            return string.Join("\n", text.ToString().Split('\n').Select(line => line.TrimEnd()).Where(line => line.Length > 0));
        }

        private static void AppendAdfText(JsonElement element, StringBuilder text)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in element.EnumerateArray())
                {
                    AppendAdfText(child, text);
                }
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("text", out var value))
                {
                    text.Append(value.GetString());
                }
                else if (element.TryGetProperty("type", out var type) && type.GetString() == "hardBreak")
                {
                    text.Append('\n');
                }

                if (element.TryGetProperty("content", out var content))
                {
                    AppendAdfText(content, text);
                }

                if (element.TryGetProperty("type", out var blockType) &&
                    blockType.GetString() is "paragraph" or "heading" or "listItem" or "codeBlock")
                {
                    text.Append('\n');
                }
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
