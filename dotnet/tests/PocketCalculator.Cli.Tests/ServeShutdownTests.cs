using System.Diagnostics;
using System.Text;
using Xunit;

namespace PocketCalculator.Cli.Tests;

/// <summary>
/// Port of upstream <c>crates/obscura-cli/tests/serve_shutdown.rs</c> (4b14a76):
/// SIGTERM or SIGINT to the balancer stops its workers before it exits, and every
/// worker is started with the balancer's <c>--max-connections</c>.
/// </summary>
public sealed class ServeShutdownTests
{
    [Theory]
    [InlineData("-TERM")]
    [InlineData("-INT")]
    public void BalancerSignalStopsWorkersAndWorkersGetMaxConnections(string signal)
    {
        ServeProcess.RequireLinux();
        using var server = ServeProcess.Start(2, "--max-connections", "7");
        server.WaitReady(TimeSpan.FromSeconds(30));

        var pids = server.Workers();
        Assert.Equal(2, pids.Count);
        foreach (var pid in pids)
        {
            var argv = Encoding.UTF8.GetString(File.ReadAllBytes($"/proc/{pid}/cmdline")).Split('\0');
            var at = Array.IndexOf(argv, "--max-connections");
            Assert.True(
                at >= 0 && at + 1 < argv.Length && argv[at + 1] == "7",
                $"worker {pid} missing --max-connections 7: {string.Join(' ', argv)}");
        }

        // Signal only the balancer, not its workers.
        ServeProcess.Kill(signal, server.Process.Id);
        Assert.True(server.Process.WaitForExit(10_000), $"balancer ignored {signal}:\n{server.Log}");
        Assert.Equal(0, server.Process.ExitCode);

        var deadline = Stopwatch.GetTimestamp() + (10 * Stopwatch.Frequency);
        while (pids.Any(ServeProcess.Alive))
        {
            Assert.True(
                Stopwatch.GetTimestamp() < deadline,
                $"workers survived {signal}: [{string.Join(", ", pids)}]\n{server.Log}");
            Thread.Sleep(10);
        }
    }
}
