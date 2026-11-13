using System.Text.Json;
using AiAgent.Core;

namespace AiAgent.Capabilities.Jira;

public interface IPullRequestMergeChecker
{
    Task<bool> IsMergedAsync(string token, AgentPullRequestRecord pullRequest, CancellationToken cancellationToken);
}

/// <summary>Asks GitHub whether a pull request has been merged.</summary>
public sealed class GitHubMergeChecker(HttpMessageHandler? handler = null) : IPullRequestMergeChecker
{
    public async Task<bool> IsMergedAsync(
        string token, AgentPullRequestRecord pullRequest, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("GitHub.GitHubRepositoryToken is not set; cannot check pull requests.");
        }

        var repositoryUrl = new Uri(pullRequest.RepositoryUrl);
        var parts = repositoryUrl.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2 || !string.Equals(repositoryUrl.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The stored repository URL \"{repositoryUrl}\" is not in the form https://github.com/owner/repository.");
        }

        var name = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        using var httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        httpClient.BaseAddress = new Uri("https://api.github.com");
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DarkFactor-AiAgent/1.0");
        using var response = await httpClient.GetAsync(
            $"/repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(name)}/pulls/{pullRequest.PullRequestNumber}",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Reading pull request {pullRequest.PullRequestNumber} failed with status {(int)response.StatusCode}. " +
                "Verify that GitHub.GitHubRepositoryToken has the Pull requests: read permission.");
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.TryGetProperty("merged", out var merged) && merged.ValueKind == JsonValueKind.True;
    }
}
