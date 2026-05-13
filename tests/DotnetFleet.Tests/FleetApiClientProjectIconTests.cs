using System.Net;
using DotnetFleet.Api.Client;

namespace DotnetFleet.Tests;

public sealed class FleetApiClientProjectIconTests
{
    [Fact]
    public async Task GetProjectIcon_ReturnsBytesWhenEndpointReturnsIcon()
    {
        var projectId = Guid.NewGuid();
        var bytes = new byte[] { 1, 2, 3 };
        var handler = new StubHandler(request =>
            request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == $"/api/projects/{projectId}/icon"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient(handler);

        var icon = await client.GetProjectIcon(projectId);

        icon.Should().Equal(bytes);
    }

    [Fact]
    public async Task GetProjectIcon_ReturnsNullWhenEndpointHasNoIcon()
    {
        var projectId = Guid.NewGuid();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient(handler);

        var icon = await client.GetProjectIcon(projectId);

        icon.Should().BeNull();
    }

    private static FleetApiClient CreateClient(HttpMessageHandler handler)
    {
        var client = new FleetApiClient(handler, handler);
        client.SetBaseAddress("http://localhost:5000");
        return client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handle(request));
    }
}
