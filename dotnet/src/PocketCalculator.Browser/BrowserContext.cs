using PocketCalculator.Net;

namespace PocketCalculator.Browser;

/// <summary>
/// The browser-level state a set of pages shares: cookies, the HTTP client, the
/// browser identity, and the policy switches that gate what a page may reach.
/// </summary>
public sealed class BrowserContext
{
    private BrowserContext(
        string id,
        string? proxyUrl,
        bool stealth,
        string? userAgent,
        string? storageDir,
        bool allowPrivateNetwork,
        bool restoreCookiesFromDisk = true,
        PocketCalculator.Js.Ops.WebStorage? localStorage = null,
        PocketCalculator.Js.Ops.IndexedDbStorage? indexedDb = null)
    {
        Id = id;
        LocalStorage = localStorage ?? new PocketCalculator.Js.Ops.WebStorage();
        IndexedDb = indexedDb ?? new PocketCalculator.Js.Ops.IndexedDbStorage();
        // upstream f661c2e: restore {storage_dir}/localStorage. A persistent copy shares its
        // template's store instead (IsolatedCopy), so it reads nothing.
        if (localStorage is null && restoreCookiesFromDisk && storageDir is not null)
        {
            WebStorageProfile.Load(LocalStorage, storageDir);
        }
        var cookieJar = new CookieJar();

        // Restore cookies from disk if storageDir is configured.
        if (restoreCookiesFromDisk && storageDir is not null)
        {
            string cookiePath = Path.Combine(storageDir, "cookies.json");
            if (File.Exists(cookiePath))
            {
                try
                {
                    cookieJar.LoadFromFile(cookiePath);
                }
                catch (Exception)
                {
                    // A malformed or unreadable jar must not stop the context from starting.
                }
            }
        }

        var client = new PocketCalculatorHttpClient(cookieJar, proxyUrl, allowPrivateNetwork);
        if (stealth)
        {
            client.BlockTrackers = true;
        }

        BrowserProfile profile = Profiles.SelectProfile();
        string resolvedUa = userAgent ?? profile.UserAgent;
        Platform = profile.Platform;
        UaPlatform = profile.UaPlatform;
        UaPlatformVersion = profile.UaPlatformVersion;
        // Sync the http client's UA at construction so navigation requests pick it
        // up before any async setup runs.
        client.SetUserAgent(resolvedUa);

        CookieJar = cookieJar;
        HttpClient = client;
        UserAgent = resolvedUa;
        ProxyUrl = proxyUrl;
        RobotsCache = new RobotsCache();
        Stealth = stealth;
        StorageDir = storageDir;
        AllowPrivateNetwork = allowPrivateNetwork;
    }

    public string Id { get; }

    public CookieJar CookieJar { get; }

    /// <summary>
    /// The context's <c>localStorage</c>, kept for its lifetime like its cookies, and with a
    /// storage directory restored from and saved to <c>{storage_dir}/localStorage</c>
    /// (upstream f661c2e).
    /// </summary>
    public PocketCalculator.Js.Ops.WebStorage LocalStorage { get; }

    /// <summary>
    /// The context's IndexedDB records (upstream 3dea548): shared by its pages, kept across
    /// navigation, in memory only (<c>--storage-dir</c> does not persist them).
    /// </summary>
    public PocketCalculator.Js.Ops.IndexedDbStorage IndexedDb { get; }

    public PocketCalculatorHttpClient HttpClient { get; }

    public string UserAgent { get; }

    public string Platform { get; }

    public string UaPlatform { get; }

    public string UaPlatformVersion { get; }

    public string? ProxyUrl { get; }

    public RobotsCache RobotsCache { get; }

    public bool ObeyRobots { get; set; }

    public bool Stealth { get; }

    /// <summary>
    /// When true, CDP-driven navigation to <c>file://</c> URLs is permitted.
    /// </summary>
    /// <remarks>
    /// The default is false: a remote CDP client cannot point the browser at
    /// <c>/etc/shadow</c> even if Obscura runs as a privileged user. Flip it on with
    /// <c>obscura serve --allow-file-access</c> for local-HTML testing. The CLI's own
    /// <c>obscura fetch file://...</c> path is unaffected because it does not go
    /// through the CDP server.
    /// </remarks>
    public bool AllowFileAccess { get; set; }

    public string? StorageDir { get; }

    /// <summary>
    /// When true, the http client allows fetching localhost / RFC1918 / link-local
    /// addresses.
    /// </summary>
    /// <remarks>
    /// Independent of <see cref="AllowFileAccess"/> because they cover different
    /// threat models: <c>file://</c> is a local file-system read, private-network is
    /// the broader SSRF gate.
    /// </remarks>
    public bool AllowPrivateNetwork { get; }

    public static BrowserContext New(string id) => new(id, null, false, null, null, false);

    /// <summary>
    /// Create a context with an optional storage directory. When set, cookies are
    /// loaded from <c>{storageDir}/cookies.json</c> on creation.
    /// </summary>
    public static BrowserContext WithStorage(string id, string? storageDir) =>
        new(id, null, false, null, storageDir, false);

    /// <summary>Create a context with full options including the storage directory.</summary>
    public static BrowserContext WithStorageFull(
        string id,
        string? proxyUrl,
        bool stealth,
        string? userAgent,
        string? storageDir) =>
        new(id, proxyUrl, stealth, userAgent, storageDir, false);

