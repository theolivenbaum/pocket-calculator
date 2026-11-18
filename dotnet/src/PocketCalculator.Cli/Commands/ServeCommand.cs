using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PocketCalculator.Cdp;
using PocketCalculator.Cli.CommandLine;

namespace PocketCalculator.Cli.Commands;

/// <summary>
/// The <c>serve</c> subcommand and the bare no-subcommand server path.
/// </summary>
public static class ServeCommand
{
    /// <summary>Dispatch for the <c>serve</c> subcommand.</summary>
    public static async Task RunAsync(CliArgs args, CliCommand.Serve serve)
    {
        // Upstream 0f27e69: worker recycling, off by default.
        var policy = new RecyclePolicy(serve.WorkerMaxConnections, serve.WorkerMaxRss, serve.WorkerMaxAge);
        if (policy.Enabled && serve.Workers <= 1)
        {
            throw new CliException("--worker-max-* options require --workers > 1");
        }
        if (policy.MaxRssMb is not null && !OperatingSystem.IsLinux())
        {
            Log.Warn("--worker-max-rss is only supported on Linux and is ignored");
        }

        // Fall back to POCKETCALCULATOR_PROXY so a proxy can be supplied without putting
        // credentials on the command line. The multi-worker load balancer passes
        // the proxy to each worker this way.
        var proxy = CliOptions.MergeProxy(args.Proxy, serve.Proxy)
            ?? EnvOrNull("POCKETCALCULATOR_PROXY");
        var userAgent = CliOptions.MergeUserAgent(args.UserAgent, serve.UserAgent);

        CliOptions.ConfigureFontDirectories(serve.FontDirs);
        HardDeadline.ArmHangExit();
        CliOptions.PrintBanner(serve.Port);
        if (serve.StorageDir is { } dir)
        {
            Log.Info($"Storage dir: {dir}");
        }
        if (proxy is { } configured)
        {
            Log.Info($"Using proxy: {CliOptions.RedactProxy(configured)}");
        }
        if (userAgent is { } ua)
        {
            Log.Info($"User-Agent: {ua}");
        }
        foreach (var directory in serve.FontDirs)
        {
            Log.Info($"Font dir: {directory}");
        }
        // SECURITY.md I1: optional TLS. Loaded here so a bad path fails before
        // anything is bound or spawned.
        X509Certificate2? certificate;
        try
        {
            certificate = PocketCalculator.Net.ServerTls.Resolve(serve.TlsCert, serve.TlsKey);
        }
        catch (InvalidOperationException error)
        {
            throw new CliException(error.Message);
        }

        // Rust logs "Stealth mode enabled (...)" here, in the Serve arm only.
        // Program.cs already logs the port's equivalent line, with the TLS gap
        // named, for every subcommand; repeating it here would put two stealth
        // lines on stderr for `-v serve --stealth` where Rust prints one.

        if (serve.Workers > 1)
        {
            Log.Info(string.Create(
                CultureInfo.InvariantCulture, $"{serve.Workers} worker processes"));
            await RunMultiWorkerServeAsync(
                serve.Port, serve.Host, serve.Workers, new WorkerSettings
                {
                    Proxy = proxy,
                    Stealth = args.Stealth,
                    UserAgent = userAgent,
                    FontDirs = serve.FontDirs,
                    AllowFileAccess = serve.AllowFileAccess,
                    StorageDir = serve.StorageDir,
                    MaxConnections = serve.MaxConnections,
                    AllowPrivateNetwork = args.AllowPrivateNetwork,
                    V8Flags = args.V8Flags,
                    TlsCert = serve.TlsCert,
                    TlsKey = serve.TlsKey,
                    Certificate = certificate,
                },
                policy,
                // A u64 of seconds can exceed what a TimeSpan holds; such a timeout
                // never ends either way.
                TimeSpan.FromSeconds(Math.Min(serve.WorkerDrainTimeout, (long)TimeSpan.MaxValue.TotalSeconds - 1)))
                .ConfigureAwait(false);
            return;
        }

        await CdpServer.StartWithTlsAsync(
            serve.Port,
            serve.Host,
            proxy,
            args.Stealth,
            userAgent,
            serve.AllowFileAccess,
            serve.StorageDir,
            args.AllowPrivateNetwork,
            serve.MaxConnections,
            certificate).ConfigureAwait(false);
    }

    /// <summary>The bare <c>obscura</c> invocation with no subcommand.</summary>
    public static async Task RunDefaultAsync(CliArgs args)
    {
        CliOptions.PrintBanner(args.Port);
        if (args.Proxy is { } proxy)
        {
            Log.Info($"Using proxy: {CliOptions.RedactProxy(proxy)}");
        }
        await CdpServer.StartWithOptionsAsync(args.Port, args.Proxy, args.Stealth).ConfigureAwait(false);
    }

