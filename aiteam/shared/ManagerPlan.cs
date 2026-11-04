using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiTeam.Configuration;

internal sealed record ManagerPlan(
    string Plan,
    bool PostedToJira,
    string? RequirementsHash = null,
    string? SourceUpdated = null,
    long? ManagerProjectId = null)
{
    public bool CanImplement(string summary, string description, Uri gitHubRepositoryUrl) =>
        PostedToJira && IsCurrent(summary, description, gitHubRepositoryUrl);

    public bool IsCurrent(string summary, string description, Uri gitHubRepositoryUrl) =>
        !string.IsNullOrWhiteSpace(Plan) &&
        string.Equals(RequirementsHash, Fingerprint(summary, description, gitHubRepositoryUrl), StringComparison.Ordinal);

    public static string Fingerprint(string summary, string description, Uri gitHubRepositoryUrl)
    {
        var normalizedGitHubRepositoryUrl = gitHubRepositoryUrl.AbsoluteUri.TrimEnd('/');
        if (normalizedGitHubRepositoryUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            normalizedGitHubRepositoryUrl = normalizedGitHubRepositoryUrl[..^4];
        }
        var payload = JsonSerializer.Serialize(new[]
        {
            summary, description.Replace("\r\n", "\n", StringComparison.Ordinal), normalizedGitHubRepositoryUrl.ToLowerInvariant()
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
