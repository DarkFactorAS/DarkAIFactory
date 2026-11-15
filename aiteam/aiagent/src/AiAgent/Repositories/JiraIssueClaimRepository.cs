using System.Security.Cryptography;
using System.Text;
using DFCommonLib.DataAccess;

namespace AiAgent.Repositories;

/// <summary>Named lock per Jira issue; disposing the handle releases it (see <see cref="MySqlNamedLock"/>).</summary>
public sealed class JiraIssueClaimRepository(IDbConnectionFactory connectionFactory)
{
    public IDisposable? TryAcquire(string issueKey)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(issueKey.ToUpperInvariant())));
        return MySqlNamedLock.TryAcquire(connectionFactory, $"jira-issue-{digest[..48]}");
    }
}