    /// <summary>
    /// Spawn <paramref name="workers"/> single-worker servers on OS-assigned
    /// loopback ports and load balance across them round robin (upstream a156914).
    /// </summary>
    /// <remarks>
    /// The balancer binds the requested host, not hardcoded loopback: with
    /// <c>--host 0.0.0.0</c> (a container with a mapped port) the single-worker
    /// path already binds all interfaces and the balancer must too, or the
    /// mapped port is refused from outside. Workers stay on loopback and are
    /// only reached by the balancer, which tells them the client-facing
    /// authority through <c>POCKETCALCULATOR_CDP_FORWARDED_HOST</c>/<c>_PORT</c> so their
    /// Host check accepts the name a client used to reach the balancer.
    /// </remarks>
    public static async Task RunMultiWorkerServeAsync(
        int port,
        string host,
        int workers,
        WorkerSettings settings,
        RecyclePolicy? policy = null,
        TimeSpan? drainTimeout = null)
    {
        // Deviation: upstream a156914 leaves this check to the workers, which see a
        // public forwarded authority, refuse to start without a token, and make the
        // balancer fail with "worker 1 exited during startup". The port refuses
        // here first, before anything is bound or spawned, with the message the
        // single-worker server gives. Workers inherit POCKETCALCULATOR_CDP_TOKEN
        // from this process's environment and enforce it too.
        var token = Environment.GetEnvironmentVariable("POCKETCALCULATOR_CDP_TOKEN");
        if (!string.IsNullOrEmpty(token) && System.Text.Encoding.UTF8.GetByteCount(token) < 32)
        {
            throw new CliException("POCKETCALCULATOR_CDP_TOKEN must be at least 32 bytes");
        }
        var loopback = IPAddress.TryParse(host, out var literal)
            ? IPAddress.IsLoopback(literal)
            : host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        if (!loopback && string.IsNullOrEmpty(token))
        {
            throw new CliException(
                "refusing to expose CDP without authentication; set POCKETCALCULATOR_CDP_TOKEN to at least 32 bytes");
        }

        var launcher = ResolveWorkerLauncher(
            Environment.ProcessPath, System.Reflection.Assembly.GetEntryAssembly()?.Location);

        // Upstream 4b14a76: without a handler, SIGTERM ended the balancer and
        // orphaned its workers. Registered before any worker is spawned, so an
        // early signal is not fatal to the balancer alone.
        using var shutdown = new CancellationTokenSource();
        using var signals = RegisterShutdownSignals(shutdown);

        // Claim the public port before starting children so another process
        // cannot take it during worker startup.
        var listener = Bind(host, port);
        var bound = (IPEndPoint)listener.LocalEndpoint;

        // Deviation: upstream forwards the --host string and --port as given.
        // A worker parses the host as an IP literal, so `--host localhost` made
        // every upstream worker exit at startup, and `--port 0` forwarded port
        // 0. The port forwards the address and port the balancer actually
        // bound, which are the same values for an IP literal and a fixed port.
        var forwardedHost = bound.Address.ToString();
        var forwardedPort = bound.Port.ToString(CultureInfo.InvariantCulture);

        // Internal worker ports are implementation details. Asking the OS for free
        // ports avoids assuming that every port adjacent to the public one is
        // available, or that `port + workers` cannot overflow.
        var reservations = new List<Socket>(workers);
        var workerPorts = new List<int>(workers);
        var children = new List<Process>(workers);
        var factory = new WorkerFactory(launcher, settings, forwardedHost, forwardedPort, token);
        try
        {
            for (var i = 0; i < workers; i++)
            {
                var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                reservations.Add(reservation);
                reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                workerPorts.Add(((IPEndPoint)reservation.LocalEndPoint!).Port);
            }

            for (var i = 0; i < workers; i++)
            {
                var workerPort = workerPorts[i];
                reservations[i].Dispose();
                var child = factory.Spawn(workerPort);
                Log.Info(string.Create(CultureInfo.InvariantCulture, $"Worker {i + 1} on port {workerPort}"));
                children.Add(child);
            }

            await WaitForWorkersAsync(children, workerPorts).ConfigureAwait(false);
            await VerifyWorkersAsync(workerPorts, token, settings.Certificate).ConfigureAwait(false);
        }
        catch
        {
            // Deviation: when startup fails, upstream returns the error and leaves
            // the workers it already spawned running, orphaned on their loopback
            // ports. The port stops them.
            foreach (var reservation in reservations)
            {
                reservation.Dispose();
            }
            foreach (var child in children)
            {
                KillQuietly(child);
            }
            listener.Dispose();
            throw;
        }

        // Upstream f2b9e99 and 0f27e69: a supervisor per pool slot reaps its worker
        // when it exits and starts a replacement, and replaces one that reaches a
        // recycle limit, draining the old one.
        policy ??= new RecyclePolicy();
        var drain = drainTimeout ?? TimeSpan.FromSeconds(DefaultWorkerDrainTimeoutSeconds);
        // At most a tenth of the pool (minimum one) starts a replacement at a time,
        // so a fleet that reaches its limit together does not stampede the CPU with
        // simultaneous V8 startups.
        using var recycles = new SemaphoreSlim(Math.Max(workers / 10, 1));
        using var stopWorkers = new CancellationTokenSource();
        var slots = new PoolSlot[workers];
        var supervisors = new Task[workers];
        for (var i = 0; i < workers; i++)
        {
            var wake = new WakeSignal();
            slots[i] = new PoolSlot(new PoolWorker(workerPorts[i], policy.MaxConnections, wake), wake);
            supervisors[i] = SuperviseWorkerAsync(
                slots[i], children[i], factory, policy, drain, recycles, stopWorkers.Token);
        }

        Log.Info(string.Create(
            CultureInfo.InvariantCulture, $"Load balancer on {host}:{port}, {workers} workers"));
        if (policy.Enabled)
        {
            Log.Info(string.Create(
                CultureInfo.InvariantCulture,
                $"Worker recycling: {policy}, drain timeout {(long)drain.TotalSeconds}s"));
        }

        // Rust advances a usize counter with wrapping_add; a ulong wraps the same
        // way for any count a process can accept.
        ulong nextWorker = 0;
        // Deviation (SECURITY.md L3): upstream proxies every accepted connection.
        // The balancer caps live proxied connections at --max-connections, the
        // same server-wide limit a single-worker server applies, and refuses the
        // rest with the worker's own 503 rather than holding two sockets per
        // client without bound.
        var maxConnections = Math.Max(1, settings.MaxConnections);
        var live = 0;
        // Refusals in flight. On a TLS listener each one costs a handshake, so
        // past this many a refused client is closed with no reply at all.
        var refusing = 0;
        void Refuse(TcpClient client, string response)
        {
            if (Interlocked.Increment(ref refusing) > maxConnections)
            {
                Interlocked.Decrement(ref refusing);
                client.Dispose();
                return;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    await RefuseAsync(client, settings.Certificate, response).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref refusing);
                }
            });
        }

        while (true)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (Interlocked.Increment(ref live) > maxConnections)
            {
                Interlocked.Decrement(ref live);
                Log.Warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"refusing connection: at --max-connections ({maxConnections})"));
                Refuse(client, MaxConnectionsRefusal);
                continue;
            }
            try
            {
                client.NoDelay = true;
            }
            catch (SocketException error)
            {
                Log.Warn($"client {client.Client.RemoteEndPoint} TCP_NODELAY failed: {error.Message}");
            }
            // Round robin over the workers that are up, skipping any being restarted.
            // The connection is held while it is proxied, so a draining worker waits for it.
            if (WorkerPool.Pick(slots, ref nextWorker) is not { } connection)
            {
                Interlocked.Decrement(ref live);
                Refuse(client, NoWorkerRefusal);
                continue;
            }
            Log.Debug(string.Create(
                CultureInfo.InvariantCulture,
                $"Routing {client.Client.RemoteEndPoint} to worker port {connection.Port}"));
            // Upstream peeks the first bytes of each request on the accept loop to
            // tell `/json` from a WebSocket (both are then proxied the same way),
            // and since 0f27e69 to count only the latter against
            // --worker-max-connections. Since a156914 it also drops a client that
            // closes before sending anything. The port never peeks on the accept
            // loop: it proxies from the first byte on the connection's own task, so
            // a silent or early-closed client cannot stall or end the accept loop
            // (it simply sees an empty exchange), and classifies the request from
            // the first bytes it forwards (ProxyClientAsync).
            _ = Task.Run(async () =>
            {
                try
                {
                    await ProxyAsync(client, connection).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref live);
                }
            });
        }

        Log.Info("Shutting down, stopping workers");
        listener.Dispose();
        // The supervisors stop their workers in parallel; whatever is still running
        // after five seconds is killed, as dropping the supervisors does upstream
        // (kill_on_drop).
        await stopWorkers.CancelAsync().ConfigureAwait(false);
        await Task.WhenAny(Task.WhenAll(supervisors), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        factory.KillAll();
    }

    /// <summary>
    /// Trip <paramref name="shutdown"/> on SIGTERM or SIGINT (upstream
    /// <c>shutdown_signal</c>), instead of letting the runtime end the process.
    /// </summary>
    private static IDisposable RegisterShutdownSignals(CancellationTokenSource shutdown)
    {
        List<PosixSignalRegistration> registrations = [];
        try
        {
            foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT })
            {
                registrations.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    context.Cancel = true;
                    try
                    {
                        shutdown.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Already shut down.
                    }
                }));
            }
        }
        catch (PlatformNotSupportedException)
        {
        }
        return new Registrations(registrations);
    }

    private sealed class Registrations(List<PosixSignalRegistration> items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items)
            {
                item.Dispose();
            }
        }
    }

    /// <summary>The balancer's own 503 when it is at <c>--max-connections</c> (SECURITY.md L3).</summary>
    private const string MaxConnectionsRefusal =
        "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n"
        + "X-Obscura-Reason: max-connections\r\n\r\n";

    /// <summary>Upstream f2b9e99's reply when no worker is ready, byte for byte.</summary>
    private const string NoWorkerRefusal = "HTTP/1.1 503 Service Unavailable\r\nConnection: close\r\n\r\n";

    /// <summary>Upstream's <c>--worker-drain-timeout</c> default, in seconds.</summary>
    public const long DefaultWorkerDrainTimeoutSeconds = 120;

    /// <summary>
    /// How often each supervisor re-checks its recycle policy (upstream
    /// <c>RECYCLE_POLL</c>): RSS and age have no event to wait on; the connection
    /// limit also wakes it directly.
    /// </summary>
    private static readonly TimeSpan RecyclePoll = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Starts workers with the balancer's settings and keeps every worker process
    /// it started until it is reaped, so shutdown can kill whatever is left.
    /// </summary>
    private sealed class WorkerFactory(
        WorkerLauncher launcher,
        WorkerSettings settings,
        string forwardedHost,
        string forwardedPort,
        string? token)
    {
        private readonly ConcurrentDictionary<Process, byte> running = new();

        public string? Token => token;

        public X509Certificate2? Certificate => settings.Certificate;

        /// <summary>
        /// Start a worker process. Its stdout is discarded, as the reference redirects
        /// it to null; its stderr is this process's, so worker errors stay visible
        /// (upstream f2b9e99, which stopped discarding it).
        /// </summary>
        public Process Spawn(int workerPort)
        {
            var psi = WorkerStartInfo(launcher, settings, workerPort, forwardedHost, forwardedPort);
            var child = Process.Start(psi)
                ?? throw new CliException($"failed to spawn worker on port {workerPort}");
            _ = child.StandardOutput.ReadToEndAsync();
            running.TryAdd(child, 0);
            return child;
        }

        /// <summary>Drop a worker that has exited.</summary>
        public void Forget(Process child)
        {
            running.TryRemove(child, out _);
            child.Dispose();
        }

        /// <summary>Kill every worker still running.</summary>
        public void KillAll()
        {
            foreach (var child in running.Keys)
            {
                KillQuietly(child);
            }
        }
    }

    /// <summary>
    /// Supervise one pool slot (upstream 0f27e69 <c>supervise_worker</c>): restart
    /// its worker when it exits (f2b9e99), replace it when it reaches a recycle
    /// limit and drain the old one, and stop both on shutdown (4b14a76).
    /// </summary>
    private static async Task SuperviseWorkerAsync(
        PoolSlot slot,
        Process child,
        WorkerFactory factory,
        RecyclePolicy policy,
        TimeSpan drainTimeout,
        SemaphoreSlim recycles,
        CancellationToken stop)
    {
        var stopping = WhenCancelled(stop);
        // Replaced workers still draining; awaited at shutdown so they are stopped
        // gracefully too.
        var drains = new List<Task>();
        var exited = child.WaitForExitAsync();
        var tick = Task.Delay(RecyclePoll);
        while (true)
        {
            drains.RemoveAll(drained => drained.IsCompleted);
            var worker = slot.Current;
            if (tick.IsCompleted)
            {
                tick = Task.Delay(RecyclePoll);
            }
            await Task.WhenAny(stopping, exited, slot.Wake.Notified, tick).ConfigureAwait(false);
            if (stop.IsCancellationRequested)
            {
                await Task.WhenAll(StopWorkerAsync(child), Task.WhenAll(drains)).ConfigureAwait(false);
                factory.Forget(child);
                return;
            }
            if (exited.IsCompleted)
            {
                worker.Ready = false;
                Log.Warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"worker on port {worker.Port} exited: exit status: {child.ExitCode}"));
                factory.Forget(child);
                if (await RespawnWorkerAsync(slot, factory, policy.MaxConnections, stop).ConfigureAwait(false)
                    is not { } replacement)
                {
                    await Task.WhenAll(drains).ConfigureAwait(false);
                    return;
                }
                child = replacement;
                exited = child.WaitForExitAsync();
                continue;
            }
            // Taken before the policy is read: a session counted after this wakes
            // the next wait, and one counted before it is in Served already.
            slot.Wake.Consume();
            if (!policy.Enabled)
            {
                continue;
            }
            var rss = policy.MaxRssMb is null ? null : WorkerPool.RssMb(child.Id);
            if (policy.Reason(worker.Served, rss, worker.Age) is not { } reason)
            {
                continue;
            }
            // Bound concurrent replacement startups (not draining); if no permit is
            // free, retry on the next tick and keep serving meanwhile.
            if (!recycles.Wait(0))
            {
                continue;
            }
            PoolWorker old;
            Process retired;
            try
            {
                var started = Stopwatch.GetTimestamp();
                int port;
                Process next;
                try
                {
                    port = FreePort();
                    next = factory.Spawn(port);
                }
                catch (Exception error) when (error is SocketException or CliException
                    or System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    Log.Warn(string.Create(
                        CultureInfo.InvariantCulture,
                        $"recycle of worker on port {worker.Port} failed to spawn: {error.Message}"));
                    await DelayUnlessStopped(TimeSpan.FromSeconds(5), stop).ConfigureAwait(false);
                    continue;
                }
                if (await ReadyAsync(next, port, TimeSpan.FromSeconds(10), factory).ConfigureAwait(false)
                    is { } failure)
                {
                    Log.Warn(string.Create(
                        CultureInfo.InvariantCulture,
                        $"recycle of worker on port {worker.Port} aborted, replacement failed: {failure}"));
                    await KillAsync(next).ConfigureAwait(false);
                    factory.Forget(next);
                    await DelayUnlessStopped(TimeSpan.FromSeconds(5), stop).ConfigureAwait(false);
                    continue;
                }
                old = slot.Replace(new PoolWorker(port, policy.MaxConnections, slot.Wake));
                Log.Info(string.Create(
                    CultureInfo.InvariantCulture,
                    $"recycling worker on port {old.Port} (pid Some({child.Id}), served {old.Served}, "
                    + $"live {old.Active}, age {(long)old.Age.TotalSeconds}s): {reason}; replacement on port "
                    + $"{port} ready in {(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds} ms"));
                retired = child;
                child = next;
                exited = child.WaitForExitAsync();
            }
            finally
            {
                recycles.Release();
            }
            drains.Add(DrainWorkerAsync(retired, old, drainTimeout, factory, stop));
        }
    }

    /// <summary>
    /// Replace a dead worker, retrying until one starts (upstream
    /// <c>respawn_worker</c>). Always on a fresh port, since 0f27e69. Null once the
    /// balancer is shutting down.
    /// </summary>
    private static async Task<Process?> RespawnWorkerAsync(
        PoolSlot slot, WorkerFactory factory, int? maxConnections, CancellationToken stop)
    {
        while (true)
        {
            // Upstream: a fixed retry interval bounds a crash loop; there is no backoff.
            await Task.Delay(100).ConfigureAwait(false);
            if (stop.IsCancellationRequested)
            {
                return null;
            }
            int port;
            try
            {
                port = FreePort();
            }
            catch (SocketException error)
            {
                Log.Warn($"worker port allocation failed: {error.Message}");
                continue;
            }
            if (await StartWorkerAsync(factory, port, TimeSpan.FromSeconds(5)).ConfigureAwait(false)
                is { } replacement)
            {
                slot.Replace(new PoolWorker(port, maxConnections, slot.Wake));
                return replacement;
            }
        }
    }

    /// <summary>
    /// Let a replaced worker finish its live connections, then kill it (upstream
    /// <c>drain_worker</c>). On shutdown it is stopped like the active workers.
    /// </summary>
    private static async Task DrainWorkerAsync(
        Process child, PoolWorker old, TimeSpan timeout, WorkerFactory factory, CancellationToken stop)
    {
        var started = Stopwatch.GetTimestamp();
        var exited = child.WaitForExitAsync();
        var stopping = WhenCancelled(stop);
        string outcome;
        while (true)
        {
            if (old.Active == 0)
            {
                outcome = "drained";
                break;
            }
            if (Stopwatch.GetElapsedTime(started) >= timeout)
            {
                outcome = string.Create(
                    CultureInfo.InvariantCulture, $"drain timeout, dropping {old.Active} live connections");
                break;
            }
            var woke = await Task.WhenAny(exited, stopping, Task.Delay(100)).ConfigureAwait(false);
            if (woke == exited)
            {
                outcome = "exited while draining";
                break;
            }
            if (woke == stopping)
            {
                await StopWorkerAsync(child).ConfigureAwait(false);
                outcome = "stopped at shutdown";
                break;
            }
        }
        await KillAsync(child).ConfigureAwait(false);
        factory.Forget(child);
        Log.Info(string.Create(CultureInfo.InvariantCulture, $"worker on port {old.Port} retired: {outcome}"));
    }

    /// <summary>A free loopback port, as upstream's <c>free_port</c> asks the OS for one.</summary>
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static async Task DelayUnlessStopped(TimeSpan delay, CancellationToken stop)
    {
        try
        {
            await Task.Delay(delay, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>A task that completes, rather than faults, once <paramref name="token"/> is cancelled.</summary>
    private static Task WhenCancelled(CancellationToken token)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() => cancelled.TrySetResult());
        return cancelled.Task;
    }

    /// <summary>
    /// Ask a worker to exit with SIGTERM, which lets it save its cookies, and kill
    /// it if it is still running three seconds later (upstream 4b14a76
    /// <c>stop_worker</c>).
    /// </summary>
    private static async Task StopWorkerAsync(Process child)
    {
        try
        {
            if (!OperatingSystem.IsWindows() && !child.HasExited && SysKill(child.Id, SigTerm) == 0)
            {
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await child.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException)
                {
                    // Still running; kill it.
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Not started or already gone.
            return;
        }
        await KillAsync(child).ConfigureAwait(false);
    }

    /// <summary>Kill a worker and give it a moment to be reaped, so it is not left a zombie.</summary>
    private static async Task KillAsync(Process child)
    {
        KillQuietly(child);
        using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await child.WaitForExitAsync(reap.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException)
        {
        }
    }

    private const int SigTerm = 15;

    /// <summary>
    /// kill(2), for the SIGTERM <see cref="Process"/> cannot send (its <c>Kill</c> is
    /// SIGKILL). libc is the platform, not a package, as for <c>ProcessExit</c>'s
    /// <c>_exit</c>, so this adds no native dependency. Unix only; SIGTERM is 15 on
    /// Linux and macOS. The pid is a child this process has not reaped yet, or the
    /// <c>HasExited</c> check just before it would have seen the exit.
    /// </summary>
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SysKill(int pid, int signal);

    /// <summary>
    /// Start one worker on <paramref name="workerPort"/> and wait up to
    /// <paramref name="within"/> for it to listen and verify. Null, with a warning logged and
    /// the process killed, when it does not come up.
    /// </summary>
    /// <remarks>
    /// Deviation: upstream only waits for the port to accept a connection. The
    /// port also checks a restarted or recycled worker the way startup checks every worker
    /// (<see cref="VerifyWorkersAsync(IReadOnlyList{int}, string?, X509Certificate2?)"/>,
    /// SECURITY.md M1) before it gets a client, since it is put behind the public
    /// port just as the first one was.
    /// </remarks>
    private static async Task<Process?> StartWorkerAsync(WorkerFactory factory, int workerPort, TimeSpan within)
    {
        Process child;
        try
        {
            child = factory.Spawn(workerPort);
        }
        catch (Exception error) when (error is CliException or System.ComponentModel.Win32Exception
            or InvalidOperationException)
        {
            Log.Warn(string.Create(
                CultureInfo.InvariantCulture, $"worker {workerPort} spawn failed: {error.Message}"));
            return null;
        }
        if (await ReadyAsync(child, workerPort, within, factory).ConfigureAwait(false) is { } failure)
        {
            Log.Warn(string.Create(
                CultureInfo.InvariantCulture, $"worker {workerPort} restart failed: {failure}"));
            await KillAsync(child).ConfigureAwait(false);
            factory.Forget(child);
            return null;
        }
        return child;
    }

    /// <summary>
    /// Wait up to <paramref name="within"/> for a new worker to listen, then verify
    /// it. Null when it is ready; otherwise why not.
    /// </summary>
    private static async Task<string?> ReadyAsync(
        Process child, int workerPort, TimeSpan within, WorkerFactory factory)
    {
        try
        {
            var deadline = Stopwatch.GetTimestamp() + (long)(within.TotalSeconds * Stopwatch.Frequency);
            await WaitForWorkerAsync(child, workerPort, deadline).ConfigureAwait(false);
            await VerifyWorkersAsync([workerPort], factory.Token, factory.Certificate).ConfigureAwait(false);
            return null;
        }
        catch (CliException error)
        {
            return error.Message;
        }
    }

    private static void KillQuietly(Process child)
    {
        try
        {
            child.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    /// <summary>What a worker process is started with, besides its port.</summary>
    public sealed record WorkerSettings
    {
        /// <summary>The proxy, passed through the environment rather than argv.</summary>
        public string? Proxy { get; init; }

        /// <summary><c>--stealth</c>.</summary>
        public bool Stealth { get; init; }

        /// <summary><c>--user-agent</c>.</summary>
        public string? UserAgent { get; init; }

        /// <summary><c>--font-dir</c>, in order.</summary>
        public IReadOnlyList<string> FontDirs { get; init; } = [];

        /// <summary><c>--allow-file-access</c>.</summary>
        public bool AllowFileAccess { get; init; }

        /// <summary><c>--storage-dir</c>.</summary>
        public string? StorageDir { get; init; }

        /// <summary><c>--max-connections</c>.</summary>
        public int MaxConnections { get; init; } = CliDefinition.DefaultMaxConnections;

        /// <summary><c>--allow-private-network</c>.</summary>
        public bool AllowPrivateNetwork { get; init; }

        /// <summary><c>--v8-flags</c>.</summary>
        public string? V8Flags { get; init; }

        /// <summary><c>--tls-cert</c>, passed on so each worker terminates TLS itself.</summary>
        public string? TlsCert { get; init; }

        /// <summary><c>--tls-key</c>.</summary>
        public string? TlsKey { get; init; }

        /// <summary>The loaded certificate, which the balancer pins when it probes a worker.</summary>
        public X509Certificate2? Certificate { get; init; }
    }

    /// <summary>The program a worker is started as, and the arguments that precede its own.</summary>
    public sealed record WorkerLauncher(string FileName, IReadOnlyList<string> LeadingArguments);

    /// <summary>
    /// Work out how to start another copy of this CLI.
    /// </summary>
    /// <remarks>
    /// Deviation (SECURITY.md M1): upstream starts <c>current_exe() serve ...</c>,
    /// and so did the port with <see cref="Environment.ProcessPath"/>. Run as
    /// <c>dotnet pocket-calculator.dll</c>, that path is the <c>dotnet</c> host, so
    /// workers became <c>dotnet serve ...</c>: an error normally, and with the
    /// <c>dotnet-serve</c> global tool installed an unauthenticated file server on
    /// the balancer's port. Under the host the entry assembly is passed first; a
    /// host with no entry assembly to name is refused rather than guessed at.
    /// </remarks>
    public static WorkerLauncher ResolveWorkerLauncher(string? processPath, string? entryAssemblyPath)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            throw new CliException("cannot locate the running executable to spawn workers");
        }
        // Either separator, so the check does not depend on the host OS.
        var name = processPath[(processPath.LastIndexOfAny(['/', '\\']) + 1)..];
        if (!name.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return new WorkerLauncher(processPath, []);
        }
        if (string.IsNullOrEmpty(entryAssemblyPath)
            || !entryAssemblyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException(
                "cannot spawn workers: running under the dotnet host with no entry assembly to pass it");
        }
        return new WorkerLauncher(processPath, [entryAssemblyPath]);
    }

    /// <summary>The start info for one worker on <paramref name="workerPort"/>.</summary>
    /// <remarks>
    /// Deviation (SECURITY.md I5): upstream passes a worker only the proxy, the
    /// user agent, the font directories and <c>--stealth</c>, so
    /// <c>--allow-file-access</c>, <c>--storage-dir</c>, <c>--max-connections</c>,
    /// <c>--allow-private-network</c> and <c>--v8-flags</c> were silently dropped
    /// under <c>--workers</c>. The port passes them all. Workers share the storage
    /// directory: the cookie file is written by an atomic rename, so the last
    /// worker to save wins but the file is never torn.
    /// </remarks>
    public static ProcessStartInfo WorkerStartInfo(
        WorkerLauncher launcher,
        WorkerSettings settings,
        int workerPort,
        string forwardedHost,
        string forwardedPort)
    {
        var psi = new ProcessStartInfo(launcher.FileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            // Inherited, so worker errors reach the balancer's stderr (upstream f2b9e99).
            RedirectStandardError = false,
        };
        foreach (var argument in launcher.LeadingArguments)
        {
            psi.ArgumentList.Add(argument);
        }
        // Root-level options precede the subcommand.
        if (settings.V8Flags is { } v8Flags)
        {
            psi.ArgumentList.Add("--v8-flags");
            psi.ArgumentList.Add(v8Flags);
        }
        psi.ArgumentList.Add("serve");
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(workerPort.ToString(CultureInfo.InvariantCulture));
        // Workers receive the client-facing Host header through the TCP
        // load balancer. Let their CDP security gate accept that public
        // authority while it continues to reject foreign hosts and browser
        // origins.
        psi.Environment["POCKETCALCULATOR_CDP_FORWARDED_HOST"] = forwardedHost;
        psi.Environment["POCKETCALCULATOR_CDP_FORWARDED_PORT"] = forwardedPort;
        if (settings.Proxy is { } workerProxy)
        {
            // Pass the proxy (which may embed credentials) through the
            // environment, not argv: a --proxy flag is visible in `ps` to
            // any local user, while POCKETCALCULATOR_PROXY is only readable by the
            // owner. The worker's serve path reads this as a fallback.
            psi.Environment["POCKETCALCULATOR_PROXY"] = workerProxy;
        }
        if (settings.UserAgent is { } ua)
        {
            psi.ArgumentList.Add("--user-agent");
            psi.ArgumentList.Add(ua);
        }
        // Each worker loads the fonts once for itself, as upstream passes the flags on.
        foreach (var directory in settings.FontDirs)
        {
            psi.ArgumentList.Add("--font-dir");
            psi.ArgumentList.Add(directory);
        }
        if (settings.Stealth)
        {
            psi.ArgumentList.Add("--stealth");
        }
        if (settings.AllowFileAccess)
        {
            psi.ArgumentList.Add("--allow-file-access");
        }
        if (settings.StorageDir is { } storageDir)
        {
            psi.ArgumentList.Add("--storage-dir");
            psi.ArgumentList.Add(storageDir);
        }
        psi.ArgumentList.Add("--max-connections");
        psi.ArgumentList.Add(settings.MaxConnections.ToString(CultureInfo.InvariantCulture));
        if (settings.AllowPrivateNetwork)
        {
            psi.ArgumentList.Add("--allow-private-network");
        }
        // TLS (SECURITY.md I1): the balancer proxies bytes, so each worker terminates
        // TLS. Paths only; POCKETCALCULATOR_TLS_CERT/_KEY reach workers through the
        // inherited environment.
        if (settings.TlsCert is { } tlsCert && settings.TlsKey is { } tlsKey)
        {
            psi.ArgumentList.Add("--tls-cert");
            psi.ArgumentList.Add(tlsCert);
            psi.ArgumentList.Add("--tls-key");
            psi.ArgumentList.Add(tlsKey);
        }
        return psi;
    }

    /// <summary>
    /// Check that every worker is a CDP server that enforces the token, before the
    /// balancer sends it a single client.
    /// </summary>
    /// <remarks>
    /// The balancer proxies bytes and authenticates nothing itself, so it relies on
    /// its workers for the token. A worker that is some other program listening on
    /// the port (SECURITY.md M1) would serve the public port unauthenticated; this
    /// refuses to start instead. The worker must answer a discovery request with
    /// the DevTools version document, and, with a token configured, refuse the
    /// same request without it with 401.
    /// </remarks>
    public static Task VerifyWorkersAsync(IReadOnlyList<int> workerPorts, string? token) =>
        VerifyWorkersAsync(workerPorts, token, null);

    /// <summary>
    /// As <see cref="VerifyWorkersAsync(IReadOnlyList{int}, string?)"/>, over TLS pinned to
    /// <paramref name="certificate"/> when the workers serve TLS.
    /// </summary>
    public static async Task VerifyWorkersAsync(
        IReadOnlyList<int> workerPorts, string? token, X509Certificate2? certificate)
    {
        for (var i = 0; i < workerPorts.Count; i++)
        {
            var authority = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{workerPorts[i]}");
            var authorized = await ProbeAsync(workerPorts[i], authority, token, certificate).ConfigureAwait(false);
            var ok = authorized.StartsWith("HTTP/1.1 200 ", StringComparison.Ordinal)
                && authorized.Contains("\"webSocketDebuggerUrl\"", StringComparison.Ordinal);
            if (ok && !string.IsNullOrEmpty(token))
            {
                var anonymous = await ProbeAsync(workerPorts[i], authority, null, certificate).ConfigureAwait(false);
                ok = anonymous.StartsWith("HTTP/1.1 401 ", StringComparison.Ordinal);
            }
            if (!ok)
            {
                throw new CliException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"worker {i + 1} on port {workerPorts[i]} is not an authenticated CDP server"));
            }
        }
    }

    private static async Task<string> ProbeAsync(
        int port, string authority, string? token, X509Certificate2? certificate)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            using var probe = new TcpClient(AddressFamily.InterNetwork);
            await probe.ConnectAsync(IPAddress.Loopback, port, cts.Token).ConfigureAwait(false);
            Stream stream = probe.GetStream();
            if (certificate is not null)
            {
                // The worker must present the very certificate this process loaded.
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions
                        {
                            TargetHost = "127.0.0.1",
                            RemoteCertificateValidationCallback = (_, presented, _, _) =>
                                presented is not null
                                && presented.GetCertHashString() == certificate.GetCertHashString(),
                        },
                        cts.Token)
                    .ConfigureAwait(false);
                stream = ssl;
            }

            var request = $"GET /json/version HTTP/1.1\r\nHost: {authority}\r\n"
                + (string.IsNullOrEmpty(token) ? string.Empty : $"Authorization: Bearer {token}\r\n")
                + "Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cts.Token).ConfigureAwait(false);
            // The version document is small; a bounded read keeps a hostile
            // listener from feeding the balancer without end.
            var buffer = new byte[16 * 1024];
            var received = 0;
            while (received < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(received), cts.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }
                received += n;
            }
            return Encoding.UTF8.GetString(buffer, 0, received);
        }
        catch (Exception error) when (error is SocketException or IOException or OperationCanceledException
            or System.Security.Authentication.AuthenticationException)
        {
            return string.Empty;
        }
    }

    /// <summary>Turn away a client that arrived while the balancer was at its limit.</summary>
    /// <remarks>
    /// On a TLS listener the balancer terminates TLS for this reply itself, as a
    /// single TLS worker does, so nothing plaintext is ever written on a TLS port;
    /// a client that fails the handshake is simply closed.
    /// </remarks>
    public static Task RefuseAsync(TcpClient client, X509Certificate2? certificate) =>
        RefuseAsync(client, certificate, MaxConnectionsRefusal);

    /// <summary>Turn away a client with <paramref name="response"/>, over TLS when the balancer serves it.</summary>
    private static async Task RefuseAsync(TcpClient client, X509Certificate2? certificate, string response)
    {
        using (client)
        {
            Stream? stream = null;
            try
            {
                stream = client.GetStream();
                if (certificate is not null)
                {
                    stream = await PocketCalculator.Net.ServerTls
                        .AuthenticateAsync(stream, certificate, CancellationToken.None).ConfigureAwait(false);
                }
                using (var write = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), write.Token).ConfigureAwait(false);
                    await stream.FlushAsync(write.Token).ConfigureAwait(false);
                }
                if (stream is SslStream ssl)
                {
                    await ssl.ShutdownAsync().ConfigureAwait(false);
                }
                client.Client.Shutdown(SocketShutdown.Send);
                // Drain briefly so the close is not a reset that discards the 503.
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                var buffer = new byte[4096];
                while (await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false) > 0)
                {
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException
                or InvalidOperationException or SocketException or OperationCanceledException
                or System.Security.Authentication.AuthenticationException)
            {
                // Best effort: the client sees a reset instead.
            }
            finally
            {
                if (stream is SslStream owned)
                {
                    await owned.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Wait until every worker has bound its control port, polling each with a
    /// connect, for at most five seconds in all (upstream a156914, which replaced
    /// a fixed 500 ms sleep that dominated multi-worker startup).
    /// </summary>
    /// <remarks>
    /// A worker that exits first fails startup with
    /// <c>worker on port P exited during startup: exit status: C</c> (the wording
    /// of upstream f2b9e99, which shares this wait with a restart); one still not
    /// listening at the deadline fails it with the connect error, as upstream
    /// returns the io error.
    /// </remarks>
    public static async Task WaitForWorkersAsync(IReadOnlyList<Process> children, IReadOnlyList<int> workerPorts)
    {
        var deadline = Stopwatch.GetTimestamp() + (5 * Stopwatch.Frequency);
        for (var i = 0; i < children.Count; i++)
        {
            await WaitForWorkerAsync(children[i], workerPorts[i], deadline).ConfigureAwait(false);
        }
    }

    /// <summary>Upstream <c>wait_for_serve_worker</c>: poll one worker's port until it accepts.</summary>
    private static async Task WaitForWorkerAsync(Process child, int workerPort, long deadline)
    {
        while (true)
        {
            if (child.HasExited)
            {
                throw new CliException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"worker on port {workerPort} exited during startup: exit status: {child.ExitCode}"));
            }

            try
            {
                using var probe = new TcpClient(AddressFamily.InterNetwork);
                await probe.ConnectAsync(IPAddress.Loopback, workerPort).ConfigureAwait(false);
                return;
            }
            catch (SocketException) when (Stopwatch.GetTimestamp() < deadline)
            {
                await Task.Delay(5).ConfigureAwait(false);
            }
            catch (SocketException error)
            {
                throw new CliException(error.Message, error);
            }
        }
    }

    /// <summary>
    /// Bind the balancer to <paramref name="host"/>, which may be a name.
    /// </summary>
    /// <remarks>
    /// Rust binds a <c>(&amp;str, u16)</c>, and tokio resolves it and tries each
    /// resolved address in turn, so <c>--host localhost</c> works there. A plain
    /// <see cref="IPAddress.TryParse"/> would reject it.
    /// </remarks>
    private static TcpListener Bind(string host, int port)
    {
        IPAddress[] candidates;
        if (IPAddress.TryParse(host, out var literal))
        {
            candidates = [literal];
        }
        else
        {
            try
            {
                candidates = Dns.GetHostAddresses(host);
            }
            catch (Exception error) when (error is SocketException or ArgumentException)
            {
                throw new CliException($"invalid --host '{host}': {error.Message}");
            }
        }

        Exception? last = null;
        foreach (var address in candidates)
        {
            var listener = new TcpListener(address, port);
            try
            {
                listener.Start();
                return listener;
            }
            catch (SocketException error)
            {
                last = error;
                listener.Dispose();
            }
        }
        throw new CliException(
            $"cannot bind {host}:{port.ToString(CultureInfo.InvariantCulture)}"
            + (last is null ? ": no addresses resolved" : $": {last.Message}"));
    }

    private static async Task ProxyAsync(TcpClient client, PoolConnection connection)
    {
        using (connection)
        using (client)
        {
            var workerPort = connection.Port;
            var workerAddress = $"127.0.0.1:{workerPort.ToString(CultureInfo.InvariantCulture)}";
            TcpClient worker;
            try
            {
                worker = new TcpClient();
                await worker.ConnectAsync(IPAddress.Loopback, workerPort).ConfigureAwait(false);
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
                Log.Warn($"worker {workerAddress} unreachable: {error.Message}");
                try
                {
                    var stream = client.GetStream();
                    await stream.WriteAsync(
                        Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n"))
                        .ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
                catch (Exception write) when (write is IOException or ObjectDisposedException
                    or InvalidOperationException)
                {
                    // The client is gone too; nothing to report.
                }
                return;
            }

            using (worker)
            {
                try
                {
                    worker.NoDelay = true;
                }
                catch (SocketException error)
                {
                    // Upstream a156914 drops the connection when this fails.
                    Log.Warn($"worker {workerAddress} TCP_NODELAY failed: {error.Message}");
                    return;
                }

                // tokio's copy_bidirectional copies each direction to EOF, shuts
                // the destination's write half down, and returns only once BOTH
                // directions are done. Completing on the first one instead would
                // tear down a connection whose peer is still sending: a client
                // that half-closes after its request would never get the reply.
                await Task.WhenAll(
                    PumpAsync(client, worker, connection),
                    PumpAsync(worker, client, null)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// One direction of the bidirectional copy, with the half-close. On the client's
    /// side, <paramref name="session"/> is counted as a CDP session from the first
    /// bytes, unless they are a <c>/json</c> discovery request (upstream 0f27e69).
    /// </summary>
    private static async Task PumpAsync(TcpClient from, TcpClient to, PoolConnection? session)
    {
        try
        {
            var source = from.GetStream();
            var sink = to.GetStream();
            if (session is not null)
            {
                var first = new byte[4096];
                var read = await source.ReadAsync(first).ConfigureAwait(false);
                if (read == 0)
                {
                    // Closed before sending anything: upstream drops it uncounted.
                    return;
                }
                if (!IsDiscoveryRequest(first.AsSpan(0, read)))
                {
                    session.CountSession();
                }
                await sink.WriteAsync(first.AsMemory(0, read)).ConfigureAwait(false);
            }
            await source.CopyToAsync(sink).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException
            or InvalidOperationException or SocketException)
        {
            // A half-closed proxy connection is ordinary.
        }
        finally
        {
            try
            {
                to.Client.Shutdown(SocketShutdown.Send);
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
                // The peer is already gone.
            }
        }
    }

    /// <summary>
    /// Upstream's test: the request starts with <c>GET </c> and <c>/json</c> appears
    /// in its first 256 bytes. Upstream peeks whatever has arrived, as this reads
    /// it, so a request split before those bytes counts as a session in both.
    /// </summary>
    /// <remarks>
    /// Deviation: behind <c>--tls-cert</c> (SECURITY.md I1, which upstream lacks) the
    /// balancer forwards ciphertext, so every connection, discovery included, counts
    /// against <c>--worker-max-connections</c>.
    /// </remarks>
    public static bool IsDiscoveryRequest(ReadOnlySpan<byte> first) =>
        first.StartsWith("GET "u8) && first[..Math.Min(first.Length, 256)].IndexOf("/json"u8) >= 0;

    private static string? EnvOrNull(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
