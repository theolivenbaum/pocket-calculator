using System.Diagnostics;
using System.Globalization;

namespace PocketCalculator.Cli.Commands;

/// <summary>
/// When a <c>serve --workers</c> worker should be replaced (upstream 0f27e69,
/// <c>pool.rs</c> <c>RecyclePolicy</c>). Every limit is off by default.
/// </summary>
/// <param name="MaxConnections"><c>--worker-max-connections</c>: CDP sessions served.</param>
/// <param name="MaxRssMb"><c>--worker-max-rss</c>: resident memory, in MB.</param>
/// <param name="MaxAgeSeconds"><c>--worker-max-age</c>: seconds since the worker started.</param>
public sealed record RecyclePolicy(int? MaxConnections = null, long? MaxRssMb = null, long? MaxAgeSeconds = null)
{
    /// <summary>True when any limit is set.</summary>
    public bool Enabled => MaxConnections is not null || MaxRssMb is not null || MaxAgeSeconds is not null;

    /// <summary>
    /// The first threshold that is reached, if any. <paramref name="rssMb"/> is null
    /// when it could not be sampled (not Linux, process gone), which never recycles.
    /// </summary>
    public string? Reason(long served, long? rssMb, TimeSpan age)
    {
        if (MaxConnections is { } maxConnections && served >= maxConnections)
        {
            return string.Create(
                CultureInfo.InvariantCulture, $"served {served} connections (limit {maxConnections})");
        }
        if (MaxRssMb is { } maxRss && rssMb is { } rss && rss >= maxRss)
        {
            return string.Create(CultureInfo.InvariantCulture, $"rss {rss} MB (limit {maxRss} MB)");
        }
        // Rust compares Durations; the limit is whole seconds, so comparing the
        // truncated age in seconds is the same test, and a limit too large for a
        // TimeSpan cannot overflow.
        var ageSeconds = (long)age.TotalSeconds;
        if (MaxAgeSeconds is { } maxAge && ageSeconds >= maxAge)
        {
            return string.Create(CultureInfo.InvariantCulture, $"age {ageSeconds}s (limit {maxAge}s)");
        }
        return null;
    }

    /// <summary>Rust's <c>{:?}</c> rendering, for the startup log line.</summary>
    public override string ToString()
    {
        static string Some<T>(T? value, string suffix = "")
            where T : struct =>
            value is { } v ? string.Create(CultureInfo.InvariantCulture, $"Some({v}{suffix})") : "None";
        return $"RecyclePolicy {{ max_connections: {Some(MaxConnections)}, max_rss_mb: {Some(MaxRssMb)}, "
            + $"max_age: {Some(MaxAgeSeconds, "s")} }}";
    }
}

/// <summary>
/// A one-shot wake-up with a stored permit, standing in for tokio's <c>Notify</c>
/// with <c>notify_one</c>: a notification with no one waiting is kept until the
/// next wait.
/// </summary>
public sealed class WakeSignal
{
    private TaskCompletionSource current = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Wake the waiter, or store the permit for the next one.</summary>
    public void Notify() => Volatile.Read(ref current).TrySetResult();

    /// <summary>Completes once notified; call <see cref="Consume"/> after it does.</summary>
    public Task Notified => Volatile.Read(ref current).Task;

    /// <summary>True when a notification is pending.</summary>
    public bool IsSet => Volatile.Read(ref current).Task.IsCompleted;

    /// <summary>Take a pending notification, so the next <see cref="Notified"/> waits again.</summary>
    public void Consume()
    {
        var seen = Volatile.Read(ref current);
        if (seen.Task.IsCompleted)
        {
            Interlocked.CompareExchange(
                ref current, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), seen);
        }
    }
}

/// <summary>
/// One worker process generation: the port it listens on and its live load
/// (upstream <c>pool.rs</c> <c>Worker</c>).
/// </summary>
public sealed class PoolWorker(int port, int? maxConnections, WakeSignal wake)
{
    private readonly long born = Stopwatch.GetTimestamp();
    private volatile bool ready = true;
    private long active;
    private long served;

    public int Port { get; } = port;

    /// <summary>Time since this worker generation was created.</summary>
    public TimeSpan Age => Stopwatch.GetElapsedTime(born);

    public bool Ready
    {
        get => ready;
        set => ready = value;
    }

    /// <summary>Live proxied connections, discovery requests included.</summary>
    public long Active => Interlocked.Read(ref active);

    /// <summary>CDP sessions counted against <c>--worker-max-connections</c>.</summary>
    public long Served => Interlocked.Read(ref served);

    /// <summary>A live proxied connection; released on dispose.</summary>
    public PoolConnection Connect()
    {
        Interlocked.Increment(ref active);
        return new PoolConnection(this);
    }

    internal void Release() => Interlocked.Decrement(ref active);

    internal void CountSession()
    {
        var count = Interlocked.Increment(ref served);
        if (maxConnections is { } max && count >= max)
        {
            wake.Notify();
        }
    }
}

/// <summary>One proxied connection to a worker (upstream <c>Connection</c>).</summary>
public sealed class PoolConnection : IDisposable
{
    private PoolWorker? worker;

    internal PoolConnection(PoolWorker worker)
    {
        this.worker = worker;
        Port = worker.Port;
    }

    public int Port { get; }

    /// <summary>
    /// Count this as a CDP session (not a <c>/json</c> discovery request) and wake
    /// the supervisor when the connection limit is reached.
    /// </summary>
    public void CountSession() => worker?.CountSession();

    public void Dispose() => Interlocked.Exchange(ref worker, null)?.Release();
}

/// <summary>
/// One pool position (upstream <c>Slot</c>). It always holds the worker that takes
/// new connections; a replaced worker is no longer reachable from here, which is
/// what drains it.
/// </summary>
public sealed class PoolSlot(PoolWorker first, WakeSignal wake)
{
    private PoolWorker current = first;

    public WakeSignal Wake { get; } = wake;

    public PoolWorker Current => Volatile.Read(ref current);

    /// <summary>Route new connections to <paramref name="next"/>; returns the worker that now drains.</summary>
    public PoolWorker Replace(PoolWorker next) => Interlocked.Exchange(ref current, next);
}

/// <summary>Routing over the pool (upstream <c>pool.rs</c> free functions).</summary>
public static class WorkerPool
{
    /// <summary>Round robin over ready workers, skipping crashed ones.</summary>
    /// <remarks>Rust advances a usize with wrapping_add; a ulong wraps the same way.</remarks>
    public static PoolConnection? Pick(IReadOnlyList<PoolSlot> slots, ref ulong next)
    {
        for (var i = 0; i < slots.Count; i++)
        {
            var worker = slots[(int)(next % (ulong)slots.Count)].Current;
            next = unchecked(next + 1);
            if (worker.Ready)
            {
                return worker.Connect();
            }
        }
        return null;
    }

    /// <summary>Resident set size of <paramref name="pid"/> in MB, from <c>/proc</c>. Null off Linux.</summary>
    public static long? RssMb(int pid)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }
        string status;
        try
        {
            status = File.ReadAllText(string.Create(CultureInfo.InvariantCulture, $"/proc/{pid}/status"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        foreach (var line in status.AsSpan().EnumerateLines())
        {
            if (!line.StartsWith("VmRSS:", StringComparison.Ordinal))
            {
                continue;
            }
            var rest = line["VmRSS:".Length..].TrimStart();
            var end = rest.IndexOfAny(' ', '\t');
            var digits = end < 0 ? rest : rest[..end];
            return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var kb)
                ? kb / 1024
                : null;
        }
        return null;
    }
}
