using AiTeam.Configuration;
using DFCommonLib.DataAccess;
using System.Text.RegularExpressions;

namespace CodingAgent.Tests;

public sealed class IntegrationSettingsTests
{
    private static readonly string[] Clients = ["aimanager", "aiworker", "aibuildpage"];

    [Fact]
    public void Load_AllClientsShareEverySettingExceptTheThreeExplicitExceptions()
    {
        var values = Values();
        foreach (var client in Clients)
        {
            // Even unexpected legacy per-client rows must not override shared settings.
            values[(client, "Jira.JiraApiToken")] = "incorrect-client-token";
            values[(client, "Jira.JiraFailureLabel")] = "incorrect-client-label";
            values[(client, "GitHub.AgentGitHubToken")] = "incorrect-client-token";
            var settings = IntegrationSettingsRepository.FromValues(client, values, 5);

            Assert.Equal("https://example.atlassian.net", settings.Jira.JiraBaseUrl);
            Assert.Equal("bot@example.com", settings.Jira.JiraEmail);
            Assert.Equal("shared-jira-token", settings.Jira.JiraApiToken);
            Assert.Equal("SHARED", settings.Jira.JiraProjectKey);
            Assert.Equal("Ready", settings.Jira.JiraStatusName);
            Assert.Equal("priority = High", settings.Jira.JiraJql);
            Assert.Equal("shared-failed", settings.Jira.JiraFailureLabel);
            Assert.Equal("Doing", settings.Jira.JiraInProgressStatus);
            Assert.Equal("Review", settings.Jira.JiraInReviewStatus);
            Assert.Equal("shared-copilot-token", settings.GitHub.AgentGitHubToken);
            Assert.Equal("https://github.com/owner/repo", settings.GitHub.TargetRepositoryUrl);
            Assert.Equal($"{client}-picked", settings.Jira.JiraPickupLabel);
            Assert.Equal($"{client}-analyzed", settings.Jira.JiraAnalyzedLabel);
            Assert.Equal($"{client}-repository-token", settings.GitHub.GitHubRepositoryToken);
            Assert.Equal(5, settings.PollIntervalSeconds);
        }
    }

    [Theory]
    [InlineData("Jira.JiraPickupLabel")]
    [InlineData("Jira.JiraAnalyzedLabel")]
    [InlineData("GitHub.GitHubRepositoryToken")]
    public void Load_MissingClientValueNeverFallsBackToSharedOrAnotherClient(string name)
    {
        var values = Values();
        values.Remove(("aiworker", name));
        values[("shared", name)] = "incorrect-fallback";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            IntegrationSettingsRepository.FromValues("aiworker", values));

