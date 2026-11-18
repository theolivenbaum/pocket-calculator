using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace PocketCalculator.Net.Tests;

/// <summary>
/// Port of <c>crates/obscura-net/tests/navigation_interception.rs</c> (upstream b76d966): a
/// navigation continued with request-local headers keeps its method and body across a
/// redirect the way Fetch says, drops Authorization across origins, and drops the body's
/// headers when the redirect turns the request into a GET.
/// </summary>
public sealed class NavigationInterception
{
    private static async Task<(string Head, byte[] Body)> ReadRequestAsync(NetworkStream stream)
    {
        List<byte> data = [];
        byte[] chunk = new byte[4096];
        while (true)
        {
            int n = await stream.ReadAsync(chunk);
            Assert.True(n > 0, "the client hung up before sending a whole request");
            data.AddRange(chunk.AsSpan(0, n).ToArray());
            byte[] bytes = [.. data];
            int end = IndexOf(bytes, "\r\n\r\n"u8);
            if (end < 0)
            {
                continue;
            }

            string head = Encoding.UTF8.GetString(bytes, 0, end);
            int size = 0;
            foreach (string line in head.Split("\r\n"))
            {
                int colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0 && line[..colon].Equals("content-length", StringComparison.OrdinalIgnoreCase))
                {
                    size = int.Parse(line[(colon + 1)..].Trim(), CultureInfo.InvariantCulture);
                }
            }

            if (bytes.Length >= end + 4 + size)
            {
                return (head, bytes[(end + 4)..(end + 4 + size)]);
            }
        }
    }

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle) =>
        haystack.AsSpan().IndexOf(needle);

    private static string Origin(TcpListener listener) =>
        "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);

    public static TheoryData<bool, int, string, string> Cases()
    {
        var data = new TheoryData<bool, int, string, string>();
        foreach (bool crossOrigin in new[] { false, true })
        {
            foreach (var (status, method, expected) in new[]
            {
                (301, "PUT", "PUT"), (302, "PUT", "PUT"), (303, "HEAD", "HEAD"), (303, "PUT", "GET"),
                (302, "POST", "GET"), (307, "POST", "POST"), (308, "POST", "POST"),
            })
            {
                data.Add(crossOrigin, status, method, expected);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NavigationInterceptionRedirectsPreserveMethodAndBody(
        bool crossOrigin, int status, string method, string expected)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        TcpListener? other = crossOrigin ? new TcpListener(IPAddress.Loopback, 0) : null;
        other?.Start();
        try
        {
            string target = other is null ? "/finish" : Origin(other) + "/finish";
            Task fixture = Task.Run(async () =>
            {
                using (TcpClient first = await listener.AcceptTcpClientAsync())
                {
                    NetworkStream stream = first.GetStream();
                    var (head, _) = await ReadRequestAsync(stream);
                    Assert.StartsWith(method + " ", head, StringComparison.Ordinal);
                    Assert.Contains("x-routed: retained", head.ToLowerInvariant(), StringComparison.Ordinal);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {status} Redirect\r\nLocation: {target}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                }

                using TcpClient second = await (other ?? listener).AcceptTcpClientAsync();
                NetworkStream secondStream = second.GetStream();
                var (finalHead, bytes) = await ReadRequestAsync(secondStream);
                Assert.StartsWith(expected + " /finish ", finalHead, StringComparison.Ordinal);
                string lower = finalHead.ToLowerInvariant();
                Assert.Contains("x-routed: retained", lower, StringComparison.Ordinal);
                Assert.Equal(!crossOrigin, lower.Contains("authorization: bearer test", StringComparison.Ordinal));
                Assert.Equal(expected != "GET", lower.Contains("content-type: application/octet-stream", StringComparison.Ordinal));
                Assert.Equal(expected is "GET" or "HEAD" ? [] : "abc"u8.ToArray(), bytes);
                await secondStream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray());
            });

            var client = new PocketCalculatorHttpClient(new CookieJar(), null, true);
            byte[]? body = method == "HEAD" ? null : "abc"u8.ToArray();
            var headers = new Dictionary<string, string>
            {
                ["x-routed"] = "retained",
                ["Authorization"] = "Bearer test",
                ["content-type"] = "application/octet-stream",
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Response response = await client.FetchNavigationAsync(
                new HttpMethod(method),
                new Uri(Origin(listener) + "/start"),
                body,
                ResourceRequest.Navigation(),
                headers,
                null,
                timeout.Token);
            Assert.Equal(200, response.Status);
            await fixture.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(client.ExtraHeaders);
        }
        finally
        {
            listener.Stop();
            other?.Stop();
        }
    }
}
