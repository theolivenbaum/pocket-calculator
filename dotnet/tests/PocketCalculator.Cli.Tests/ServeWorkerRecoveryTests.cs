using System.Diagnostics;
using Xunit;

namespace PocketCalculator.Cli.Tests;

/// <summary>
/// Port of upstream <c>crates/obscura-cli/tests/serve_worker_recovery.rs</c>
/// (f2b9e99): the balancer reaps a worker that dies, restarts it, and routes
/// discovery only to workers that are up.
/// </summary>
[Collection(ServeProcess.Collection)]
public sealed class ServeWorkerRecoveryTests
{
    [Fact]
    public void KilledWorkersAreReapedAndReplacedWithoutPoisoningDiscovery()
    {
        ServeProcess.RequireLinux();
        using var server = ServeProcess.Start(2);
        server.WaitReady(TimeSpan.FromSeconds(30));

        foreach (var killAll in new[] { false, true })
        {
            var original = server.Workers();
            Assert.Equal(2, original.Count);
            var victims = killAll ? original : original[..1];
            foreach (var victim in victims)
            {
                ServeProcess.Kill("-KILL", victim);
            }

            // Upstream allows 5 s; a .NET worker starts slower than the Rust one,
            // and the replacement is also verified before it takes traffic.
            var deadline = Stopwatch.GetTimestamp() + (30 * Stopwatch.Frequency);
            while (true)
            {
                var current = server.Workers();
                if (current.Count == 2 && victims.All(id => !current.Contains(id)) && server.DiscoveryOk())
                {
                    break;
                }
                Assert.True(
                    Stopwatch.GetTimestamp() < deadline,
                    $"dead workers were not reaped and replaced: [{string.Join(", ", current)}]\n{server.Log}");
                Thread.Sleep(10);
            }
            for (var i = 0; i < 12; i++)
            {
                Assert.StartsWith("HTTP/1.1 200", server.Discovery(), StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// With every worker down, a client gets upstream's 503 instead of being
    /// routed to a dead port.
    /// </summary>
    [Fact]
    public void NoReadyWorkerAnswers503()
    {
        ServeProcess.RequireLinux();
        using var server = ServeProcess.Start(2);
        server.WaitReady(TimeSpan.FromSeconds(30));

        var sawRefusal = false;
        var deadline = Stopwatch.GetTimestamp() + (10 * Stopwatch.Frequency);
        while (!sawRefusal && Stopwatch.GetTimestamp() < deadline)
        {
            foreach (var pid in server.Workers())
            {
                ServeProcess.Kill("-KILL", pid);
            }
            // The supervisors wait 100 ms before restarting, and a replacement
            // takes far longer than that to listen.
            Thread.Sleep(50);
            try
            {
                var reply = server.Discovery();
                sawRefusal = reply == "HTTP/1.1 503 Service Unavailable\r\nConnection: close\r\n\r\n";
            }
            catch (IOException)
            {
            }
        }
        Assert.True(sawRefusal, server.Log);
        server.WaitReady(TimeSpan.FromSeconds(30));
    }
}
