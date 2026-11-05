using AiDashboard.Tasks;
using Microsoft.AspNetCore.Http;

namespace AiDashboard.Tests;

public sealed class JiraCommentEndpointTests
{
    private sealed class FakeClient(bool fail = false) : IJiraCommentClient
    {
        public List<(string Key, string Text)> Calls { get; } = [];

        public Task AddCommentAsync(string issueKey, string text, CancellationToken cancellationToken)
        {
            if (fail)
            {
                throw new HttpRequestException("boom");
            }

            Calls.Add((issueKey, text));
            return Task.CompletedTask;
        }
    }

    private static int Status(IResult result) => ((IStatusCodeHttpResult)result).StatusCode!.Value;

    [Fact]
    public async Task AppendsTrimmedComment()
    {
        var client = new FakeClient();
        var result = await JiraCommentEndpoint.HandleAsync(
            client, "DFAI-1", new JiraCommentRequest(" more "), CancellationToken.None);

        Assert.Equal(201, Status(result));
        Assert.Equal(("DFAI-1", "more"), Assert.Single(client.Calls));
    }

    [Theory]
    [InlineData("DFAI-1", null)]
    [InlineData("DFAI-1", "  ")]
    [InlineData("bad key", "x")]
    [InlineData("../x", "x")]
    public async Task RejectsInvalidInput(string key, string? text)
    {
        var client = new FakeClient();
        var result = await JiraCommentEndpoint.HandleAsync(
            client, key, new JiraCommentRequest(text), CancellationToken.None);

        Assert.Equal(400, Status(result));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task JiraFailureReturns502()
    {
        var result = await JiraCommentEndpoint.HandleAsync(
            new FakeClient(fail: true), "DFAI-1", new JiraCommentRequest("x"), CancellationToken.None);

        Assert.Equal(502, Status(result));
    }

    [Fact]
    public void BuildBodyCreatesAdfParagraphPerLine()
    {
        var body = JiraCommentClient.BuildBody("a\nb");

        Assert.Contains("\"type\":\"doc\"", body);
        Assert.Contains("\"text\":\"a\"", body);
        Assert.Contains("\"text\":\"b\"", body);
    }
}
