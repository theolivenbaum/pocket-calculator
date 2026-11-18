using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace PocketCalculator.Cli.Tests;

/// <summary>
/// A `serve --workers N` balancer run as the built CLI, for the Linux-only worker
/// pool tests ported from upstream's <c>serve_*.rs</c>. Workers are found the way
/// those tests find them, through <c>/proc/&lt;pid&gt;/task/*/children</c>.
/// </summary>
internal sealed class ServeProcess : IDisposable
{
    /// <summary>
    /// The test collection these share, so the worker pools they start, each a few
    /// .NET processes with V8, run one after another rather than side by side.
    /// </summary>
    public const string Collection = "serve worker pool";

    private readonly StringBuilder log = new();

    private ServeProcess(Process process, int port)
    {
        Process = process;
        Port = port;
    }

    public Process Process { get; }

    public int Port { get; }

    /// <summary>The balancer's and its workers' stderr so far, for failure messages.</summary>
    public string Log
    {
        get
        {
            lock (log)
            {
                return log.ToString();
            }
        }
    }

    /// <summary>Skip on anything but Linux, which the upstream tests are gated to.</summary>
    public static void RequireLinux()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "reads /proc, as the upstream tests do");
        Assert.True(CliProcess.SkipReason is null, CliProcess.SkipReason ?? string.Empty);
    }

    public static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    /// <summary>Start <c>serve --host 127.0.0.1 --port P --workers N</c> plus <paramref name="extra"/>.</summary>
    public static ServeProcess Start(int workers, params string[] extra)
    {
        var port = FreePort();
        var psi = new ProcessStartInfo(CliProcess.Binary!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[]
        {
            "serve", "--host", "127.0.0.1", "--port", port.ToString(CultureInfo.InvariantCulture),
            "--workers", workers.ToString(CultureInfo.InvariantCulture),
        })
        {
            psi.ArgumentList.Add(arg);
        }
        foreach (var arg in extra)
        {
            psi.ArgumentList.Add(arg);
        }
        psi.Environment["POCKETCALCULATOR_CDP_TOKEN"] = string.Empty;
        var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start the CLI");
        var server = new ServeProcess(process, port);
        _ = process.StandardOutput.ReadToEndAsync();
        process.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is { } text)
            {
                lock (server.log)
                {
                    server.log.AppendLine(text);
                }
            }
        };
        process.BeginErrorReadLine();
        return server;
    }

    /// <summary>The balancer's live child processes, sorted.</summary>
    public List<int> Workers()
    {
        var children = new SortedSet<int>();
        try
        {
            foreach (var task in Directory.EnumerateDirectories($"/proc/{Process.Id}/task"))
            {
                try
                {
                    foreach (var id in File.ReadAllText(Path.Combine(task, "children"))
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        children.Add(int.Parse(id, CultureInfo.InvariantCulture));
                    }
                }
                catch (IOException)
                {
                    // The thread ended while we read.
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // The balancer is gone.
        }
        return [.. children];
    }

    /// <summary><c>GET /json/version</c> through the balancer; throws when the connection fails.</summary>
    public string Discovery()
    {
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, Port);
        client.ReceiveTimeout = 2000;
        var stream = client.GetStream();
        stream.Write(Encoding.ASCII.GetBytes(
            $"GET /json/version HTTP/1.1\r\nHost: 127.0.0.1:{Port}\r\nConnection: close\r\n\r\n"));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public bool DiscoveryOk()
    {
        try
        {
            return Discovery().StartsWith("HTTP/1.1 200", StringComparison.Ordinal);
        }
        catch (Exception error) when (error is IOException or SocketException)
        {
            return false;
        }
    }

    /// <summary>Wait until discovery answers 200 through the balancer.</summary>
    public void WaitReady(TimeSpan within)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(within.TotalSeconds * Stopwatch.Frequency);
        while (!DiscoveryOk())
        {
            Assert.False(Process.HasExited, $"balancer exited:\n{Log}");
            Assert.True(Stopwatch.GetTimestamp() < deadline, $"balancer did not become ready:\n{Log}");
            Thread.Sleep(10);
        }
    }

    /// <summary>Send <paramref name="signal"/> (for example <c>-TERM</c>) to <paramref name="pid"/> with kill(1).</summary>
    public static void Kill(string signal, int pid)
    {
        using var kill = Process.Start(new ProcessStartInfo("kill")
        {
            ArgumentList = { signal, pid.ToString(CultureInfo.InvariantCulture) },
            UseShellExecute = false,
        })!;
        kill.WaitForExit();
        Assert.Equal(0, kill.ExitCode);
    }

    public static bool Alive(int pid) => Directory.Exists($"/proc/{pid}");

    public void Dispose()
    {
        var workers = Workers();
        try
        {
            Process.Kill(entireProcessTree: true);
            Process.WaitForExit(10_000);
        }
        catch (InvalidOperationException)
        {
        }
        foreach (var pid in workers)
        {
            try
            {
                System.Diagnostics.Process.GetProcessById(pid).Kill();
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException
                or System.ComponentModel.Win32Exception)
            {
            }
        }
        Process.Dispose();
    }
}
