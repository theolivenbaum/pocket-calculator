using System.Diagnostics;
using PocketCalculator.Js.Ops;
using Xunit;

namespace PocketCalculator.Browser.Tests;

// Ports of upstream f661c2e / 677f6d2 (crates/obscura-browser/src/context.rs) and 9a5d9c6
// (crates/obscura-cdp/src/server.rs): localStorage persisted under --storage-dir.
public sealed class LocalStoragePersistence : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("obscura-localstorage");

    public void Dispose()
    {
        try
        {
            _dir.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Profile => Path.Combine(_dir.FullName, "profile");

    private string AreaPath(string key) =>
        Path.Combine(Profile, "localStorage", WebStorageProfile.FileStem(key) + ".json");

    [Fact]
    public void A_second_context_reads_back_what_the_first_wrote_and_files_are_private()
    {
        var context = BrowserContext.WithStorage("writer", Profile);
        Assert.True(context.LocalStorage.Set("https://example.com", "token", "private"));
        Assert.True(context.LocalStorage.Set("https://example.com", "lone", "a\uD800b"));
        Assert.True(context.LocalStorage.Set("https://frame.test https://top.test", "k", "partitioned"));
        context.SaveStorage();
        Assert.False(context.LocalStorage.IsDirty);

        if (!OperatingSystem.IsWindows())
        {
            var owner0700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Assert.Equal(owner0700, File.GetUnixFileMode(Profile));
            Assert.Equal(owner0700, File.GetUnixFileMode(Path.Combine(Profile, "localStorage")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(AreaPath("https://example.com")));
        }

        var restored = BrowserContext.WithStorage("restored", Profile);
        Assert.Equal("private", restored.LocalStorage.Get("https://example.com", "token"));
        Assert.Equal("a\uD800b", restored.LocalStorage.Get("https://example.com", "lone"));
        Assert.Equal("partitioned", restored.LocalStorage.Get("https://frame.test https://top.test", "k"));
        Assert.False(restored.LocalStorage.IsDirty);
    }

    [Fact]
    public void Local_storage_retries_failed_writes_without_rewriting_successes()
    {
        var context = BrowserContext.WithStorage("write-retry", Profile);
        Directory.CreateDirectory(Path.Combine(Profile, "localStorage"));
        var blocked = AreaPath("https://failed.test") + ".tmp";
        Directory.CreateDirectory(blocked);
        Assert.True(context.LocalStorage.Set("https://failed.test", "token", "kept"));
        Assert.True(context.LocalStorage.Set("https://saved.test", "token", "saved"));
        context.SaveLocalStorage();
        Assert.True(context.LocalStorage.IsDirty, "failed writes must remain retryable");
        Assert.Equal(["https://failed.test"], context.LocalStorage.ChangedKeys());
        Directory.Delete(blocked);
        context.SaveLocalStorage();
        Assert.False(context.LocalStorage.IsDirty);
        var reloaded = BrowserContext.WithStorage("reloaded", Profile);
        Assert.Equal("kept", reloaded.LocalStorage.Get("https://failed.test", "token"));
        Assert.Equal("saved", reloaded.LocalStorage.Get("https://saved.test", "token"));
    }

    [Fact]
    public void Local_storage_retries_failed_deletions()
    {
        var context = BrowserContext.WithStorage("delete-retry", Profile);
        Assert.True(context.LocalStorage.Set("https://app.test", "token", "old"));
        context.SaveLocalStorage();
        var path = AreaPath("https://app.test");
        File.Delete(path);
        Directory.CreateDirectory(path);
        context.LocalStorage.Clear("https://app.test");
        context.SaveLocalStorage();
        Assert.True(context.LocalStorage.IsDirty, "a failed deletion must not be acknowledged");
        Directory.Delete(path);
        context.SaveLocalStorage();
        Assert.False(context.LocalStorage.IsDirty);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Local_storage_ignores_non_regular_profile_files()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var root = Path.Combine(Profile, "localStorage");
        Directory.CreateDirectory(root);
        var pipe = Path.Combine(root, "blocked.json");
        using (var mkfifo = Process.Start("mkfifo", [pipe]))
        {
            mkfifo.WaitForExit();
            Assert.Equal(0, mkfifo.ExitCode);
        }
        var load = Task.Run(() => BrowserContext.WithStorage("reader", Profile).LocalStorage.IsDirty);
        var finished = await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(2)));
        if (finished != load)
        {
            // Release the blocked reader before reporting the failure.
            await File.WriteAllTextAsync(pipe, "{}");
        }
        Assert.True(finished == load, "profile loading must not block on a FIFO");
        Assert.False(await load);
    }

    [Fact]
    public void Invalid_or_over_budget_area_files_are_ignored()
    {
        var root = Path.Combine(Profile, "localStorage");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.json"), """{"origin":"https://ok.test","entries":[["k","v"]]}""");
        File.WriteAllText(Path.Combine(root, "b.json"), """{"origin":"https://ok.test/path","entries":[["k","v"]]}""");
        File.WriteAllText(Path.Combine(root, "c.json"), """{"origin":"null","entries":[["k","v"]]}""");
        File.WriteAllText(Path.Combine(root, "d.json"), """{"origin":"https://dup.test","entries":[["k","v"],["k","w"]]}""");
        File.WriteAllText(Path.Combine(root, "e.json"), "not json");
        var context = BrowserContext.WithStorage("reader", Profile);
        Assert.Equal("v", context.LocalStorage.Get("https://ok.test", "k"));
        Assert.Single(context.LocalStorage.Entries());
    }

    // Upstream 9a5d9c6 (server.rs): merging one connection's changes keeps values other
    // connections wrote and the profile's key order, and a merge over budget changes nothing.
    [Fact]
    public void Local_storage_delta_preserves_other_clients_values_and_key_order()
    {
        const string origin = "https://app.test";
        var initial = new WebStorage();
        Assert.True(initial.Load(origin, [KeyValuePair.Create("sid", "old"), KeyValuePair.Create("gone", "x")]));
        var current = initial.IsolatedSnapshot();
        current.Remove(origin, "gone");
        Assert.True(current.Set(origin, "first", "1"));
        Assert.True(current.Set(origin, "second", "2"));
        var destination = new WebStorage();
        Assert.True(destination.Load(origin,
            [KeyValuePair.Create("a", "A"), KeyValuePair.Create("sid", "newer"), KeyValuePair.Create("gone", "x")]));
        Assert.True(destination.MergeChanges(initial, current));
        Assert.Equal(
            [KeyValuePair.Create("a", "A"), KeyValuePair.Create("sid", "newer"), KeyValuePair.Create("first", "1"), KeyValuePair.Create("second", "2")],
            destination.Snapshot(origin));
        Assert.True(destination.MergeChanges(initial, initial));
        Assert.Equal("newer", destination.Get(origin, "sid"));
    }

    [Fact]
    public void Local_storage_delta_does_not_discard_existing_values_when_profile_is_full()
    {
        const string origin = "https://app.test";
        var initial = new WebStorage();
        var current = initial.IsolatedSnapshot();
        Assert.True(current.Set(origin, "new", "value"));
        var destination = new WebStorage();
        Assert.True(destination.Load(origin, [KeyValuePair.Create("k", new string('x', (5 * 1024 * 1024) - 2))]));
        Assert.False(destination.MergeChanges(initial, current));
        Assert.Equal(1, destination.Count(origin));
        Assert.Equal("k", destination.KeyAt(origin, 0));
        Assert.False(destination.IsDirty);
    }

    [Fact]
    public void A_connection_copy_does_not_share_its_template_store()
    {
        var template = BrowserContext.WithStorage("template", Profile);
        Assert.True(template.LocalStorage.Set("https://app.test", "k", "template"));
        var connection = template.ConnectionCopy("default");
        Assert.Null(connection.StorageDir);
        Assert.True(connection.LocalStorage.Set("https://app.test", "k", "connection"));
        Assert.Equal("template", template.LocalStorage.Get("https://app.test", "k"));
        var persistent = template.IsolatedCopy("persistence", true);
        Assert.Same(template.LocalStorage, persistent.LocalStorage);
    }
}
