using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using PocketCalculator.Dom;
using PocketCalculator.Js.Ops;
using PocketCalculator.Js.Runtime;
using PocketCalculator.Js.Url;
using PocketCalculator.Net;
using PocketCalculator.Render.Css;

namespace PocketCalculator.Browser;

/// <summary>One network request/response this page observed.</summary>
public sealed class NetworkEvent
{
    public required string RequestId { get; init; }

    public required string Url { get; init; }

    public required string Method { get; init; }

    public required string ResourceType { get; init; }

    public required int Status { get; init; }

    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>Shared, so a large header map is not copied per observer.</summary>
    public required IReadOnlyDictionary<string, string> ResponseHeaders { get; init; }

    public required int BodySize { get; init; }

    public required double Timestamp { get; init; }

    /// <summary>
    /// The page's own document request, as opposed to a subresource. <see cref="Url"/> is
    /// then the URL the document came from, after any redirects.
    /// </summary>
    /// <remarks>
    /// Port addition. The CDP layer used to find the document by matching
    /// <see cref="Url"/> against the page URL, which found nothing once a redirect had
    /// moved the page, so no request carried the loader id and a client's
    /// <c>page.goto()</c> resolved to null.
    /// </remarks>
    public bool IsNavigation { get; init; }

    /// <summary>The redirects the document request followed, in order.</summary>
    public IReadOnlyList<NetworkRedirect> Redirects { get; init; } = [];

    /// <summary>
    /// The request was paused through Fetch interception, which already announced it
    /// (<c>Network.requestWillBeSent</c> and <c>Fetch.requestPaused</c>) under this
    /// <see cref="RequestId"/>; the CDP layer must not announce it again.
    /// </summary>
    public bool Intercepted { get; init; }

    /// <summary>
    /// Set when the request failed without a response (upstream 0155807); CDP reports
    /// <c>Network.loadingFailed</c> with it instead of a response.
    /// </summary>
    public string? ErrorText { get; init; }
}

/// <summary>One redirect a request followed: the URL it asked for and the response that sent it on.</summary>
/// <param name="Url">The URL this hop requested, in the page's URL spelling.</param>
/// <param name="Status">The redirect status.</param>
/// <param name="Headers">The redirect response's headers.</param>
public sealed record NetworkRedirect(string Url, int Status, IReadOnlyDictionary<string, string> Headers);

/// <summary>A response body retained for <c>Network.getResponseBody</c>.</summary>
public sealed record StoredResponseBody(string Body, bool Base64Encoded);

/// <summary>Which <c>PageError</c> variant a <see cref="PageException"/> carries.</summary>
public enum PageErrorKind
{
    InvalidUrl,
    NetworkError,
    ParseError,

    /// <summary>
    /// A page kept triggering its own navigations until the chain's limit was
    /// exhausted. HTTP 3xx redirects are followed one layer down, in
    /// <c>PocketCalculator.Net</c>, and report <c>TooManyRedirects</c>.
    /// </summary>
    TooManyClientNavigations,
}

/// <summary>Port of <c>PageError</c>.</summary>
public sealed class PageException : Exception
{
    public PageException(PageErrorKind kind, string message)
        : base(message) => Kind = kind;

    public PageException(PageErrorKind kind, string message, int chainLimit)
        : base(message)
    {
        Kind = kind;
        ChainLimit = chainLimit;
    }

    public PageErrorKind Kind { get; }

    /// <summary>Meaningful only for <see cref="PageErrorKind.TooManyClientNavigations"/>.</summary>
    public int ChainLimit { get; }

    internal static PageException InvalidUrl(string detail) =>
        new(PageErrorKind.InvalidUrl, $"Invalid URL: {detail}");

    internal static PageException Network(string detail) =>
        new(PageErrorKind.NetworkError, $"Network error: {detail}");

    internal static PageException Parse(string detail) =>
        new(PageErrorKind.ParseError, $"Parse error: {detail}");

    internal static PageException TooManyClientNavigations(int limit) =>
        new(
            PageErrorKind.TooManyClientNavigations,
            $"Too many client-initiated navigations, the chain reached its limit of {limit.ToString(CultureInfo.InvariantCulture)} documents",
            limit);
}

/// <summary>Metrics captured when CDP device emulation is first enabled.</summary>
/// <remarks>
/// Chromium keeps this baseline across subsequent override calls and restores it
/// only when the override is cleared.
/// </remarks>
internal readonly record struct DeviceMetricsBaseline(
    (float Width, float Height) Viewport,
    float DeviceScaleFactor);

/// <summary>A frame document moving through Page-owned realm construction.</summary>
internal abstract class PendingFrameWork
{
    internal abstract uint FrameId { get; }

    internal abstract uint ParentFrameId { get; }

    internal sealed class Unattached(PendingFrame frame) : PendingFrameWork
    {
        internal PendingFrame Frame { get; } = frame;

        internal override uint FrameId => Frame.FrameId;

        internal override uint ParentFrameId => Frame.ParentFrameId;
    }

    internal sealed class Attached : PendingFrameWork
    {
        internal required uint Id { get; init; }

        internal required uint ParentId { get; init; }

