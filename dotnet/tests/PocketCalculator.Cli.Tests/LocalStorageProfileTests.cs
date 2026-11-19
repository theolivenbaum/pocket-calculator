using Xunit;

namespace PocketCalculator.Cli.Tests;

// Upstream f661c2e: `fetch --storage-dir` keeps localStorage across runs, as it keeps cookies.
public sealed class LocalStorageProfileTests
{
    public LocalStorageProfileTests() =>
        Assert.True(CliProcess.SkipReason is null, CliProcess.SkipReason ?? string.Empty);

    [Fact]
    public void Fetch_persists_local_storage_in_the_storage_dir()
    {
        using var server = LocalHttpServer.Html("<html><body><p>x</p></body></html>");
        var profile = Directory.CreateTempSubdirectory("obscura-ls-profile");
        try
        {
            var env = new Dictionary<string, string> { ["POCKETCALCULATOR_ALLOW_PRIVATE_NETWORK"] = "1" };
            var first = CliProcess.Run(env,
                "fetch", server.Base + "/", "--storage-dir", profile.FullName,
                "--eval", "(localStorage.setItem('kept', 'across runs'), 'set')", "--quiet");
            Assert.True(first.Success, first.StdErr);
            var second = CliProcess.Run(env,
                "fetch", server.Base + "/", "--storage-dir", profile.FullName,
                "--eval", "localStorage.getItem('kept')", "--quiet");
            Assert.True(second.Success, second.StdErr);
            Assert.Equal("across runs", second.StdOut.Trim());
        }
        finally
        {
            profile.Delete(recursive: true);
        }
    }
}
