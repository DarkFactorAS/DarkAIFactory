using System.Security.Cryptography;
using System.Text;
using DFCommonLib.DataAccess;

namespace AiTeam.Configuration;

public sealed class JiraIssueClaimRepository(IDbConnectionFactory connectionFactory)
{
    public IDisposable? TryAcquire(string issueKey)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(issueKey.ToUpperInvariant())));
        var command = connectionFactory.CreateCommand("SELECT GET_LOCK(@lock_name, 0)");
        command.AddParameter("@lock_name", $"jira-issue-{digest[..48]}");
        try
        {
            if (Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1)
            {
                return new IssueClaim(command);
            }

            command.Dispose();
            return null;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    private sealed class IssueClaim(IDisposable command) : IDisposable
    {
        public void Dispose() => command.Dispose();
    }
}
