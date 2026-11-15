using System.Data;
using System.Globalization;
using AiAgent.Core;
using DFCommonLib.DataAccess;
using DFCommonLib.Logger;

namespace AiAgent.Repositories;

/// <summary>
/// MySQL/MariaDB named lock (GET_LOCK). A named lock belongs to the database session that took it, so each lock gets
/// its own connection. Disposing the handle runs RELEASE_LOCK on that same connection and then closes it. Disposing
/// the command alone is not enough: a pooled or shared session keeps the lock, and other processes stay locked out.
/// </summary>
internal static class MySqlNamedLock
{
    private const string Source = "NamedLock";

    /// <summary>Returns a handle that releases the lock on dispose, or null when another session holds it.</summary>
    public static IDisposable? TryAcquire(IDbConnectionFactory connectionFactory, string lockName)
    {
        var connection = connectionFactory.CreateConnection();
        IBluDbCommand? acquire = null;
        try
        {
            if (connection.State != ConnectionState.Open)
            {
                connection.Open();
            }

            acquire = connectionFactory.CreateCommand("SELECT GET_LOCK(@lock_name, 0)", connection);
            acquire.AddParameter("@lock_name", lockName);
            if (ToInt(acquire.ExecuteScalar()) == 1)
            {
                // The acquire command stays alive until release, in case disposing it would close the connection.
                return new Handle(connectionFactory, connection, acquire, lockName);
            }
        }
        catch
        {
            acquire?.Dispose();
            connection.Dispose();
            throw;
        }

        acquire.Dispose();
        connection.Dispose();
        return null;
    }

    private static int? ToInt(object? value) =>
        value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);

    private sealed class Handle(
        IDbConnectionFactory connectionFactory, IDbConnection connection, IBluDbCommand acquire, string lockName)
        : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            try
            {
                using var release = connectionFactory.CreateCommand("SELECT RELEASE_LOCK(@lock_name)", connection);
                release.AddParameter("@lock_name", lockName);
                var released = ToInt(release.ExecuteScalar());
                if (released != 1)
                {
                    // 0: the lock is owned by another session; NULL: it did not exist. Closing the connection below
                    // still frees a lock this session holds.
                    AgentLog.LogOutput(DFLogLevel.WARNING, Source,
                        $"RELEASE_LOCK for {lockName} returned {released?.ToString(CultureInfo.InvariantCulture) ?? "NULL"}.");
                }
            }
            catch (Exception exception)
            {
                AgentLog.LogOutput(DFLogLevel.WARNING, Source,
                    $"Could not release {lockName}: {exception.Message}. Closing its connection instead.");
            }
            finally
            {
                acquire.Dispose();
                connection.Dispose();
            }
        }
    }
}
