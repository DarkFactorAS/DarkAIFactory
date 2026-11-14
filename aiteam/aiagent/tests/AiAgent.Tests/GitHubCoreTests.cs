using System.Net;
using System.Text;
using AiAgent.Core;
using AiAgent.GitHub;
using AiAgent.Repositories;

namespace AiAgent.Tests;

public sealed class GitHubCoreTests
{
    private sealed class RecordingGit(string stagedOutput = "src/a.cs\0", int diffExit = 1) : IGitRunner
    {
        public List<string[]> Calls { get; } = [];
        public Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, bool withCredentials,
            CancellationToken cancellationToken, bool allowExitCodeOne = false)
        {
            Calls.Add([.. arguments]);
            if (arguments[0] == "diff" && arguments.Contains("--name-only")) return Task.FromResult(new GitResult(0, stagedOutput, ""));
            if (arguments[0] == "diff") return Task.FromResult(new GitResult(diffExit, "", ""));
            return Task.FromResult(new GitResult(0, "", ""));
        }
    }

    private sealed class Recorder : IAgentPullRequestRepository
    {
        public List<AgentPullRequestRecord> Items { get; } = [];
        public void Record(AgentPullRequestRecord pullRequest) => Items.Add(pullRequest);
        public IReadOnlyList<AgentPullRequestRecord> GetForAgent(long agentId) => Items;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            var json = request.Method == HttpMethod.Get
                ? "{\"default_branch\":\"main\"}"
                : "{\"html_url\":\"https://github.com/o/r/pull/7\",\"number\":7}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static GitHubCore Create(IGitRunner git, Recorder recorder, HttpMessageHandler? handler = null) =>
        new(git, new Uri("https://github.com/o/r.git"), "token", recorder, handler);

    [Fact]
    public void BranchNameUsesAiagentPrefixAndKey()
    {
        var name = GitHubCore.BranchName("DFAI-166", "Core: AgentHost & claim lock!", "abc");
        Assert.Equal("aiagent/DFAI-166-core-agenthost-claim-lock-abc", name);
    }

    [Fact]
    public void TitleStartsWithTheJiraKey()
    {
        Assert.Equal("DFAI-166: Do it", GitHubCore.PullRequestTitle("DFAI-166", "Do \n it"));
        Assert.Equal("DFAI-166", GitHubCore.PullRequestTitle("DFAI-166", "  "));
    }

    [Fact]
    public async Task CloneUsesSubmodulesAndCreatesBranch()
    {
        var git = new RecordingGit();
        var path = Path.Combine(Path.GetTempPath(), "aiagent-test-" + Guid.NewGuid().ToString("N"), "checkout");
        var branch = await Create(git, new Recorder()).CloneAndBranchAsync(path, "DFAI-1", "Hello", null, default);
        Assert.StartsWith("aiagent/DFAI-1-hello-", branch);
        Assert.Contains("--recurse-submodules", git.Calls[0]);
        Assert.Equal("switch", git.Calls[1][0]);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public async Task PushIsNeverForced()
    {
        var git = new RecordingGit();
        var result = await Create(git, new Recorder()).CommitAndPushAsync("/x", "aiagent/K-1", "K-1", "sum", default);
        Assert.True(result.Pushed);
        var push = Assert.Single(git.Calls, c => c[0] == "push");
        Assert.DoesNotContain(push, a => a.Contains("force", StringComparison.OrdinalIgnoreCase) || a == "-f" || a.StartsWith('+'));
        Assert.Contains(git.Calls, c => c[0] == "commit");
    }

    [Fact]
    public async Task NothingStagedMeansNoCommitOrPush()
    {
        var git = new RecordingGit(stagedOutput: "", diffExit: 0);
        var result = await Create(git, new Recorder()).CommitAndPushAsync("/x", "b", "K-1", "sum", default);
        Assert.False(result.Pushed);
        Assert.DoesNotContain(git.Calls, c => c[0] is "commit" or "push");
    }

    [Fact]
    public async Task UserSettingsAreRefused() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create(new RecordingGit("UserSettings/x\0"), new Recorder()).CommitAndPushAsync("/x", "b", "K-1", "s", default));

    [Fact]
    public async Task OpenPullRequestPostsTitleAndRecordsIt()
    {
        var handler = new StubHandler();
        var recorder = new Recorder();
        var record = await Create(new RecordingGit(), recorder, handler)
            .OpenPullRequestAsync(5, "DFAI-166", "Core", "aiagent/DFAI-166-core-abc", null, "body", default);
        Assert.Equal(7, record.PullRequestNumber);
        Assert.Equal(record, Assert.Single(recorder.Items));
        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal("/repos/o/r/pulls", post.Path);
        Assert.Contains("\"title\":\"DFAI-166: Core\"", post.Body);
        Assert.Contains("\"base\":\"main\"", post.Body);
    }
}
