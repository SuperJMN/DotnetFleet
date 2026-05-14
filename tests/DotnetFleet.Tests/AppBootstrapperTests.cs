using System.Reactive.Subjects;
using CSharpFunctionalExtensions;
using DotnetFleet.Api.Client;
using DotnetFleet.ViewModels;

namespace DotnetFleet.Tests;

public sealed class AppBootstrapperTests
{
    [Fact]
    public async Task Logout_ShouldClearTokenAndCredentialsBeforeRequiringClient()
    {
        var settings = new InMemorySettingsService();
        settings.SetToken("token");
        settings.SetCredentials("admin", "secret");
        var client = CreateClient();
        client.SetToken("token");
        var context = Substitute.For<IConnectedFleetClientContext>();
        context.Require().Returns(_ =>
        {
            settings.GetToken().Should().BeNull();
            settings.GetCredentials().Should().BeNull();
            client.IsAuthenticated.Should().BeFalse();
            return Task.FromResult(Maybe<Result<FleetApiClient>>.None);
        });
        var bootstrapper = new AppBootstrapper(
            client,
            settings,
            Substitute.For<IBackendHealthMonitor>(),
            context,
            Substitute.For<IFleetClientConnectionFlow>());

        await bootstrapper.Logout();

        await context.Received(1).Require();
    }

    private static FleetApiClient CreateClient()
    {
        var handler = new StubHandler();
        return new FleetApiClient(handler, handler, new Subject<System.Reactive.Unit>());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
