using System.Security.Authentication;

namespace PocketCalculator.Net.Tests;

// Port of upstream 52d8ddd's error_chain_appends_each_source_once (client.rs).
public class ErrorChainTests
{
    [Fact]
    public void Error_chain_appends_each_source_once()
    {
        var leaf = new AuthenticationException("certificate expired");
        // The middle layer already quotes the leaf, so the leaf is not repeated.
        var middle = new IOException("tls: certificate expired", leaf);
        var top = new HttpRequestException("error sending request", middle);
        Assert.Equal("error sending request: tls: certificate expired", PocketCalculatorNetException.ErrorChain(top));
        Assert.Equal("alone", PocketCalculatorNetException.ErrorChain(new Exception("alone")));
    }

    [Fact]
    public void Error_chain_names_the_tls_cause()
    {
        var top = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate is invalid because of errors in the certificate chain: NotTimeValid"));
        Assert.Equal(
            "The SSL connection could not be established, see inner exception.: The remote certificate is invalid because of errors in the certificate chain: NotTimeValid",
            PocketCalculatorNetException.ErrorChain(top));
    }
}
