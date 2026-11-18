namespace PocketCalculator.Net;

/// <summary>
/// robots.txt fetch/cache/match (port of <c>crates/obscura-net/src/robots.rs</c>, at upstream
/// 0e17221, b3475a5, 8dd9d48 and 9e0b8b6: a bounded cache, a 500 KiB body cap, RFC 9309
/// groups matched by product token, and longest-match precedence).
/// </summary>
public sealed class RobotsCache
{
    /// <summary>Origins kept per context; past this the oldest is evicted and refetched if visited again.</summary>
    internal const int MaxRobotsEntries = 4096;

    /// <summary>Bytes of a robots.txt body that are parsed (Google's documented limit).</summary>
    internal const int MaxRobotsBodyBytes = 500 * 1024;

    private readonly Dictionary<string, RobotsRules> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly System.Threading.Lock _lock = new();

    private sealed record RobotsRules(List<string> Disallowed, List<string> Allowed);

    /// <summary>Parse a robots.txt body for <paramref name="ourAgent"/> and cache it under a domain.</summary>
    public void ParseAndStore(string domain, string body, string ourAgent)
    {
        var rules = ParseRobotsTxt(TruncateBody(body), ourAgent);
        lock (_lock)
        {
            if (_cache.TryAdd(domain, rules))
            {
                _order.Enqueue(domain);
            }
            else
            {
                _cache[domain] = rules;
            }

            while (_cache.Count > MaxRobotsEntries && _order.TryDequeue(out var oldest))
            {
                _cache.Remove(oldest);
            }
        }
    }

    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _cache.Count;
            }
        }
    }

    /// <summary>True when a robots.txt has already been parsed for this origin.</summary>
    public bool Contains(string origin)
    {
        lock (_lock)
        {
            return _cache.ContainsKey(origin);
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> is allowed for <paramref name="domain"/>.
    /// A domain with no cached rules is allowed.
    /// </summary>
    public bool IsAllowed(string domain, string path)
    {
        RobotsRules? rules;
        lock (_lock)
        {
            if (!_cache.TryGetValue(domain, out rules))
            {
                return true;
            }
        }

        // RFC 9309 2.2.2: the most specific (longest) match wins; Allow only wins when both
        // rules have the same length.
        int bestLength = -1;
        bool bestAllow = true;
        foreach (var pattern in rules.Allowed)
        {
            if (PathMatches(path, pattern) && pattern.Length >= bestLength)
            {
                bestLength = pattern.Length;
                bestAllow = true;
            }
        }

        foreach (var pattern in rules.Disallowed)
        {
            if (PathMatches(path, pattern)
                && (pattern.Length > bestLength || (pattern.Length == bestLength && !bestAllow)))
            {
                bestLength = pattern.Length;
                bestAllow = false;
            }
        }

        return bestLength < 0 || bestAllow;
    }

    /// <summary>
    /// The first <see cref="MaxRobotsBodyBytes"/> UTF-8 bytes of <paramref name="body"/>, cut
    /// back to the last complete line so no rule is parsed from a truncated line.
    /// </summary>
    private static string TruncateBody(string body)
    {
        if (body.Length <= MaxRobotsBodyBytes / 3 || System.Text.Encoding.UTF8.GetByteCount(body) <= MaxRobotsBodyBytes)
        {
            return body;
        }

        int bytes = 0;
        int end = 0;
        foreach (var rune in body.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > MaxRobotsBodyBytes)
            {
                break;
            }

            bytes += rune.Utf8SequenceLength;
            end += rune.Utf16SequenceLength;
        }

        var newline = body.LastIndexOf('\n', end - 1, end);
        return newline < 0 ? body[..end] : body[..newline];
    }

    private sealed class Group
    {
        public List<string> Agents { get; } = [];
        public List<string> Disallowed { get; } = [];
        public List<string> Allowed { get; } = [];
        public bool RulesStarted { get; set; }
    }

    private static RobotsRules ParseRobotsTxt(string body, string ourAgent)
    {
        // RFC 9309 2.2.1: a group applies when any of its user-agent lines names one of our
        // product tokens (not a substring of the UA), or when it names "*". Consecutive
        // user-agent lines belong to the same group; the first rule ends the agent list.
        var ourTokens = new List<string>();
        foreach (var token in ourAgent.Split([' ', '\t', '\n', '\r', '/', '(', ')', ';', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            ourTokens.Add(token.ToLowerInvariant());
        }

        var groups = new List<Group>();
        foreach (var raw in Lines(body))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (!TrySplitOnce(line, ':', out var rawKey, out var rawValue))
            {
                continue;
            }

            var key = rawKey.Trim().ToLowerInvariant();
            var value = rawValue.Trim();
            switch (key)
            {
                case "user-agent":
                    if (groups.Count == 0 || groups[^1].RulesStarted)
                    {
                        groups.Add(new Group());
                    }

                    groups[^1].Agents.Add(value.ToLowerInvariant());
                    break;
                case "allow" or "disallow" when groups.Count != 0:
                    var group = groups[^1];
                    group.RulesStarted = true;
                    if (value.Length != 0)
                    {
                        (key == "allow" ? group.Allowed : group.Disallowed).Add(value);
                    }

                    break;
                default:
                    break;
            }
        }

        bool Matches(Group group)
        {
            foreach (var agent in group.Agents)
            {
                if (agent != "*" && ourTokens.Contains(agent))
                {
                    return true;
                }
            }

            return false;
        }

        var specific = groups.Exists(Matches);
        var disallowed = new List<string>();
        var allowed = new List<string>();
        foreach (var group in groups)
        {
            if (specific ? Matches(group) : group.Agents.Contains("*"))
            {
                disallowed.AddRange(group.Disallowed);
                allowed.AddRange(group.Allowed);
            }
        }

        return new RobotsRules(disallowed, allowed);
    }

    private static bool PathMatches(string path, string pattern)
    {
        if (pattern.EndsWith('*'))
        {
            return path.StartsWith(pattern[..^1], StringComparison.Ordinal);
        }

        if (pattern.EndsWith('$'))
        {
            return string.Equals(path, pattern[..^1], StringComparison.Ordinal);
        }

        return path.StartsWith(pattern, StringComparison.Ordinal);
    }

    /// <summary>Rust's <c>str::lines</c>: split on '\n' and drop a trailing '\r'.</summary>
    internal static IEnumerable<string> Lines(string body)
    {
        var start = 0;
        while (start <= body.Length)
        {
            var end = body.IndexOf('\n', start);
            if (end < 0)
            {
                if (start < body.Length)
                {
                    yield return body[start..].TrimEnd('\r');
                }

                yield break;
            }

            var line = body[start..end];
            yield return line.EndsWith('\r') ? line[..^1] : line;
            start = end + 1;
        }
    }

    private static bool TrySplitOnce(string value, char separator, out string left, out string right)
    {
        var index = value.IndexOf(separator);
        if (index < 0)
        {
            left = value;
            right = string.Empty;
            return false;
        }

        left = value[..index];
        right = value[(index + 1)..];
        return true;
    }
}
