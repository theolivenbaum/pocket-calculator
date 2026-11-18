using System.Text;
using PocketCalculator.Cli.Commands;
using Xunit;

namespace PocketCalculator.Cli.Tests;

/// <summary>
/// The unit tests of upstream <c>crates/obscura-cli/src/pool.rs</c> (0f27e69), in
/// order, plus the port's own checks of the session classifier and the options.
/// </summary>
public sealed class WorkerPoolTests
{
    private static readonly TimeSpan S = TimeSpan.FromSeconds(1);

    [Fact]
    public void DefaultPolicyNeverRecycles()
    {
        var policy = new RecyclePolicy();
        Assert.False(policy.Enabled);
        Assert.Null(policy.Reason(long.MaxValue, long.MaxValue, S * 100_000));
    }

    [Fact]
    public void ThresholdsTriggerAtTheLimit()
    {
        var policy = new RecyclePolicy(MaxConnections: 3, MaxRssMb: 500, MaxAgeSeconds: 60);
        Assert.Null(policy.Reason(2, 499, S * 59));
        Assert.Contains("connections", policy.Reason(3, 0, S), StringComparison.Ordinal);
        Assert.Contains("rss", policy.Reason(0, 500, S), StringComparison.Ordinal);
        Assert.Contains("age", policy.Reason(0, 0, S * 60), StringComparison.Ordinal);
        // An unreadable RSS never triggers the RSS limit.
        Assert.Null(policy.Reason(0, null, S));
    }

    /// <summary>The reasons are logged; their text is upstream's.</summary>
    [Fact]
    public void ReasonsReadAsUpstreamFormatsThem()
    {
        var policy = new RecyclePolicy(MaxConnections: 3, MaxRssMb: 500, MaxAgeSeconds: 60);
        Assert.Equal("served 4 connections (limit 3)", policy.Reason(4, 0, S));
        Assert.Equal("rss 512 MB (limit 500 MB)", policy.Reason(0, 512, S));
        Assert.Equal("age 61s (limit 60s)", policy.Reason(0, 0, TimeSpan.FromSeconds(61.9)));
        Assert.Equal(
            "RecyclePolicy { max_connections: Some(3), max_rss_mb: Some(500), max_age: Some(60s) }",
            policy.ToString());
        Assert.Equal(
            "RecyclePolicy { max_connections: None, max_rss_mb: None, max_age: None }",
            new RecyclePolicy().ToString());
    }

    private static PoolSlot[] Pool(int n, int? max)
    {
        var slots = new PoolSlot[n];
        for (var i = 0; i < n; i++)
        {
            var wake = new WakeSignal();
            slots[i] = new PoolSlot(new PoolWorker(9000 + i, max, wake), wake);
        }
        return slots;
    }

    [Fact]
    public void PickRoundRobinsAndSkipsUnready()
    {
        var slots = Pool(3, null);
        ulong next = 0;
        var ports = new int[3];
        for (var i = 0; i < 3; i++)
        {
            ports[i] = WorkerPool.Pick(slots, ref next)!.Port;
        }
        Assert.Equal([9000, 9001, 9002], ports);
        slots[1].Current.Ready = false;
        for (var i = 0; i < 6; i++)
        {
            Assert.NotEqual(9001, WorkerPool.Pick(slots, ref next)!.Port);
        }
        foreach (var slot in slots)
        {
            slot.Current.Ready = false;
        }
        Assert.Null(WorkerPool.Pick(slots, ref next));
    }

    [Fact]
    public void ReplacedWorkerDrainsAndGetsNoNewConnections()
    {
        var slots = Pool(2, 2);
        ulong next = 0;
        var held = WorkerPool.Pick(slots, ref next)!;
        var old = slots[0].Current;
        Assert.Equal(1, old.Active);

        var fresh = new PoolWorker(9100, 2, slots[0].Wake);
        var draining = slots[0].Replace(fresh);
        Assert.Same(old, draining);
        for (var i = 0; i < 8; i++)
        {
            Assert.NotEqual(9000, WorkerPool.Pick(slots, ref next)!.Port);
        }
        // The live connection keeps the old worker busy until it finishes.
        Assert.Equal(1, draining.Active);
        held.Dispose();
        Assert.Equal(0, draining.Active);
        // Capacity stays at two ready slots.
        Assert.Equal(2, slots.Length);
        Assert.All(slots, slot => Assert.NotEqual(9000, slot.Current.Port));
    }