    /// <summary>
    /// Variant that also accepts the <c>allow_private_network</c> opt-in. Every other
    /// constructor defaults it to false.
    /// </summary>
    public static BrowserContext WithStorageAndNetwork(
        string id,
        string? proxyUrl,
        bool stealth,
        string? userAgent,
        string? storageDir,
        bool allowPrivateNetwork) =>
        new(id, proxyUrl, stealth, userAgent, storageDir, allowPrivateNetwork);

    public static BrowserContext WithOptions(string id, string? proxyUrl, bool stealth) =>
        WithFullOptions(id, proxyUrl, stealth, null);

    public static BrowserContext WithFullOptions(
        string id,
        string? proxyUrl,
        bool stealth,
        string? userAgent) =>
        new(id, proxyUrl, stealth, userAgent, null, false);

    public static BrowserContext WithProxy(string id, string? proxyUrl) =>
        WithOptions(id, proxyUrl, false);

    /// <summary>
    /// Create a context with the same browser configuration but independent mutable
    /// network state.
    /// </summary>
    /// <remarks>
    /// Persistent copies start with the template's current cookies; incognito copies
    /// start empty and never write to the template's storage directory.
    /// </remarks>
    public BrowserContext IsolatedCopy(string id, bool persistent)
    {
        var copy = new BrowserContext(
            id,
            ProxyUrl,
            Stealth,
            UserAgent,
            persistent ? StorageDir : null,
            AllowPrivateNetwork,
            restoreCookiesFromDisk: false,
            // localStorage belongs to the profile, so a persistent copy shares the template's
            // store: two contexts on one --storage-dir would otherwise overwrite each other's
            // areas (upstream f661c2e). An incognito copy starts empty.
            localStorage: persistent ? LocalStorage : new PocketCalculator.Js.Ops.WebStorage(),
            // upstream 3dea548: IndexedDB too, a persistent copy shares it and an incognito
            // one starts empty.
            indexedDb: persistent ? IndexedDb : null)
        {
            ObeyRobots = ObeyRobots,
            AllowFileAccess = AllowFileAccess,
        };
        if (persistent)
        {
            // A raw copy, so host-only cookies stay host-only. Going through the CDP
            // model would widen every one of them to its subdomains.
            copy.CookieJar.CopyFrom(CookieJar);
        }
        return copy;
    }

    /// <summary>
    /// A CDP connection's private state (upstream 9a5d9c6): the template's cookies, and a
    /// snapshot of its localStorage that the connection can change without touching another
    /// connection's. No storage directory: only the server's persistence context writes the
    /// profile, merging each closed connection's changes in.
    /// </summary>
    public BrowserContext ConnectionCopy(string id)
    {
        var copy = new BrowserContext(
            id,
            ProxyUrl,
            Stealth,
            UserAgent,
            null,
            AllowPrivateNetwork,
            restoreCookiesFromDisk: false,
            localStorage: LocalStorage.IsolatedSnapshot(),
            // upstream 3dea548: a connection's IndexedDB starts empty and is its own.
            indexedDb: null)
        {
            ObeyRobots = ObeyRobots,
            AllowFileAccess = AllowFileAccess,
        };
        copy.CookieJar.CopyFrom(CookieJar);
        return copy;
    }

    /// <summary>
    /// Flush the localStorage areas changed since the last save to
    /// <c>{storage_dir}/localStorage</c> (upstream f661c2e, 677f6d2). A failed write keeps
    /// its area changed, so the next save retries it. Returns the failures, each written to
    /// stderr.
    /// </summary>
    public IReadOnlyList<string> SaveLocalStorage()
    {
        if (StorageDir is null)
        {
            return [];
        }
        var failures = WebStorageProfile.Save(LocalStorage, StorageDir);
        foreach (var failure in failures)
        {
            try
            {
                Console.Error.WriteLine($"WARN {failure}");
            }
            catch (IOException)
            {
            }
        }
        return failures;
    }

    /// <summary>Persist cookies and localStorage when a storage directory is configured.</summary>
    public void SaveStorage()
    {
        SaveLocalStorage();
        SaveCookies();
    }

    /// <summary>
    /// Persist cookies to disk when a storage directory is configured. Returns the error
    /// message when the save failed, after writing it to stderr, or null.
    /// </summary>
    /// <remarks>
    /// Upstream 1b259b9: a failed save used to be dropped silently here (Rust logged it at
    /// warn); it is now reported at error level and says the cookies will not be kept. The
    /// directory and file modes it also added were already the port's (SECURITY.md I4).
    /// </remarks>
    public string? SaveCookies()
    {
        if (StorageDir is null)
        {
            return null;
        }
        var cookiePath = Path.Combine(StorageDir, "cookies.json");
        try
        {
            // Owner-only when this creates it (SECURITY.md I4).
            PocketCalculator.Net.CookieJar.CreateOwnerOnlyDirectory(StorageDir);
            CookieJar.SaveToFile(cookiePath);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or NotSupportedException or ArgumentException)
        {
            // Shutdown must not fail because the cookie file could not be written, but
            // the operator has to hear about it.
            var message = $"Could not save cookies to {cookiePath}: {error.Message}. "
                + "They will not be persisted, so the next run starts without them.";
            try
            {
                Console.Error.WriteLine($"ERROR {message}");
            }
            catch (IOException)
            {
            }
            return message;
        }
    }
}
