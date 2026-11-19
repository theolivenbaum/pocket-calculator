using System.Text;
using System.Text.Json;
using PocketCalculator.Js.Ops;
using PocketCalculator.Net;

namespace PocketCalculator.Browser;

/// <summary>
/// <c>{storage_dir}/localStorage/*.json</c>: one file per storage key (upstream f661c2e,
/// 677f6d2). Each file is <c>{"origin": KEY, "entries": [[key, value], ...]}</c>, upstream's
/// format, so a profile written by either engine loads in the other.
/// </summary>
/// <remarks>
/// <para>
/// DEVIATION from crates/obscura-browser (context.rs): a key or value holding a lone
/// surrogate, which a .NET string (like a JS string) can and a Rust string cannot, is written
/// as <c>{"utf16": base64}</c> of its UTF-16LE code units, since JSON text cannot carry one.
/// Upstream escapes such strings in the MCP layer instead.
/// </para>
/// <para>
/// The port's storage key is the origin, or <c>origin + " " + top-level site</c> for a
/// partitioned third-party area; both are accepted on load. Files are written owner-only
/// (0600) in a 0700 directory, as the cookie jar is (SECURITY.md I4); upstream writes them
/// with the process umask.
/// </para>
/// </remarks>
internal static class WebStorageProfile
{
    /// <summary>A 5 MiB store can expand about sixfold as JSON escapes.</summary>
    private const long MaxFileBytes = 32L * 1024 * 1024;

    private const string Directory = "localStorage";

    /// <summary>Restore every readable, valid area file under <paramref name="storageDir"/>.</summary>
    public static void Load(WebStorage store, string storageDir)
    {
        var root = Path.Combine(storageDir, Directory);
        if (!System.IO.Directory.Exists(root))
        {
            return;
        }
        IEnumerable<string> files;
        try
        {
            files = System.IO.Directory.EnumerateFiles(root, "*.json");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (var path in files)
        {
            try
            {
                var info = new FileInfo(path);
                // Regular files only (upstream 677f6d2): a symlink is skipped, and so is a FIFO
                // or device, whose open would block. .NET exposes no file type, but those report
                // a length of 0 and a valid area file is never empty.
                if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    || info.Length == 0 || info.Length > MaxFileBytes)
                {
                    continue;
                }
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                if (Parse(document.RootElement) is not { } parsed || !ValidKey(parsed.Key)
                    || !store.Load(parsed.Key, parsed.Items))
                {
                    Console.Error.WriteLine($"WARN Ignoring invalid or over-budget {path}");
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                or InvalidOperationException or FormatException)
            {
                Console.Error.WriteLine($"WARN Ignoring unreadable {path}: {error.Message}");
            }
        }
    }

    /// <summary>
    /// Write the areas changed since the last save. An area that lost its last item has its
    /// file removed, so a restart cannot resurrect it; an area whose write failed stays
    /// changed and is retried by the next save. Returns the failures.
    /// </summary>
    public static IReadOnlyList<string> Save(WebStorage store, string storageDir)
    {
        var failures = new List<string>();
        if (!store.IsDirty)
        {
            return failures;
        }
        var root = Path.Combine(storageDir, Directory);
        try
        {
            CookieJar.CreateOwnerOnlyDirectory(root);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            failures.Add($"Failed to create {root}: {error.Message}");
            return failures;
        }
        store.FlushChanged((key, items) =>
        {
            var path = Path.Combine(root, FileStem(key) + ".json");
            try
            {
                if (items.Count == 0)
                {
                    File.Delete(path);
                }
                else
                {
                    WriteAtomically(path, Serialize(key, items));
                }
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                failures.Add($"Failed to persist {path}: {error.Message}");
                return false;
            }
        });
        return failures;
    }

    /// <summary>
    /// Upstream <c>storage_file_stem</c>: a sanitized 48-character prefix for readability and
    /// the FNV-1a digest of the key's UTF-8, so the name is unique and stable.
    /// </summary>
    internal static string FileStem(string key)
    {
        var prefix = new StringBuilder(48);
        foreach (var rune in key.EnumerateRunes())
        {
            if (prefix.Length >= 48)
            {
                break;
            }
            prefix.Append(rune.IsAscii && (char.IsAsciiLetterOrDigit((char)rune.Value) || rune.Value == '.')
                ? (char)rune.Value
                : '_');
        }
        ulong digest = 0xcbf2_9ce4_8422_2325;
        foreach (var b in Encoding.UTF8.GetBytes(key))
        {
            digest ^= b;
            digest = unchecked(digest * 0x0000_0100_0000_01b3);
        }
        return $"{prefix}-{digest:x16}";
    }

    private static bool ValidKey(string key)
    {
        var space = key.IndexOf(' ', StringComparison.Ordinal);
        return space < 0
            ? ValidOrigin(key)
            : ValidOrigin(key[..space]) && ValidOrigin(key[(space + 1)..]);

        static bool ValidOrigin(string origin) =>
            origin == "file://"
            || (origin != "null"
                && Uri.TryCreate(origin, UriKind.Absolute, out var url)
                && url.GetLeftPart(UriPartial.Authority) == origin);
    }

    private static (string Key, List<KeyValuePair<string, string>> Items)? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("origin", out var origin) || origin.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var items = new List<KeyValuePair<string, string>>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() != 2
                || ReadString(entry[0]) is not { } key || ReadString(entry[1]) is not { } value)
            {
                return null;
            }
            items.Add(KeyValuePair.Create(key, value));
        }
        return (origin.GetString()!, items);
    }

    private static string? ReadString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Object when element.TryGetProperty("utf16", out var units)
            && units.ValueKind == JsonValueKind.String
            && Convert.FromBase64String(units.GetString()!) is { } bytes && bytes.Length % 2 == 0
            => FromUtf16(bytes),
        _ => null,
    };

    // Encoding.Unicode would replace a lone surrogate; the code units are copied as they are.
    private static string FromUtf16(byte[] bytes)
    {
        var chars = new char[bytes.Length / 2];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)(bytes[2 * i] | (bytes[(2 * i) + 1] << 8));
        }
        return new string(chars);
    }

    private static byte[] ToUtf16(string value)
    {
        var bytes = new byte[value.Length * 2];
        for (var i = 0; i < value.Length; i++)
        {
            bytes[2 * i] = (byte)value[i];
            bytes[(2 * i) + 1] = (byte)(value[i] >> 8);
        }
        return bytes;
    }

    private static bool WellFormed(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(value[i]))
            {
                return false;
            }
        }
        return true;
    }

    private static byte[] Serialize(string key, IReadOnlyList<KeyValuePair<string, string>> items)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("origin", key);
            writer.WriteStartArray("entries");
            foreach (var (name, value) in items)
            {
                writer.WriteStartArray();
                WriteString(writer, name);
                WriteString(writer, value);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static void WriteString(Utf8JsonWriter writer, string value)
    {
        if (WellFormed(value))
        {
            writer.WriteStringValue(value);
            return;
        }
        writer.WriteStartObject();
        writer.WriteString("utf16", Convert.ToBase64String(ToUtf16(value)));
        writer.WriteEndObject();
    }

    /// <summary>Through a temporary file, so an interrupted write leaves no half profile.</summary>
    private static void WriteAtomically(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        File.Delete(tmp);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using (var stream = new FileStream(tmp, options))
        {
            stream.Write(bytes);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
