namespace PocketCalculator.Net.Tests;

/// <summary>Port of the tests in <c>crates/obscura-net/src/operator_hint.rs</c> (upstream 9a6dd86).</summary>
public sealed class OperatorHintTests
{
    [Theory]
    [InlineData("Access to localhost domain 'localhost' is not allowed")]
    [InlineData("Network error: Network error: Access to localhost domain 'app.localhost' is not allowed")]
    [InlineData("Access to private/internal IP address 127.0.0.1 is not allowed")]
    [InlineData("Access to private/internal IP address 192.168.1.1 is not allowed")]
    [InlineData("Access to private/internal IP address 10.0.0.1 is not allowed")]
    [InlineData("Access to private/internal IPv6 address ::1 is not allowed")]
    [InlineData("Access to private/internal IPv6 address fd12::1 is not allowed")]
    [InlineData("Access to private/internal IPv6 address ::ffff:127.0.0.1 is not allowed")]
    public void LocalDenialsOfferTheExistingOperatorOptIn(string message)
    {
        string? hint = OperatorHint.PrivateNetworkErrorHint(message);
        Assert.NotNull(hint);
        Assert.Contains("--allow-private-network", hint, StringComparison.Ordinal);
        Assert.Contains("POCKETCALCULATOR_ALLOW_PRIVATE_NETWORK=1", hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Access to private/internal IP address 169.254.169.254 is not allowed")]
    [InlineData("Access to private/internal IP address 100.100.100.200 is not allowed")]
    [InlineData("Access to private/internal IPv6 address fd00:ec2::254 is not allowed")]
    [InlineData("Access to private/internal IPv6 address fe80::1 is not allowed")]
    [InlineData("Access to private/internal IPv6 address ::ffff:169.254.169.254 is not allowed")]
    [InlineData("Access to private/internal IP address 198.18.0.1 is not allowed")]
    [InlineData("Access to private/internal IP address 0.0.0.0 is not allowed")]
    [InlineData("Access to localhost domain 'example.com' is not allowed")]
    [InlineData("Network error: certificate verify failed")]
    [InlineData("Access to private/internal IP address invalid is not allowed")]
    public void MetadataSpecialRangesAndUnrelatedErrorsDoNotGetAHint(string message) =>
        Assert.Null(OperatorHint.PrivateNetworkErrorHint(message));
}