        internal required string FrameUrl { get; init; }

        internal required IReadOnlyList<string> Urls { get; init; }

        internal int NextUrl { get; set; }

        internal Dictionary<string, string> Sources { get; } = new(StringComparer.Ordinal);

        internal override uint FrameId => Id;

        internal override uint ParentFrameId => ParentId;
    }
}

/// <summary>
/// One browser page: its document, its JavaScript realm, its navigation
/// lifecycle, and the network bookkeeping the CDP layer reports.
/// </summary>
public sealed partial class Page : IDisposable
{
    private PocketCalculatorJsRuntime? _js;
    private (float Width, float Height)? _screenSizeOverride;
    private bool _screenMetricsEmulated;
    private DeviceMetricsBaseline? _deviceMetricsBaseline;
    private RgbaColor? _defaultBackgroundColorOverride;
    private long _documentTimelineOrigin = Stopwatch.GetTimestamp();
    private TimeSpan? _navigationTimeout;
    private int? _navigationChainLimit;
    private readonly Dictionary<string, StoredResponseBody> _responseBodies = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _responseBodyOrder = new();
    private uint _networkEventCounter;
    private IInterceptSink? _interceptTx;
    private List<string> _preloadScripts = [];
    private bool _runtimeEventsEnabled;
    internal readonly LinkedList<PendingFrameWork> _pendingFrameWork = new();
    private List<uint> _suspendedStartedScriptIds = [];
    private CdpObjectState _suspendedCdpObjectState = new();
    private readonly CallbackRegistry _callbacks = new();

    /// <summary>
    /// This page's <c>sessionStorage</c> areas (one top-level browsing context), which its
    /// navigations keep, as in Chromium.
    /// </summary>
    public PocketCalculator.Js.Ops.WebStorage SessionStorage { get; } = new();
    private bool _disposed;

    public Page(string id, BrowserContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Id = id;
        // Chromium convention: the main frame's frameId == the targetId.
        // Playwright's frame manager looks up the main frame by targetId, so any
        // divergence here makes Page.getFrameTree return a frame the client
        // cannot match, triggering a Target.closeTarget and "Frame has been
        // detached".
        FrameId = id;
        Context = context;
        HttpClient = context.HttpClient;
    }

    public string Id { get; }

    public string FrameId { get; }

    public UrlRecord? Url { get; set; }

    public DomTree? Dom { get; set; }

    /// <summary>How far the current document got (see <see cref="DocumentReadiness"/>).</summary>
    public DocumentReadiness Readiness { get; private set; } = DocumentReadiness.Loaded;

    /// <summary>
    /// The last navigation hit its deadline after the document committed, and the page
    /// was left as it stood rather than failed: its DOM is built, and whatever parsing,
    /// scripts or frames were still pending did not run. <see cref="Readiness"/> says how
    /// far the document got.
    /// </summary>
    public bool LoadAbandoned { get; private set; }

    /// <summary>Record that the current document reached <paramref name="readiness"/>.</summary>
    internal void ReachReadiness(DocumentReadiness readiness)
    {
        if (readiness > Readiness)
        {
            Readiness = readiness;
        }
    }

    /// <summary>Live child frame realms, in creation order.</summary>
    public List<FrameRealm> Frames { get; } = [];

    /// <summary>
    /// This page's JavaScript realm. Replacing it disposes the previous runtime,
    /// which is what dropping the Rust <c>Option&lt;PocketCalculatorJsRuntime&gt;</c> does.
    /// </summary>
    public PocketCalculatorJsRuntime? Js
    {
        get => _js;
        set
        {
            if (ReferenceEquals(_js, value))
            {
                return;
            }
            PocketCalculatorJsRuntime? previous = _js;
            _js = value;
            previous?.Dispose();
        }
    }

    public LifecycleState Lifecycle { get; set; } = LifecycleState.Idle;

    public PocketCalculatorHttpClient HttpClient { get; }

    public BrowserContext Context { get; }

    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Source document URL for the current document.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="Url"/>: direct automation navigations
    /// have no referrer, while a navigation requested by page script uses the
    /// previous document.
    /// </remarks>
    public string Referrer { get; set; } = string.Empty;

    /// <summary>
    /// The policy of the current document's <c>Referrer-Policy</c> response header, or null.
    /// Port addition (Rust has no referrer policy).
    /// </summary>
    public ReferrerPolicy? ReferrerPolicyHeader { get; set; }

    /// <summary>
    /// CSS viewport used by responsive page JavaScript and CDP screenshots. The
    /// physical <c>screen</c> fingerprint stays independent.
    /// </summary>
    public (float Width, float Height) Viewport { get; private set; } = (1280.0f, 720.0f);

    /// <summary>
    /// The clock every document's animation timeline reads. The system's monotonic clock
    /// unless a test installs one it advances by hand, which makes CSS and Web Animations land
    /// at exact times however slow the host is. Takes effect from the next document.
    /// </summary>
    public TimeProvider AnimationClock { get; set; } = TimeProvider.System;

