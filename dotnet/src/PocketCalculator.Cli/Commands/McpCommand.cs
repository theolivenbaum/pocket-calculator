using PocketCalculator.Cli.CommandLine;
using PocketCalculator.Mcp;

namespace PocketCalculator.Cli.Commands;

/// <summary>The <c>mcp</c> subcommand: the stdio server, or the HTTP/SSE transport.</summary>
public static class McpCommand
{
    /// <summary>Dispatch for the <c>mcp</c> subcommand.</summary>
    public static async Task RunAsync(CliArgs args, CliCommand.Mcp mcp)
    {
        var proxy = CliOptions.MergeProxy(args.Proxy, mcp.Proxy);
        var userAgent = CliOptions.MergeUserAgent(args.UserAgent, mcp.UserAgent);
        CliOptions.ConfigureFontDirectories(mcp.FontDirs);
        HardDeadline.ArmHangExit();
        // `pocket-calculator mcp` builds its BrowserContext from the environment, so the
        // root profile flag is handed over explicitly (upstream 7500e6d).
        if (args.StorageDir is { } storageDir)
        {
            Environment.SetEnvironmentVariable("POCKETCALCULATOR_STORAGE_DIR", storageDir);
        }
        if (mcp.Http)
        {
            // The option is a u16 in Rust and its parser enforces that here too,
            // reporting clap's message, so an out-of-range port never reaches
            // this cast. Rust has no second runtime check and neither should
            // this: a duplicate guard would only ever print a message the
            // reference does not.
            // SECURITY.md I1: optional TLS, from --tls-cert/--tls-key or the
            // POCKETCALCULATOR_TLS_CERT/_KEY fallbacks.
            System.Security.Cryptography.X509Certificates.X509Certificate2? certificate;
            try
            {
                certificate = PocketCalculator.Net.ServerTls.Resolve(mcp.TlsCert, mcp.TlsKey);
            }
            catch (InvalidOperationException error)
            {
                throw new CliException(error.Message);
            }

            await Http.RunAsync(mcp.Host, checked((ushort)mcp.Port), proxy, userAgent, args.Stealth, certificate)
                .ConfigureAwait(false);
            return;
        }
        await McpServer.RunAsync(proxy, userAgent, args.Stealth).ConfigureAwait(false);
    }
}
