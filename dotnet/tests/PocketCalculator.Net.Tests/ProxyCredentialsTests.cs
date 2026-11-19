using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PocketCalculator.Net.Tests;

/// <summary>
/// Credentials in the proxy URL (<c>http://user:password@host:port</c>) authenticate to the
/// proxy, as reqwest's <c>Proxy::all</c> does in the reference.
/// </summary>
public sealed class ProxyCredentialsTests
{
    /// <summary>
    /// A loopback HTTP proxy that requires Basic authentication: 407 with a challenge until a
    /// request carries the expected <c>Proxy-Authorization</c>, then it answers on the target's behalf.
    /// </summary>
    private sealed class AuthenticatingProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly string _expected;
        private readonly List<string?> _authorizations = [];

        public AuthenticatingProxy(string user, string password)
        {
            _expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptAsync);
        }

        public int Port { get; }

        /// <summary>The <c>Proxy-Authorization</c> of every request, null where there was none.</summary>
        public IReadOnlyList<string?> Authorizations
        {
            get
            {
                lock (_authorizations)
                {
                    return [.. _authorizations];
                }
            }
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception)
                {
                    return;
                }

                using (client)
                {
                    var stream = client.GetStream();
                    var authorization = await ReadProxyAuthorizationAsync(stream, _stop.Token);
                    lock (_authorizations)
                    {
                        _authorizations.Add(authorization);
                    }

                    var response = authorization == _expected
                        ? "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 7\r\nConnection: close\r\n\r\nproxied"
                        : "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"test\"\r\n"
                          + "Content-Length: 0\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
                    await stream.FlushAsync(_stop.Token);
                }
            }
        }

        private static async Task<string?> ReadProxyAuthorizationAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var head = new StringBuilder();
            var buffer = new byte[4096];
            while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                head.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            foreach (var line in head.ToString().Split("\r\n"))
            {
                if (line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
                {
                    return line["Proxy-Authorization:".Length..].Trim();
                }
            }

            return null;
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }

    // The proxy answers for the target, so the vetting lookup must not depend on DNS being available.
    private static Task<IPAddress[]> PublicAddress(string host, CancellationToken cancellationToken) =>
        Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });

    [Fact]
    public async Task CredentialsInTheProxyUrlAuthenticate()
    {
        using var proxy = new AuthenticatingProxy("user", "p@ss:word");
        var client = new PocketCalculatorHttpClient(
            new CookieJar(), $"http://user:p%40ss%3Aword@127.0.0.1:{proxy.Port}", allowPrivateNetwork: false)
        {
            ProxyTargetResolver = PublicAddress,
        };

        Response response = await client.FetchAsync(new Uri("http://example.com/"));

        Assert.Equal(200, response.Status);
        Assert.Equal("proxied", Encoding.UTF8.GetString(response.Body));
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:p@ss:word")), proxy.Authorizations[^1]);
    }

    [Fact]
    public async Task WithoutCredentialsTheProxyRefuses()
    {
        using var proxy = new AuthenticatingProxy("user", "password");
        var client = new PocketCalculatorHttpClient(new CookieJar(), $"http://127.0.0.1:{proxy.Port}", allowPrivateNetwork: false)
        {
            ProxyTargetResolver = PublicAddress,
        };

        Response response = await client.FetchAsync(new Uri("http://example.com/"));

        Assert.Equal(407, response.Status);
        Assert.All(proxy.Authorizations, Assert.Null);
    }

    [Fact]
    public void CreateProxyMovesTheUserInfoIntoCredentials()
    {
        var proxy = PocketCalculatorHttpClient.CreateProxy(new Uri("http://us%3Aer:p%40ss:word@proxy.example:3128"));

        Assert.Equal(new Uri("http://proxy.example:3128/"), proxy.Address);
        var credential = Assert.IsType<NetworkCredential>(proxy.Credentials);
        Assert.Equal("us:er", credential.UserName);
        Assert.Equal("p@ss:word", credential.Password);
    }

    [Fact]
    public void CreateProxyWithAUserAloneHasAnEmptyPassword()
    {
        var proxy = PocketCalculatorHttpClient.CreateProxy(new Uri("http://user@proxy.example:3128"));

        var credential = Assert.IsType<NetworkCredential>(proxy.Credentials);
        Assert.Equal("user", credential.UserName);
        Assert.Equal(string.Empty, credential.Password);
    }

    [Fact]
    public void CreateProxyWithoutUserInfoHasNoCredentials()
    {
        var proxy = PocketCalculatorHttpClient.CreateProxy(new Uri("http://proxy.example:3128"));

        Assert.Equal(new Uri("http://proxy.example:3128/"), proxy.Address);
        Assert.Null(proxy.Credentials);
    }
}