    [Fact]
    public void SessionCountWakesSupervisorAtLimitOnly()
    {
        var slots = Pool(1, 2);
        ulong next = 0;
        var first = WorkerPool.Pick(slots, ref next)!;
        first.CountSession();
        var wake = slots[0].Wake;
        // Not woken yet: a stored permit would complete the notified task.
        Assert.False(wake.IsSet);
        var second = WorkerPool.Pick(slots, ref next)!;
        second.CountSession();
        Assert.True(wake.IsSet);
        Assert.Equal(2, slots[0].Current.Served);
        // Discovery connections are active but never counted.
        var discovery = WorkerPool.Pick(slots, ref next)!;
        Assert.Equal(3, slots[0].Current.Active);
        discovery.Dispose();
        Assert.Equal(2, slots[0].Current.Served);
    }

    [Fact]
    public void RssOfSelfIsReadableOnLinux()
    {
        if (OperatingSystem.IsLinux())
        {
            Assert.True(WorkerPool.RssMb(Environment.ProcessId) > 0);
        }
        else
        {
            Assert.Null(WorkerPool.RssMb(Environment.ProcessId));
        }
    }

    /// <summary>A consumed wake waits again; a later notification is kept for the next wait.</summary>
    [Fact]
    public void WakeSignalKeepsOnePermit()
    {
        var wake = new WakeSignal();
        wake.Notify();
        wake.Notify();
        Assert.True(wake.Notified.IsCompleted);
        wake.Consume();
        Assert.False(wake.Notified.IsCompleted);
        wake.Notify();
        Assert.True(wake.Notified.IsCompleted);
    }

    /// <summary>A connection's release is counted once, however often it is disposed.</summary>
    [Fact]
    public void ConnectionReleasesOnce()
    {
        var slots = Pool(1, null);
        ulong next = 0;
        var connection = WorkerPool.Pick(slots, ref next)!;
        connection.Dispose();
        connection.Dispose();
        Assert.Equal(0, slots[0].Current.Active);
    }

    /// <summary>Upstream's classifier: `GET ` first and `/json` in the first 256 bytes.</summary>
    [Theory]
    [InlineData("GET /json/version HTTP/1.1\r\nHost: x\r\n\r\n", true)]
    [InlineData("GET /json HTTP/1.1\r\n", true)]
    [InlineData("GET /devtools/browser HTTP/1.1\r\nUpgrade: websocket\r\n\r\n", false)]
    [InlineData("POST /json/new HTTP/1.1\r\n", false)]
    [InlineData("GE", false)]
    public void DiscoveryRequestsAreNotSessions(string request, bool discovery) =>
        Assert.Equal(discovery, ServeCommand.IsDiscoveryRequest(Encoding.ASCII.GetBytes(request)));

    [Fact]
    public void JsonPastTheFirst256BytesIsASession()
    {
        var request = "GET /" + new string('a', 300) + "/json HTTP/1.1\r\n";
        Assert.False(ServeCommand.IsDiscoveryRequest(Encoding.ASCII.GetBytes(request)));
    }

    /// <summary>Upstream refuses the recycle options without a pool to recycle.</summary>
    [Fact]
    public void RecycleOptionsRequireWorkers()
    {
        Assert.True(CliProcess.SkipReason is null, CliProcess.SkipReason ?? string.Empty);
        var run = CliProcess.Run("serve", "--port", "0", "--worker-max-connections", "3");
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Error: --worker-max-* options require --workers > 1", run.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void RecycleOptionsParse()
    {
        Assert.True(CliArgs_TryParse(
            ["serve", "--workers", "2", "--worker-max-connections", "3", "--worker-max-rss", "500",
                "--worker-max-age", "60", "--worker-drain-timeout", "5"],
            out var serve));
        Assert.Equal(3, serve.WorkerMaxConnections);
        Assert.Equal(500, serve.WorkerMaxRss);
        Assert.Equal(60, serve.WorkerMaxAge);
        Assert.Equal(5, serve.WorkerDrainTimeout);

        Assert.True(CliArgs_TryParse(["serve"], out var defaults));
        Assert.Null(defaults.WorkerMaxConnections);
        Assert.Null(defaults.WorkerMaxRss);
        Assert.Null(defaults.WorkerMaxAge);
        Assert.Equal(120, defaults.WorkerDrainTimeout);
    }

    private static bool CliArgs_TryParse(string[] args, out PocketCalculator.Cli.CommandLine.CliCommand.Serve serve)
    {
        var parsed = PocketCalculator.Cli.CommandLine.CliArgs.TryParseFrom(["obscura", .. args], out var errors);
        Assert.Empty(errors);
        serve = Assert.IsType<PocketCalculator.Cli.CommandLine.CliCommand.Serve>(parsed!.Command);
        return true;
    }
}
