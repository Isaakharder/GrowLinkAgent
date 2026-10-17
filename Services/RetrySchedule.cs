using System.Collections.Concurrent;

namespace FlowMasterAgent.Services;

/// <summary>
/// Files kept in place after a temporary failure, and when each may be tried again.
/// The delay grows with each consecutive failure (1, 2, 5, 10, 15 min, then every 15 min)
/// so an outage isn't hammered, and a file recovers on its own once the server is back.
/// </summary>
public sealed class RetrySchedule
{
    public static readonly TimeSpan[] DefaultBackoff =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15)];

    private sealed record Entry(int Failures, DateTime NextAttemptUtc, string Reason);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan[] _backoff;

    public RetrySchedule(TimeSpan[]? backoff = null) => _backoff = backoff is { Length: > 0 } ? backoff : DefaultBackoff;

    /// <summary>Records a temporary failure; returns when the file may be tried again.</summary>
    public DateTime RecordFailure(string path, string reason, DateTime nowUtc, TimeSpan? notBefore = null)
    {
        var failures = _entries.TryGetValue(path, out var e) ? e.Failures + 1 : 1;
        var delay = _backoff[Math.Min(failures - 1, _backoff.Length - 1)];
        if (notBefore.HasValue && notBefore.Value > delay) delay = notBefore.Value;
        var next = nowUtc + delay;
        _entries[path] = new Entry(failures, next, reason);
        return next;
    }

    public void Clear(string path) => _entries.TryRemove(path, out _);

    /// <summary>A file not in the schedule is always due.</summary>
    public bool IsDue(string path, DateTime nowUtc) => !_entries.TryGetValue(path, out var e) || e.NextAttemptUtc <= nowUtc;

    /// <summary>Makes every waiting file due now (the "Retry now" button).</summary>
    public void MakeAllDue(DateTime nowUtc)
    {
        foreach (var key in _entries.Keys)
            if (_entries.TryGetValue(key, out var e))
                _entries[key] = e with { NextAttemptUtc = nowUtc };
    }

    /// <summary>Forgets files that no longer exist (moved or deleted elsewhere).</summary>
    public void Prune()
    {
        foreach (var key in _entries.Keys)
            if (!File.Exists(key)) _entries.TryRemove(key, out _);
    }

    public int Count => _entries.Count;

    public (DateTime NextUtc, string Reason)? Next =>
        _entries.Values.OrderBy(e => e.NextAttemptUtc).Select(e => ((DateTime, string)?)(e.NextAttemptUtc, e.Reason)).FirstOrDefault();
}
