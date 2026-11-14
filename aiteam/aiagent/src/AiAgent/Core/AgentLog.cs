using DFCommonLib.Logger;

namespace AiAgent.Core;

/// <summary>
/// Logging that prefixes each message with the name of the agent running in the current async flow,
/// as <c>[AgentName] message</c>. Outside an agent scope the message is written unchanged.
/// </summary>
public static class AgentLog
{
    private static readonly AsyncLocal<string?> CurrentName = new();

    /// <summary>The agent name of the current async flow, or null outside an agent scope.</summary>
    public static string? Current => CurrentName.Value;

    /// <summary>Sets the agent name for the current async flow until the returned scope is disposed.</summary>
    public static IDisposable BeginScope(string? agentName)
    {
        var previous = CurrentName.Value;
        CurrentName.Value = agentName;
        return new Scope(previous);
    }

    public static void LogOutput(DFLogLevel level, string group, string message) =>
        DFLogger.LogOutput(level, group, Format(CurrentName.Value, message));

    internal static string Format(string? name, string message) =>
        string.IsNullOrWhiteSpace(name) ? message : $"[{name}] {message}";

    private sealed class Scope(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CurrentName.Value = previous;
        }
    }
}
