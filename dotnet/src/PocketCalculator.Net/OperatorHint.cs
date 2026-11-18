using System.Net;

namespace PocketCalculator.Net;

/// <summary>
/// Port of <c>crates/obscura-net/src/operator_hint.rs</c> (upstream 9a6dd86): a hint for an
/// operator-facing error, not a policy change. The CLI and the stdio MCP server present it;
/// remote transports keep the original error. The denied host is read from the error itself,
/// since a redirect can lead somewhere other than the requested URL. Metadata and other
/// special-purpose ranges deliberately get no opt-out suggestion.
/// </summary>
public static class OperatorHint
{
    /// <summary>The hint text. Names the port's executable and environment variable.</summary>
    public const string PrivateNetworkHint =
        "Local/private network access is blocked by the SSRF guard. For your own local services, start pocket-calculator with --allow-private-network or set POCKETCALCULATOR_ALLOW_PRIVATE_NETWORK=1.";

    /// <summary>The hint for an SSRF denial of a loopback, private or localhost target, else null.</summary>
    public static string? PrivateNetworkErrorHint(string error)
    {
        ArgumentNullException.ThrowIfNull(error);
        const string NetworkPrefix = "Network error: ";
        while (error.StartsWith(NetworkPrefix, StringComparison.Ordinal))
        {
            error = error[NetworkPrefix.Length..];
        }

        const string LocalhostPrefix = "Access to localhost domain '";
        const string LocalhostSuffix = "' is not allowed";
        if (error.StartsWith(LocalhostPrefix, StringComparison.Ordinal) && error.EndsWith(LocalhostSuffix, StringComparison.Ordinal))
        {
            string domain = error[LocalhostPrefix.Length..^LocalhostSuffix.Length].ToLowerInvariant();
            return domain == "localhost" || domain.EndsWith(".localhost", StringComparison.Ordinal) ? PrivateNetworkHint : null;
        }

        string? addressText = null;
        foreach (string prefix in new[] { "Access to private/internal IP address ", "Access to private/internal IPv6 address " })
        {
            if (error.StartsWith(prefix, StringComparison.Ordinal) && error.EndsWith(" is not allowed", StringComparison.Ordinal))
            {
                addressText = error[prefix.Length..^" is not allowed".Length];
            }
        }

        if (addressText is null || !IPAddress.TryParse(addressText, out IPAddress? address))
        {
            return null;
        }

        return IsLocal(address) ? PrivateNetworkHint : null;
    }

    private static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return IsLocalV4(address.MapToIPv4());
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return IsLocalV4(address);
        }

        // AWS's IPv6 instance metadata endpoint is in the unique-local range.
        if (address.Equals(IPAddress.Parse("fd00:ec2::254")))
        {
            return false;
        }

        byte[] bytes = address.GetAddressBytes();
        bool compatible = bytes.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0 && (bytes[12] | bytes[13]) != 0;
        return IPAddress.IsLoopback(address)
            || (bytes[0] & 0xfe) == 0xfc
            || (compatible && IsLocalV4(new IPAddress(bytes.AsSpan(12, 4))));
    }

    private static bool IsLocalV4(IPAddress v4)
    {
        byte[] b = v4.GetAddressBytes();
        return b[0] == 127 || b[0] == 10 || (b[0] == 172 && (b[1] & 0xf0) == 16) || (b[0] == 192 && b[1] == 168);
    }

    /// <summary><paramref name="message"/>, with the hint on its own line when one applies.</summary>
    public static string WithHint(string message) =>
        PrivateNetworkErrorHint(message) is { } hint ? $"{message}\n{hint}" : message;
}
