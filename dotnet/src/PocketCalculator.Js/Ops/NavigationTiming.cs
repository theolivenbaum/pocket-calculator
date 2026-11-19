using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PocketCalculator.Js.Ops;

/// <summary>
/// A document's observed navigation milestones (upstream 60107d1 <c>NavigationTiming</c>):
/// anchored once to the wall clock when the navigation starts and advanced by the monotonic
/// clock. Transport phases the host does not measure (DNS, TCP, TLS, request and response
/// start) are not synthesized.
/// </summary>
/// <remarks>
/// Deviation from the port's earlier shim, which laid those phases out proportionally inside
/// [navigationStart, now] and took the lifecycle marks from page-visible event listeners, so a
/// synthetic DOMContentLoaded dispatched by the page set them. Upstream reports absolute epoch
/// milliseconds; here the marks are relative to <see cref="TimeOrigin"/> and the shim derives
/// both the legacy and the Level 2 values from them.
/// </remarks>
public sealed class NavigationTiming
{
    /// <summary>The marks, in the order the shim reads them.</summary>
    public static readonly string[] Fields =
    [
        "fetchStart", "responseEnd", "domLoading", "domInteractive",
        "domContentLoadedEventStart", "domContentLoadedEventEnd", "domComplete",
        "loadEventStart", "loadEventEnd",
    ];

    private readonly long _origin = Stopwatch.GetTimestamp();
    private readonly double[] _values = new double[Fields.Length];
    private readonly Lock _lock = new();

    /// <summary>Navigation start, in milliseconds since the Unix epoch.</summary>
    public double TimeOrigin { get; } = (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;

    /// <summary>
    /// Record <paramref name="name"/> now, unless it already was: document.open/close and a
    /// second lifecycle pass do not replace the first navigation's completed milestones.
    /// </summary>
    public void Record(string name)
    {
        int index = Array.IndexOf(Fields, name);
        if (index < 0)
        {
            return;
        }

        lock (_lock)
        {
            if (_values[index] == 0)
            {
                // At least a microsecond, so a mark taken at the origin still reads as reached.
                _values[index] = Math.Max(0.001, Stopwatch.GetElapsedTime(_origin).TotalMilliseconds);
            }
        }
    }

    /// <summary>The value of <paramref name="name"/> in ms from <see cref="TimeOrigin"/>, 0 when not reached.</summary>
    public double Get(string name)
    {
        int index = Array.IndexOf(Fields, name);
        if (index < 0)
        {
            return 0;
        }

        lock (_lock)
        {
            return _values[index];
        }
    }

    /// <summary><c>{"timeOrigin":..., "fetchStart":..., ...}</c>, for the shim.</summary>
    public string ToJson()
    {
        var builder = new StringBuilder(256);
        builder.Append("{\"timeOrigin\":").Append(TimeOrigin.ToString("R", CultureInfo.InvariantCulture));
        lock (_lock)
        {
            for (int i = 0; i < Fields.Length; i++)
            {
                builder.Append(",\"").Append(Fields[i]).Append("\":")
                    .Append(_values[i].ToString("R", CultureInfo.InvariantCulture));
            }
        }

        return builder.Append('}').ToString();
    }
}