        Assert.Contains($"settings[aiworker, {name}]", exception.Message);
        Assert.DoesNotContain("incorrect-fallback", exception.Message);
    }

    [Fact]
    public void Load_MissingSharedValueNeverFallsBackToClientValue()
    {
        var values = Values();
        values.Remove(("shared", "Jira.JiraApiToken"));
        values[("aiworker", "Jira.JiraApiToken")] = "private-token";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            IntegrationSettingsRepository.FromValues("aiworker", values));

        Assert.Contains("settings[shared, Jira.JiraApiToken]", exception.Message);
        Assert.DoesNotContain("private-token", exception.Message);
    }

    [Fact]
    public void Load_RejectsUnknownClient()
    {
        Assert.Throws<ArgumentException>(() => IntegrationSettingsRepository.FromValues("unknown", Values()));
    }

    [Fact]
    public void Patcher_SeedsExactlyTheRowsRequiredByEveryClient()
    {
        var patcher = new SettingsPatcherStub(0);
        Assert.True(IntegrationSettingsPatcher.Run(patcher));
        var values = Regex.Matches(patcher.Sql[1], @"\('([^']+)', '([^']+)', '([^']*)'\)")
            .ToDictionary(match => (match.Groups[1].Value, match.Groups[2].Value),
                match => match.Groups[3].Value);

        Assert.Equal(20, values.Count);
        Assert.Equal(11, values.Keys.Count(key => key.Item1 == "shared"));
        foreach (var client in Clients)
        {
            Assert.Equal(3, values.Keys.Count(key => key.Item1 == client));
            var settings = IntegrationSettingsRepository.FromValues(client, values);
            Assert.Equal("ai-agent-failed", settings.Jira.JiraFailureLabel);
            Assert.Equal("ai-analyzed", settings.Jira.JiraAnalyzedLabel);
            Assert.Empty(settings.GitHub.GitHubRepositoryToken);
            Assert.Empty(settings.Jira.JiraApiToken);
        }
    }

    [Fact]
    public void WorkerPatcher_RunsSharedSettingsMigrations()
    {
        var patcher = new SettingsPatcherStub(0);

        Assert.True(new CodingAgent.Repository.AiDatabasePatcher(patcher).RunPatcher());
        Assert.Equal("IntegrationSettings", patcher.System);
        Assert.Contains(patcher.Sql, sql => sql.Contains("CREATE TABLE `settings`", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, true, 2)]
    [InlineData(1, false, 1)]
    [InlineData(2, false, 2)]
    public void Patcher_CreatesAndSeedsSettingsAndPropagatesFailures(
        int failingPatch, bool expected, int patchCount)
    {
        var patcher = new SettingsPatcherStub(failingPatch);

        Assert.Equal(expected, IntegrationSettingsPatcher.Run(patcher));
        Assert.Equal("IntegrationSettings", patcher.System);
        Assert.Equal(patchCount, patcher.Sql.Count);
        Assert.Contains("CHARACTER SET ascii COLLATE ascii_bin", patcher.Sql[0]);
        Assert.Contains("CONSTRAINT `ck_settings_scope` CHECK", patcher.Sql[0]);
        Assert.Contains("`client` = 'shared' AND `name` NOT IN", patcher.Sql[0]);
        Assert.Contains("`client` IN ('aimanager', 'aiworker', 'aibuildpage') AND `name` IN", patcher.Sql[0]);
        if (patchCount == 2)
        {
            foreach (var client in Clients)
            {
                Assert.Contains($"('{client}', 'GitHub.GitHubRepositoryToken', '')", patcher.Sql[1]);
                Assert.Contains($"('{client}', 'Jira.JiraPickupLabel',", patcher.Sql[1]);
                Assert.Contains($"('{client}', 'Jira.JiraAnalyzedLabel',", patcher.Sql[1]);
            }
        }
    }

    private static Dictionary<(string Client, string Name), string> Values()
    {
        var values = new Dictionary<(string, string), string>
        {
            [("shared", "Jira.JiraBaseUrl")] = "https://example.atlassian.net",
            [("shared", "Jira.JiraEmail")] = "bot@example.com",
            [("shared", "Jira.JiraApiToken")] = "shared-jira-token",
            [("shared", "Jira.JiraProjectKey")] = "SHARED",
            [("shared", "Jira.JiraStatusName")] = "Ready",
            [("shared", "Jira.JiraJql")] = "priority = High",
            [("shared", "Jira.JiraFailureLabel")] = "shared-failed",
            [("shared", "Jira.JiraInProgressStatus")] = "Doing",
            [("shared", "Jira.JiraInReviewStatus")] = "Review",
            [("shared", "GitHub.AgentGitHubToken")] = "shared-copilot-token",
            [("shared", "GitHub.TargetRepositoryUrl")] = "https://github.com/owner/repo"
        };
        foreach (var client in Clients)
        {
            values[(client, "Jira.JiraPickupLabel")] = $"{client}-picked";
            values[(client, "Jira.JiraAnalyzedLabel")] = $"{client}-analyzed";
            values[(client, "GitHub.GitHubRepositoryToken")] = $"{client}-repository-token";
        }

        return values;
    }

    private sealed class SettingsPatcherStub(int failingPatch) : IDBPatcher
    {
        private bool _successful = true;
        public string? System { get; private set; }
        public List<string> Sql { get; } = [];
        public bool IsConnected() => true;
        public void Init(string system) => System = system;
        public bool Patch(string system, int patchId, string sql)
        {
            Assert.Equal(System, system);
            Sql.Add(sql);
            _successful = patchId != failingPatch;
            return _successful;
        }
        public bool Successful() => _successful;
    }
}
