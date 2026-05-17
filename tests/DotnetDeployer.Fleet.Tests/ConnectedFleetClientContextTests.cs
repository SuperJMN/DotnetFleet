using System.Net;
using System.Net.Http.Json;
using System.Reactive.Subjects;
using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.Api.Client;
using DotnetDeployer.Fleet.App.ViewModels;

namespace DotnetDeployer.Fleet.Tests;

public sealed class ConnectedFleetClientContextTests
{
    [Fact]
    public async Task Require_WhenEndpointAndSessionAreValid_ShouldReturnClientWithoutOpeningFlow()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new FleetApiClient.SessionResponse("admin", "Admin"))
        });
        client.SetBaseAddress("http://localhost:5000");
        client.SetToken("valid");
        var flow = new TestConnectionFlow();
        var context = CreateContext(client, flow);

        var result = await context.Require();

        result.Value.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().BeSameAs(client);
        flow.ConnectCalls.Should().Be(0);
        flow.LoginCalls.Should().Be(0);
    }

    [Fact]
    public async Task Require_WhenEndpointFlowIsCancelled_ShouldReturnNone()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var flow = new TestConnectionFlow
        {
            ConnectResult = Maybe<Result>.None
        };
        var context = CreateContext(client, flow);

        var result = await context.Require();

        result.HasNoValue.Should().BeTrue();
        flow.ConnectCalls.Should().Be(1);
    }

    [Fact]
    public async Task Require_WhenStoredTokenIsRejected_ShouldClearItAndRunLoginFlow()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        client.SetBaseAddress("http://localhost:5000");
        client.SetToken("stale");
        var settings = new InMemorySettingsService();
        settings.SetEndpoint("http://localhost:5000");
        settings.SetToken("stale");
        var flow = new TestConnectionFlow
        {
            LoginAction = () => client.SetToken("fresh"),
            LoginResult = Maybe.From(Result.Success())
        };
        var context = CreateContext(client, flow, settings);

        var result = await context.Require();

        result.Value.IsSuccess.Should().BeTrue();
        client.IsAuthenticated.Should().BeTrue();
        flow.LoginCalls.Should().Be(1);
        settings.GetToken().Should().BeNull();
    }

    [Fact]
    public async Task Require_WhenStoredCredentialsAreValid_ShouldLoginWithoutOpeningFlow()
    {
        var client = CreateClient(request =>
            request.RequestUri?.AbsolutePath == "/api/auth/login"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new FleetApiClient.LoginResponse("fresh", "admin", "Admin"))
                }
                : new HttpResponseMessage(HttpStatusCode.OK));
        client.SetBaseAddress("http://localhost:5000");
        var settings = new InMemorySettingsService();
        settings.SetEndpoint("http://localhost:5000");
        settings.SetCredentials("admin", "admin");
        var flow = new TestConnectionFlow();
        var context = CreateContext(client, flow, settings);

        var result = await context.Require();

        result.Value.IsSuccess.Should().BeTrue();
        client.IsAuthenticated.Should().BeTrue();
        settings.GetToken().Should().Be("fresh");
        flow.LoginCalls.Should().Be(0);
    }

    [Fact]
    public async Task Require_WhenCachedLoginFails_ShouldClearCredentialsAndRunLoginFlow()
    {
        var client = CreateClient(request =>
            request.RequestUri?.AbsolutePath == "/api/auth/login"
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.OK));
        client.SetBaseAddress("http://localhost:5000");
        var settings = new InMemorySettingsService();
        settings.SetEndpoint("http://localhost:5000");
        settings.SetCredentials("admin", "wrong");
        var flow = new TestConnectionFlow
        {
            LoginAction = () =>
            {
                client.SetToken("manual");
                settings.SetToken("manual");
            },
            LoginResult = Maybe.From(Result.Success())
        };
        var context = CreateContext(client, flow, settings);

        var result = await context.Require();

        result.Value.IsSuccess.Should().BeTrue();
        flow.LoginCalls.Should().Be(1);
        settings.GetCredentials().Should().BeNull();
    }

    private static ConnectedFleetClientContext CreateContext(
        FleetApiClient client,
        TestConnectionFlow flow,
        ISettingsService? settings = null) =>
        new(client, settings ?? new InMemorySettingsService(), new TestHealthMonitor(client), flow);

    private static FleetApiClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> response)
    {
        var handler = new StubHandler(response);
        return new FleetApiClient(handler, handler);
    }

    private sealed class TestConnectionFlow : IFleetClientConnectionFlow
    {
        public int ConnectCalls { get; private set; }
        public int LoginCalls { get; private set; }
        public Maybe<Result> ConnectResult { get; init; } = Maybe.From(Result.Success());
        public Maybe<Result> LoginResult { get; init; } = Maybe.From(Result.Success());
        public Action? ConnectAction { get; init; }
        public Action? LoginAction { get; init; }

        public Task<Maybe<Result>> Connect()
        {
            ConnectCalls++;
            ConnectAction?.Invoke();
            return Task.FromResult(ConnectResult);
        }

        public Task<Maybe<Result>> Login()
        {
            LoginCalls++;
            LoginAction?.Invoke();
            return Task.FromResult(LoginResult);
        }
    }

    private sealed class TestHealthMonitor(FleetApiClient client) : IBackendHealthMonitor
    {
        public IObservable<BackendHealthSnapshot> Snapshots => new Subject<BackendHealthSnapshot>();
        public BackendHealthSnapshot Current { get; private set; } =
            new(BackendHealth.Unknown, null, DateTimeOffset.UtcNow, null, null, client.BaseAddress);

        public void Start()
        {
        }

        public Task<BackendHealthSnapshot> CheckNowAsync(CancellationToken ct = default)
        {
            Current = client.BaseAddress is null
                ? new BackendHealthSnapshot(BackendHealth.Unknown, "No endpoint configured", DateTimeOffset.UtcNow, null, null, null)
                : new BackendHealthSnapshot(BackendHealth.Healthy, null, DateTimeOffset.UtcNow, null, TimeSpan.FromMilliseconds(1), client.BaseAddress);
            return Task.FromResult(Current);
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
