using AiAgent.Core;

namespace AiAgent.Tests;

/// <summary>Exclusive claim fake that records every acquire and release, to prove capabilities release their claims.</summary>
internal sealed class TrackingClaimLock : IAgentClaimLock
{
    private readonly object _gate = new();
    private readonly HashSet<string> _held = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Acquired { get; } = [];
    public List<string> Released { get; } = [];

    public IReadOnlyCollection<string> Held
    {
        get { lock (_gate) return [.. _held]; }
    }

    public IDisposable? TryAcquire(long agentId, string issueKey)
    {
        lock (_gate)
        {
            if (!_held.Add(issueKey))
            {
                return null;
            }

            Acquired.Add(issueKey);
            return new Handle(this, issueKey);
        }
    }

    /// <summary>Asserts at least one claim was taken and every claim was released.</summary>
    public void AssertAllReleased()
    {
        Assert.NotEmpty(Acquired);
        Assert.Empty(Held);
        Assert.Equal(Acquired.Order(), Released.Order());
    }

    private sealed class Handle(TrackingClaimLock owner, string issueKey) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            lock (owner._gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                owner._held.Remove(issueKey);
                owner.Released.Add(issueKey);
            }
        }
    }
}
