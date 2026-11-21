using AccountClientModule.Client;
using AccountCommon.SharedModel;
using DFCommonLib.Config;
using DFCommonLib.Logger;
using DFCommonLib.Utils;

namespace AiDashboard.Auth;

/// <summary>Account server connection, same shape as DFWeb's <c>AccountServer</c> section.</summary>
public sealed class AccountServerSettings
{
    public string? Endpoint { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? Scope { get; set; }
}

/// <summary>Dashboard appsettings: the shared DarkFactor settings plus the account server.</summary>
public sealed class DashboardAppSettings : AppSettings
{
    public AccountServerSettings? AccountServer { get; set; }
}

public enum DashboardLoginStatus
{
    Ok,
    InvalidCredentials,
    NotConfigured,
    Unavailable,
}

public sealed record DashboardLoginResult(DashboardLoginStatus Status, string? Username = null, uint AccountId = 0);

public interface IDashboardLogin
{
    Task<DashboardLoginResult> LoginAsync(string username, string password, CancellationToken cancellationToken);
}

/// <summary>
/// Checks credentials against the DarkFactor account server with the DFCommonLib account client,
/// the same way DFWeb does (password encrypted with <see cref="DFCrypt.EncryptInput"/>).
/// Any valid account is accepted. The password and the account token are never logged or stored.
/// </summary>
public sealed class AccountServerLogin(IAccountClient accountClient, IConfigurationHelper configuration) : IDashboardLogin
{
    public async Task<DashboardLoginResult> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        var server = (configuration.Settings as DashboardAppSettings)?.AccountServer;
        if (server is null || string.IsNullOrWhiteSpace(server.Endpoint))
        {
            DFLogger.LogOutput(DFLogLevel.ERROR, "Login", "AccountServer.Endpoint is not configured.");
            return new DashboardLoginResult(DashboardLoginStatus.NotConfigured);
        }

        AccountData? data;
        try
        {
            accountClient.SetEndpoint(server.Endpoint);
            accountClient.SetAuthCredentials(server.ClientId, server.ClientSecret, server.Scope);
            var loginData = new LoginData { username = username, password = DFCrypt.EncryptInput(password) };
            // The account client API is synchronous; keep it off the request thread.
            data = await Task.Run(() => accountClient.LoginAccount(loginData), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DFLogger.LogOutput(DFLogLevel.ERROR, "Login", $"Account server login failed: {ex.GetType().Name}");
            return new DashboardLoginResult(DashboardLoginStatus.Unavailable);
        }

        if (data is null)
        {
            return new DashboardLoginResult(DashboardLoginStatus.Unavailable);
        }

        return data.errorCode == AccountData.ErrorCode.OK
            ? new DashboardLoginResult(
                DashboardLoginStatus.Ok,
                string.IsNullOrWhiteSpace(data.nickname) ? username : data.nickname,
                data.id)
            : new DashboardLoginResult(DashboardLoginStatus.InvalidCredentials);
    }
}
