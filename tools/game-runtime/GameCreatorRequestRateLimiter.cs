using OpenMetaverse;

namespace OpenSim.GameRuntime;

/// <summary>
/// In-memory fixed-window per-principal limiter shared by all Creator API routes
/// in one Robust process. It is a local abuse guard, not a distributed quota system.
/// </summary>
public sealed class GameCreatorRequestRateLimiter
{
    private readonly object _sync = new();
    private readonly Dictionary<UUID, Window> _windows = new();
    private readonly int _requestsPerWindow;
    private readonly int _maximumTrackedPrincipals;
    private readonly TimeSpan _windowDuration;

    public GameCreatorRequestRateLimiter(
        int requestsPerWindow = 120,
        int maximumTrackedPrincipals = 10000,
        TimeSpan? windowDuration = null)
    {
        if (requestsPerWindow < 1)
            throw new ArgumentOutOfRangeException(nameof(requestsPerWindow));
        if (maximumTrackedPrincipals < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumTrackedPrincipals));

        _requestsPerWindow = requestsPerWindow;
        _maximumTrackedPrincipals = maximumTrackedPrincipals;
        _windowDuration = windowDuration ?? TimeSpan.FromMinutes(1);
        if (_windowDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(windowDuration));
    }

    public bool TryAcquire(UUID principalID) => TryAcquire(principalID, DateTimeOffset.UtcNow);

    public bool TryAcquire(UUID principalID, DateTimeOffset now)
    {
        if (principalID == UUID.Zero)
            return false;

        lock (_sync)
        {
            if (_windows.TryGetValue(principalID, out Window? current))
            {
                if (now < current.ResetAt)
                {
                    if (current.Count >= _requestsPerWindow)
                        return false;
                    current.Count++;
                    return true;
                }

                current.Count = 1;
                current.ResetAt = now + _windowDuration;
                return true;
            }

            if (_windows.Count >= _maximumTrackedPrincipals)
            {
                UUID[] expired = _windows
                    .Where(pair => now >= pair.Value.ResetAt)
                    .Select(pair => pair.Key)
                    .ToArray();
                foreach (UUID key in expired)
                    _windows.Remove(key);

                if (_windows.Count >= _maximumTrackedPrincipals)
                    return false;
            }

            _windows.Add(principalID, new Window(1, now + _windowDuration));
            return true;
        }
    }

    private sealed class Window
    {
        public Window(int count, DateTimeOffset resetAt) { Count = count; ResetAt = resetAt; }
        public int Count { get; set; }
        public DateTimeOffset ResetAt { get; set; }
    }
}
