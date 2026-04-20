using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DotnetFleet.WorkerService;
using DotnetFleet.WorkerService.Coordinator;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DotnetFleet.Tests;

/// <summary>
/// Token caching + refresh-on-401 are critical: without them, every Worker call
/// would re-login (load on the coordinator) and a rotated secret would silently
/// fail. <see cref="WorkerAuthHandler"/> is the single place this is enforced.
/// </summary>
public class WorkerTokenManagerTests
{
    private static string MakeJwt(DateTimeOffset expires)
    {
        var header = Base64Url("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        var payload = Base64Url($"{{\"exp\":{expires.ToUnixTimeSeconds()}}}");
        return $"{header}.{payload}.";
    }

    private static string Base64Url(string s) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(s))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class StubHandler : HttpMessageHandler
    {
        public int LoginCalls;
        public int ProtectedCalls;
        public Func<int, HttpStatusCode> ProtectedStatus = _ => HttpStatusCode.OK;
        public string Token = "tok-1";
        public DateTimeOffset Expiry = DateTimeOffset.UtcNow.AddHours(1);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/login"))
            {
                LoginCalls++;
                var body = JsonSerializer.Serialize(new
                {
                    token = MakeJwt(Expiry),
                    workerId = Guid.NewGuid(),
                    name = "w"
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                });
            }

            ProtectedCalls++;
            return Task.FromResult(new HttpResponseMessage(ProtectedStatus(ProtectedCalls)));
        }
    }

    private static (WorkerTokenManager mgr, WorkerAuthHandler auth, StubHandler stub) Build()
    {
        var stub = new StubHandler();
        var loginClient = new HttpClient(stub) { BaseAddress = new Uri("http://test/") };
        var opts = Options.Create(new WorkerOptions
        {
            CoordinatorBaseUrl = "http://test/",
            Id = Guid.NewGuid(),
            Secret = "s",
            TokenRefreshSkewSeconds = 30
        });
        var mgr = new WorkerTokenManager(loginClient, opts, NullLogger<WorkerTokenManager>.Instance);
        var auth = new WorkerAuthHandler(mgr) { InnerHandler = stub };
        return (mgr, auth, stub);
    }

    [Fact]
    public async Task GetTokenAsync_caches_token_across_calls()
    {
        var (mgr, _, stub) = Build();
        var t1 = await mgr.GetTokenAsync();
        var t2 = await mgr.GetTokenAsync();

        t1.Should().Be(t2);
        stub.LoginCalls.Should().Be(1, "the second call must hit the cache");
    }

    [Fact]
    public async Task WorkerAuthHandler_refreshes_token_on_401_and_retries()
    {
        var (_, auth, stub) = Build();
        // First protected response is 401, subsequent ones succeed.
        stub.ProtectedStatus = call => call == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK;

        var client = new HttpClient(auth) { BaseAddress = new Uri("http://test/") };
        var resp = await client.GetAsync("/api/jobs/next");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.LoginCalls.Should().Be(2, "401 must trigger one re-login and one retry");
        stub.ProtectedCalls.Should().Be(2);
    }

    [Fact]
    public async Task GetTokenAsync_throws_when_credentials_missing()
    {
        var stub = new StubHandler();
        var loginClient = new HttpClient(stub) { BaseAddress = new Uri("http://test/") };
        var opts = Options.Create(new WorkerOptions
        {
            CoordinatorBaseUrl = "http://test/",
            Id = null,
            Secret = null
        });
        var mgr = new WorkerTokenManager(loginClient, opts, NullLogger<WorkerTokenManager>.Instance);

        await mgr.Invoking(m => m.GetTokenAsync())
            .Should().ThrowAsync<InvalidOperationException>();
    }
}