    /// <summary>
    /// Output device pixels per CSS pixel for CDP surface capture. Layout and CSSOM
    /// stay in CSS pixels; <c>Emulation.setDeviceMetricsOverride</c> owns this
    /// independent raster scale.
    /// </summary>
    public float DeviceScaleFactor { get; private set; } = 1.0f;

    /// <summary>
    /// WHATWG canonical name of the current document's character encoding.
    /// </summary>
    /// <remarks>
    /// Detected when the response body is decoded, exposed to JS as
    /// <c>document.characterSet</c>, and used for the URL query encoding override on
    /// <c>&lt;a&gt;</c>/<c>&lt;area&gt;</c> hrefs in legacy-charset documents.
    /// </remarks>
    public string Encoding { get; set; } = "UTF-8";

    /// <summary>
    /// Navigation history for <c>Page.getNavigationHistory</c> /
    /// <c>navigateToHistoryEntry</c>, in visit order.
    /// </summary>
    public List<string> History { get; } = [];

    public int HistoryIndex { get; set; }

    /// <summary>
    /// Index of the current document's first history entry; the entries after it up to
    /// <see cref="HistoryIndex"/> are its same-document (pushState) URLs (upstream 1a0773d).
    /// </summary>
    private int _documentHistoryStart;

    /// <summary>
    /// The entry a navigation in flight traverses to: a page-initiated <c>history.go()</c>
    /// taken with its pending navigation, or a client's history navigation. Committing
    /// the load moves the cursor to the entry instead of appending one (upstream 1a0773d).
    /// </summary>
    private int? _pendingHistoryTraversal;

    /// <summary>
    /// The current document's navigation milestones (upstream 60107d1): a new record per
    /// navigation, handed to each runtime built for the document, so a resumed runtime keeps
    /// them.
    /// </summary>
    private NavigationTiming _navigationTiming = new();

    public List<NetworkEvent> NetworkEvents { get; } = [];

    public bool InterceptEnabled { get; private set; }

    public List<string> InterceptBlockPatterns { get; } = [];

    /// <summary>
    /// The current document's MIME type when it is not the URL-derived default: set for a
    /// plain-text document (upstream 22eb6b4), null otherwise.
    /// </summary>
    internal string? DocumentContentType { get; set; }

    public List<string> BlockedUrlPatterns { get; private set; } = [];

    /// <summary>
    /// Set the end-to-end navigation deadline for this page.
    /// </summary>
    /// <remarks>
    /// This page-scoped value takes precedence over <c>POCKETCALCULATOR_NAV_TIMEOUT_MS</c>;
    /// callers that do not set it keep the environment-configurable 30s default.
    /// </remarks>
    public void SetNavigationTimeout(TimeSpan timeout) => _navigationTimeout = timeout;

    /// <summary>The effective end-to-end navigation deadline for this page.</summary>
    public TimeSpan NavigationTimeout => _navigationTimeout ?? PageHelpers.DefaultNavigationTimeout();

    /// <summary>
    /// Takes precedence over <c>POCKETCALCULATOR_NAV_CHAIN_LIMIT</c>. A limit of 0 is raised
    /// to 1, which would otherwise report success having loaded nothing.
    /// </summary>
    public void SetNavigationChainLimit(int limit) => _navigationChainLimit = Math.Max(1, limit);

    public int NavigationChainLimit =>
        _navigationChainLimit ?? PageHelpers.DefaultNavigationChainLimitValue();

