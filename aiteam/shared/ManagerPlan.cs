using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiTeam.Configuration;

internal sealed record ManagerPlan(string Plan, bool PostedToJira, string? RequirementsHash = null, string? SourceUpdated = null)
{
    public bool CanImplement(string summary, string description, Uri repositoryUrl) =>
        PostedToJira && IsCurrent(summary, description, repositoryUrl);

    public bool IsCurrent(string summary, string description, Uri repositoryUrl) =>
        !string.IsNullOrWhiteSpace(Plan) &&
        string.Equals(RequirementsHash, Fingerprint(summary, description, repositoryUrl), StringComparison.Ordinal);

    public static string Fingerprint(string summary, string description, Uri repositoryUrl)
    {
        var repository = repositoryUrl.AbsoluteUri.TrimEnd('/');
        if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repository = repository[..^4];
        }
        var payload = JsonSerializer.Serialize(new[]
        {
            summary, description.Replace("\r\n", "\n", StringComparison.Ordinal), repository.ToLowerInvariant()
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