    internal bool ShouldBlockUrl(string url)
    {
        foreach (string pattern in BlockedUrlPatterns)
        {
            if (PageHelpers.UrlMatchesCdpPattern(pattern, url))
            {
                return true;
            }
        }
        if (InterceptEnabled)
        {
            foreach (string pattern in InterceptBlockPatterns)
            {
                if (PocketCalculator.Js.Ops.FetchOps.FetchPatternMatches(pattern, url))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Update the page's CSS viewport.
    /// </summary>
    /// <remarks>
    /// Calling this before navigation makes responsive scripts observe it from their
    /// first instruction; calling it on a live page mirrors CDP's device-metrics
    /// override surfaces.
    /// </remarks>
    public void SetViewport((float Width, float Height) viewport)
    {
        if (!float.IsFinite(viewport.Width) || !float.IsFinite(viewport.Height)
            || viewport.Width <= 0.0f || viewport.Height <= 0.0f)
        {
            return;
        }
        Viewport = viewport;
        Js?.SetViewport(viewport.Width, viewport.Height);
    }

    /// <summary>Set or clear the CDP physical-screen override independently of layout.</summary>
    public void SetScreenSizeOverride((float Width, float Height)? size, bool emulated)
    {
        _screenSizeOverride = size is { } value
            && float.IsFinite(value.Width) && float.IsFinite(value.Height)
            && value.Width > 0.0f && value.Height > 0.0f
                ? value
                : null;
        _screenMetricsEmulated = emulated;
        Js?.SetScreenSizeOverride(
            _screenSizeOverride is { } screen ? ((double)screen.Width, (double)screen.Height) : null,
            _screenMetricsEmulated);
    }

    /// <summary>
    /// Apply CDP device metrics relative to the metrics active when emulation was
    /// first enabled. A zero protocol dimension/scale arrives as null and therefore
    /// restores that axis from the retained baseline.
    /// </summary>
    public void ApplyDeviceMetricsOverride(
        float? width,
        float? height,
        float? deviceScaleFactor,
        (float Width, float Height)? screenSize,
        bool mobile)
    {
        _deviceMetricsBaseline ??= new DeviceMetricsBaseline(Viewport, DeviceScaleFactor);
        DeviceMetricsBaseline baseline = _deviceMetricsBaseline.Value;
        (float Width, float Height) viewport = (
            width ?? baseline.Viewport.Width,
            height ?? baseline.Viewport.Height);
        SetViewport(viewport);

        // Blink uses the effective widget size as the screen size for mobile
        // emulation when no complete explicit screen size was supplied.
        (float Width, float Height)? effectiveScreenSize =
            screenSize ?? (mobile ? viewport : null);
        SetScreenSizeOverride(effectiveScreenSize, true);
        SetDeviceScaleFactor(deviceScaleFactor ?? baseline.DeviceScaleFactor);
    }

    /// <summary>
    /// Disable CDP device metrics and restore the state captured by the first
    /// override. Clearing while emulation is inactive is intentionally a no-op.
    /// </summary>
    public void ClearDeviceMetricsOverride()
    {
        if (_deviceMetricsBaseline is not { } baseline)
        {
            return;
        }
        _deviceMetricsBaseline = null;
        SetViewport(baseline.Viewport);
        SetScreenSizeOverride(null, false);
        SetDeviceScaleFactor(baseline.DeviceScaleFactor);
    }

    /// <summary>
    /// Set the screenshot surface density without changing CSS layout. CDP uses zero
    /// to disable its override, which restores the native 1x surface.
    /// </summary>
    public void SetDeviceScaleFactor(float deviceScaleFactor)
    {
        if (!float.IsFinite(deviceScaleFactor) || deviceScaleFactor < 0.0f)
        {
            return;
        }
        DeviceScaleFactor = deviceScaleFactor == 0.0f ? 1.0f : deviceScaleFactor;
        if (Js is { } js)
        {
            TryExecute(js, "<device-metrics>", DevicePixelRatioScript());
        }
    }

    public void SetDefaultBackgroundColorOverride(RgbaColor? color) =>
        _defaultBackgroundColorOverride = color;

    private (bool ReducedMotion, bool Dark, string MediaType) _mediaPreferences = (false, false, string.Empty);

    /// <summary>
    /// Emulate a media type and the reduced-motion and dark color-scheme preferences together
    /// (upstream c9978c3 / c7ec60a <c>set_media_preferences</c>). They persist across
    /// navigation and reach the page's frames.
    /// </summary>
    /// <param name="reducedMotion">Whether <c>prefers-reduced-motion: reduce</c> matches.</param>
    /// <param name="dark">Whether <c>prefers-color-scheme: dark</c> matches.</param>
    /// <param name="mediaType">The emulated media type; empty for none. The port's addition.</param>
    public void SetMediaPreferences(bool reducedMotion, bool dark, string mediaType = "")
    {
        _mediaPreferences = (reducedMotion, dark, mediaType ?? string.Empty);
        Js?.SetMediaPreferences(reducedMotion, dark, _mediaPreferences.MediaType);
    }

    internal RgbaColor CaptureSurfaceColor() =>
        _defaultBackgroundColorOverride ?? new RgbaColor(255, 255, 255, 255);

    private string DevicePixelRatioScript() =>
        "globalThis.devicePixelRatio="
        + DeviceScaleFactor.ToString("R", CultureInfo.InvariantCulture)
        + ";";

    internal Task<Response> DoFetchAsync(UrlRecord url, CancellationToken cancellationToken) =>
        HttpClient.FetchWithCallbacksAsync(NetUrl.From(url), _callbacks, cancellationToken);

    /// <summary>
    /// Rebuild the JavaScript realm for a new document.
    /// </summary>
    /// <remarks>
    /// The old code reused the V8 isolate and only re-bound <c>globalThis.document</c>,
    /// leaving window.onload, custom window properties and event handlers from the
    /// prior page in place. That let a page set attacker-controlled state, trigger a
    /// navigation, and then run code in the next document's context.
    /// </remarks>
    internal void InitJs()
    {
        // InitJs is also the new-document path. Only ResumeJs takes these ids out
        // before entering here and restores them after the same DomTree is
        // installed; a navigation must never inherit ids from a suspended prior
        // document whose allocator may reuse them.
        _suspendedStartedScriptIds.Clear();
        _suspendedCdpObjectState = new CdpObjectState();
        _pendingFrameWork.Clear();
        if (Js is not null)
        {
            // Every frame realm holds a V8 handle into this isolate, so the frames of
            // the outgoing document must go before the runtime does.
            Frames.Clear();
            Js = null;

            // The outgoing document's DOM, computed styles, layout and shaped text
            // all became garbage on the line above, and this is the low point of the
            // navigation: the new document is parsed but not yet laid out. Releasing
            // here reclaims the most for the least compaction work.
            PageHelpers.ReleaseReplacedDocumentMemory();
        }

        // Thread the BrowserContext's proxy through to the ES-module loader and
        // op_fetch_url so dynamic imports and JS fetch() honour the configured
        // upstream proxy. A null proxy is a direct connection.
        var rt = TakePrewarmedRuntime() ?? PocketCalculatorJsRuntime.WithBaseUrlAndProxy(UrlString(), Context.ProxyUrl);
        rt.State.AnimationClock = AnimationClock;
        rt.SetUrl(UrlString());
        rt.SetEncoding(Encoding);
        rt.SetTitle(Title);
        rt.SetReferrer(Referrer);
        rt.SetReferrerPolicyHeader(ReferrerPolicyHeader);
        rt.NavigationTiming = _navigationTiming;
        (List<string> sessionHistory, int sessionIndex) = PredictedSessionHistory();
        rt.SetSessionHistory(sessionHistory, sessionIndex, sessionIndex);

        rt.SetUserAgent(HttpClient.UserAgent);
        rt.SetPlatform(Context.Platform, Context.UaPlatform, Context.UaPlatformVersion);

        if (PageHelpers.EnvGeolocation() is { } geolocation)
        {
            rt.SetGeolocation(geolocation.Latitude, geolocation.Longitude);
        }
        rt.SetViewport(Viewport.Width, Viewport.Height);
        rt.SetScreenSizeOverride(
            _screenSizeOverride is { } screen ? ((double)screen.Width, (double)screen.Height) : null,
            _screenMetricsEmulated);

        rt.SetCookieJar(Context.CookieJar);
        rt.SetWebStorage(Context.LocalStorage, SessionStorage);
        rt.SetIndexedDb(Context.IndexedDb);
        rt.SetConsoleMessagesEnabled(ConsoleMessagesEnabled);
        rt.SetDocumentContentType(DocumentContentType);
        rt.SetHttpClient(HttpClient);
        rt.SetCallbacks(_callbacks);
        rt.SetBlockedUrls(BlockedUrlPatterns);

        if (_interceptTx is { } sink)
        {
            rt.SetInterceptSink(sink);
        }
        // Re-apply InterceptEnabled: EnableInterception()/EnableIntercept() called
        // before the first navigation sets this on the Page while the runtime does
        // not exist yet, so a new runtime would otherwise start with interception
        // disabled and op_fetch_url would never intercept.
        rt.SetInterceptEnabled(InterceptEnabled);
        rt.SetInterceptBlockPatterns(InterceptBlockPatterns);
        rt.SetRuntimeEventsEnabled(_runtimeEventsEnabled);

        if (Dom is { } dom)
        {
            Dom = null;
            rt.SetDom(dom);
        }

        rt.RunPageInit();
        if (_mediaPreferences is not (false, false, ""))
        {
            rt.SetMediaPreferences(_mediaPreferences.ReducedMotion, _mediaPreferences.Dark, _mediaPreferences.MediaType);
        }
        TryExecute(rt, "<device-metrics>", DevicePixelRatioScript());

        Js = rt;
    }

    /// <summary>A runtime being created while the navigation that will use it is fetched.</summary>
    private RuntimePrewarm? _runtimePrewarm;

    private static readonly bool RuntimePrewarmDisabled =
        Environment.GetEnvironmentVariable("POCKETCALCULATOR_NO_RUNTIME_PREWARM") == "1";

    /// <summary>
    /// Start creating the next document's runtime now, on another thread, while its response
    /// is fetched. Creating one (a V8 isolate and bootstrap.js) takes about 90 ms, which
    /// otherwise delays the document's first script by as much once the response is in.
    /// </summary>
    /// <remarks>
    /// DEVIATION from crates/obscura-browser, which creates the runtime once the document has
    /// been parsed. <c>POCKETCALCULATOR_NO_RUNTIME_PREWARM=1</c> turns it off.
    /// </remarks>
    private void PrewarmRuntime(string url)
    {
        DiscardPrewarmedRuntime();
        if (RuntimePrewarmDisabled)
        {
            return;
        }

        string? proxy = Context.ProxyUrl;
        _runtimePrewarm = new RuntimePrewarm(() => PocketCalculatorJsRuntime.WithBaseUrlAndProxy(url, proxy));
    }

    /// <summary>The runtime <see cref="PrewarmRuntime"/> started, once it exists; null without one.</summary>
    private PocketCalculatorJsRuntime? TakePrewarmedRuntime()
    {
        if (Interlocked.Exchange(ref _runtimePrewarm, null) is not { } pending)
        {
            return null;
        }

        try
        {
            PocketCalculatorJsRuntime runtime = pending.Take();
            runtime.RebaseModuleLoader(UrlString());
            return runtime;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Created here instead, which reports the failure the usual way.
            return null;
        }
    }

    /// <summary>A navigation that will not use the runtime it started disposes it.</summary>
    private void DiscardPrewarmedRuntime() => Interlocked.Exchange(ref _runtimePrewarm, null)?.Discard();

    /// <summary>
    /// A runtime created on a pool thread ahead of need, or on the taker's thread when the pool
    /// has not got to it yet.
    /// </summary>
    /// <remarks>
    /// The navigation used to block on the prewarm <see cref="Task"/>
    /// (<c>GetAwaiter().GetResult()</c>), which waits for a pool thread to pick the work up;
    /// on a pool with no thread to spare that was a wait for the pool to grow. Taking it
    /// through a <see cref="Lazy{T}"/> creates the runtime inline if no thread has started it,
    /// and otherwise waits only for the thread that is already creating it.
    /// </remarks>
    private sealed class RuntimePrewarm
    {
        private readonly Lazy<PocketCalculatorJsRuntime> _runtime;
        private readonly Task _started;

        public RuntimePrewarm(Func<PocketCalculatorJsRuntime> create)
        {
            _runtime = new Lazy<PocketCalculatorJsRuntime>(create, LazyThreadSafetyMode.ExecutionAndPublication);
            _started = Task.Run(() => _runtime.Value);
        }

        public PocketCalculatorJsRuntime Take() => _runtime.Value;

        public void Discard() =>
            _ = _started.ContinueWith(
                _ =>
                {
                    if (_runtime.IsValueCreated)
                    {
                        _runtime.Value.Dispose();
                    }
                },
                TaskScheduler.Default);
    }

    /// <summary>
    /// Resolve the document base URL per the HTML spec, falling back to
    /// <see cref="Url"/> when no <c>&lt;base href&gt;</c> exists.
    /// </summary>
    internal UrlRecord? ResolveBaseUrl()
    {
        if (Url is not { } documentUrl)
        {
            return null;
        }
        string? baseHref = Js?.WithDom(dom =>
        {
            NodeId? node = dom.TryQuerySelector("base[href]", out NodeId? found, out _) ? found : null;
            return node is { } id ? dom.GetNode(id)?.GetAttribute("href") : null;
        });
        return baseHref is null ? documentUrl : PageUrl.TryJoin(documentUrl, baseHref);
    }

    public string UrlString() => Url?.Href ?? "about:blank";

    /// <summary>
    /// The serialized origin of the loaded document as the host committed it, <c>"null"</c>
    /// when opaque. Nothing the page does moves it (<c>history.pushState</c> cannot leave
    /// the origin, and <c>location.origin</c> is page-replaceable).
    /// </summary>
    public string DocumentOrigin() => Js is { } js
        ? StateHelpers.DocumentOrigin(js.State)
        : Url?.AsciiOrigin ?? "null";

    public T? WithDom<T>(Func<DomTree, T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (Js is { } js)
        {
            return js.WithDom(body);
        }
        return Dom is { } dom ? body(dom) : default;
    }

    /// <summary>
    /// Append the current URL to the history stack, truncating any forward entries
    /// past the cursor (matching Chrome: navigating after a goBack clobbers the
    /// forward history).
    /// </summary>
    /// <remarks>
    /// Upstream 1a0773d: a navigation that traverses to an entry (<c>history.go()</c>, a
    /// client's back or forward) moves the cursor to it instead, and the new document's
    /// <c>window.history</c> is handed the result.
    /// </remarks>
    public void PushHistory(string url)
    {
        if (url.Length == 0)
        {
            return;
        }

        int? traversal = _pendingHistoryTraversal;
        _pendingHistoryTraversal = null;
        if (traversal is { } index && index < History.Count
            && string.Equals(History[index], url, StringComparison.Ordinal))
        {
            HistoryIndex = index;
        }
        else if (!(HistoryIndex < History.Count
            && string.Equals(History[HistoryIndex], url, StringComparison.Ordinal)))
        {
            // Consecutive duplicates are skipped so Page.reload does not pile up entries.
            if (History.Count != 0 && HistoryIndex < History.Count - 1)
            {
                History.RemoveRange(HistoryIndex + 1, History.Count - HistoryIndex - 1);
            }
            History.Add(url);
            HistoryIndex = History.Count - 1;
        }

        _documentHistoryStart = HistoryIndex;
        if (AdoptDocumentHistory())
        {
            return;
        }
        SyncJsSessionHistory();
    }

    /// <summary>
    /// Take the document's own history moves into <see cref="History"/> when the session
    /// history it was handed at creation is the one just committed. A document that called
    /// <c>pushState</c> while it loaded has entries past its first one, and the host's
    /// copy of them (op_history_url) is the record. Port addition: upstream overwrote them
    /// with the page's history on commit.
    /// </summary>
    private bool AdoptDocumentHistory()
    {
        if (Js is not { } js || js.SessionHistory is not { IsEmpty: false } mirror
            || mirror.DocumentStart != _documentHistoryStart
            || mirror.DocumentStart >= History.Count
            || mirror.Urls.Count <= mirror.DocumentStart)
        {
            return false;
        }

        for (int i = 0; i <= mirror.DocumentStart; i++)
        {
            if (!string.Equals(mirror.Urls[i], History[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (mirror.Urls.Count == History.Count && mirror.Current == HistoryIndex)
        {
            return true;
        }

        History.Clear();
        History.AddRange(mirror.Urls);
        HistoryIndex = mirror.Current;
        return true;
    }

    /// <summary>
    /// Copy the document's same-document entries, as the host recorded them through
    /// <c>op_history_url</c>, into <see cref="History"/> (upstream 1a0773d
    /// <c>push_same_document_history</c>, 0ecc417 <c>apply_history_update</c>). The copy
    /// is the page's history with this document's entries spliced in, so it replaces
    /// <see cref="History"/> whole, which also keeps a pushState from being counted twice.
    /// </summary>
    private void AdoptDocumentHistoryEntries(PocketCalculatorJsRuntime js, bool moved)
    {
        if (!moved || js.SessionHistory is not { IsEmpty: false } mirror)
        {
            return;
        }

        History.Clear();
        History.AddRange(mirror.Urls);
        HistoryIndex = mirror.Current;
        _documentHistoryStart = mirror.DocumentStart;
    }

    /// <summary>
    /// Replace the history and cursor, treating the current document as the entry at
    /// <paramref name="index"/> (upstream 1a0773d <c>set_history</c>). Client history
    /// navigations and <c>Page.resetNavigationHistory</c> rewrite history through this.
    /// </summary>
    public void SetHistory(IEnumerable<string> history, int index)
    {
        ArgumentNullException.ThrowIfNull(history);
        List<string> entries = [.. history];
        History.Clear();
        History.AddRange(entries);
        HistoryIndex = index;
        _documentHistoryStart = index;
        SyncJsSessionHistory();
    }

    /// <summary>
    /// Give the document's <c>window.history</c> the page's session history, so
    /// <c>history.length</c> and a cross-document <c>history.go()</c> see every entry
    /// (upstream 1a0773d).
    /// </summary>
    public void SyncJsSessionHistory()
    {
        if (Js is { } js)
        {
            js.SetSessionHistory(History, _documentHistoryStart, HistoryIndex);
        }
    }

    /// <summary>
    /// The session history as it will be once the document being created commits through
    /// <see cref="PushHistory"/>: the entry a traversal targets, the current entry on a
    /// reload, or a new entry replacing the forward ones (upstream 1a0773d).
    /// </summary>
    private (List<string> Urls, int Index) PredictedSessionHistory()
    {
        string url = UrlString();
        if (_pendingHistoryTraversal is { } index && index < History.Count
            && string.Equals(History[index], url, StringComparison.Ordinal))
        {
            return ([.. History], index);
        }
        if (HistoryIndex < History.Count && string.Equals(History[HistoryIndex], url, StringComparison.Ordinal))
        {
            return ([.. History], HistoryIndex);
        }
        if (url.Length == 0)
        {
            return ([], 0);
        }

        List<string> urls = [.. History.Take(Math.Min(History.Count, HistoryIndex + 1))];
        urls.Add(url);
        return (urls, urls.Count - 1);
    }

    /// <summary>
    /// Traverse to session history entry <paramref name="index"/> when it belongs to the
    /// live document, without replacing its runtime (upstream 0ecc417): the document fires
    /// <c>popstate</c> and keeps its state. Null when the entry is not the document's own
    /// (or is the current one), and the caller loads it as a document instead.
    /// </summary>
    public PageNavigationOutcome? TraverseSameDocumentHistory(int index)
    {
        SyncVirtualUrl();
        if (Js is not { } js || js.SessionHistory is not { IsEmpty: false } mirror
            || index < 0 || index >= History.Count || index == HistoryIndex
            || index < mirror.DocumentStart || index >= mirror.DocumentStart + mirror.DocumentLength)
        {
            return null;
        }

        string literal = System.Text.Json.JsonSerializer.Serialize(History[index]);
        if (EvaluateHost(string.Create(CultureInfo.InvariantCulture, $"__obscura_host.historyTraverse({index}, {literal})"))
            ?.GetValueKind() != System.Text.Json.JsonValueKind.True)
        {
            return null;
        }

        PageNavigationOutcome outcome = SyncVirtualUrl();
        // Entries pushed with the URL they already had move nothing the host reports, but
        // the document did traverse.
        return outcome.IsSameDocument ? outcome : PageNavigationOutcome.SameDocument(PageNavigationOutcome.HistoryApiType);
    }

    /// <summary>
    /// <c>Page.resetNavigationHistory</c>: only the current entry is left, in the page and
    /// in the document's <c>window.history</c> (upstream 0ecc417; Chromium 1243 reports
    /// <c>history.length</c> 1 after it).
    /// </summary>
    public void ResetHistory()
    {
        SyncVirtualUrl();
        EvaluateHost("__obscura_host.historyReset()");
        string url = UrlString();
        SetHistory(url.Length == 0 ? [] : [url], 0);
    }

    /// <summary>
    /// Navigate to session history entry <paramref name="index"/> on a client's behalf
    /// (CDP <c>Page.navigateToHistoryEntry</c>, MCP back and forward): the load moves the
    /// cursor to the entry, and a load that fails leaves history as it was.
    /// </summary>
    public async Task NavigateToHistoryEntryAsync(int index, WaitUntil waitUntil, CancellationToken cancellationToken = default)
    {
        if (index < 0 || index >= History.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _pendingHistoryTraversal = index;
        try
        {
            await NavigateWithWaitAsync(History[index], waitUntil, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pendingHistoryTraversal = null;
        }
    }

    /// <summary>
    /// Move the history cursor without re-navigating; used by
    /// <c>Page.navigateToHistoryEntry</c>, which then drives the actual fetch.
    /// </summary>
    public void SetHistoryIndex(int index)
    {
        if (index >= 0 && index < History.Count)
        {
            HistoryIndex = index;
        }
    }

    /// <summary>
    /// Fork-only: adopt a URL the page routed to itself, without fetching anything.
    /// </summary>
    /// <remarks>
    /// A single page app answers a click by calling <c>history.pushState</c> and
    /// rendering the next view in place. <c>bootstrap.js</c> tracks that (upstream in
    /// <c>__virtualUrl</c>, here reported through <c>op_history_url</c>) so <c>location.href</c> reads correctly, but nothing on
    /// the host side looked at it, so the page had moved on while
    /// <c>page.url()</c> still reported the old document. Adopting the URL is
    /// unchanged; what the outcome adds is that the change is classified, because
    /// nothing fetched a document here and CDP has to say so.
    /// <para>
    /// Deviation from <c>crates/obscura-browser/src/fork_virtual_url.rs</c>: Rust's
    /// <c>sync_virtual_url</c> answers a bare <c>bool</c> and its CDP layer turns
    /// that into <c>Page.frameNavigated</c>, which in CDP means a new document, so
    /// every client retires the frame's execution contexts and the next
    /// <c>Runtime.evaluate</c> fails with "Execution context was destroyed". The
    /// C# port answers the kind instead so the CDP layer can emit
    /// <c>Page.navigatedWithinDocument</c>. The Rust tree has the same gap; this is
    /// a fix, not a port correction.
    /// </para>
    /// </remarks>
    public PageNavigationOutcome SyncVirtualUrl()
    {
        if (Js is not { } js)
        {
            return PageNavigationOutcome.None;
        }
        // DEVIATION from fork_virtual_url.rs, which evaluates the page-writable global
        // __virtualUrl: a page could set it to any URL and move Page.Url and what CDP and
        // MCP report (SECURITY.md L9). The host keeps the History API URL itself, checked
        // against the committed document URL when the page moved it.
        // A traversal back to the document's first entry clears the History API URL: the
        // document is at its committed URL again (port: upstream reads '' as no change and
        // leaves the page on the entry it came from until 0ecc417's take()).
        bool moved = js.TakeHistoryChanged();
        AdoptDocumentHistoryEntries(js, moved);
        string? virtualUrl = js.HistoryUrl ?? (moved ? js.CommittedUrl : null);
        if (string.IsNullOrEmpty(virtualUrl) || PageUrl.TryParse(virtualUrl) is not { } parsed)
        {
            return PageNavigationOutcome.None;
        }
        if (Url is { } current && string.Equals(current.Href, parsed.Href, StringComparison.Ordinal))
        {
            return PageNavigationOutcome.None;
        }
        UrlRecord? previous = Url;
        Url = parsed;
        if (js.SessionHistory.IsEmpty)
        {
            // No session history was handed to this document (none was recorded yet):
            // record the URL as before.
            PushHistory(UrlString());
        }

        // No document was fetched, so the DOM, the JS realm and every execution context
        // built on it survive: whichever API moved the URL, this is a same-document
        // navigation. Chrome tells the two apart only in the reported navigationType.
        bool fragmentOnly = previous is { } before && IsFragmentOnlyChange(before, parsed);

        return PageNavigationOutcome.SameDocument(fragmentOnly
            ? PageNavigationOutcome.FragmentType
            : PageNavigationOutcome.HistoryApiType);
    }

    /// <summary>Whether two URLs differ in their fragment and in nothing else.</summary>
    private static bool IsFragmentOnlyChange(UrlRecord before, UrlRecord after) =>
        !string.Equals(before.Fragment, after.Fragment, StringComparison.Ordinal)
        && string.Equals(
            PageUrl.WithoutFragment(before).Href,
            PageUrl.WithoutFragment(after).Href,
            StringComparison.Ordinal);

    /// <summary>Runs a host script, swallowing a page-level failure as Rust's <c>let _ =</c> does.</summary>
    internal static void TryExecute(PocketCalculatorJsRuntime js, string name, string source)
    {
        try
        {
            js.ExecuteScript(name, source);
        }
        catch (JsRuntimeException)
        {
            // A broken page must degrade, never throw out of navigation.
        }
    }

    /// <summary>
    /// <see cref="TryExecute"/> for a host script that uses the realm's host helpers as
    /// <c>__obscura_host</c> (see <see cref="HostScript"/>).
    /// </summary>
    internal static void TryExecuteHost(PocketCalculatorJsRuntime js, string name, string source)
    {
        try
        {
            js.ExecuteHostScript(name, source);
        }
        catch (JsRuntimeException)
        {
            // A broken page must degrade, never throw out of navigation.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        DiscardPrewarmedRuntime();
        _pendingFrameWork.Clear();
        Frames.Clear();
        Js = null;

        // A closed target is the other point a document dies wholesale; a CDP client
        // that opens a tab per route would otherwise keep every one of them committed.
        PageHelpers.ReleaseReplacedDocumentMemory();
    }
}
