using System.Net;
using DotnetFleet.Api.Client;

namespace DotnetFleet.Tests;

public sealed class FleetApiClientProjectIconTests
{
    private static readonly byte[] IconBytes = [1, 2, 3];

    [Fact]
    public async Task GetProjectIcon_ReturnsBytesWhenEndpointReturnsIcon()
    {
        var projectId = Guid.NewGuid();
        var handler = new StubHandler(request =>
            request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == $"/api/projects/{projectId}/icon"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(IconBytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient(handler);

        var icon = await client.GetProjectIcon(projectId);

        icon.Should().Equal(IconBytes);
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

    [Fact]
    public async Task SetProjectIcon_UploadsMultipartIcon()
    {
        var projectId = Guid.NewGuid();
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var client = CreateClient(handler);

        await client.SetProjectIcon(projectId, IconBytes, "icon.png");

        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Put);
        captured.RequestUri!.AbsolutePath.Should().Be($"/api/projects/{projectId}/icon");
        captured.Content.Should().BeOfType<MultipartFormDataContent>();
        capturedBody.Should().Contain("name=icon");
        capturedBody.Should().Contain("filename=icon.png");
    }

    [Fact]
    public async Task ResetProjectIcon_CallsDeleteEndpoint()
    {
        var projectId = Guid.NewGuid();
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var client = CreateClient(handler);

        await client.ResetProjectIcon(projectId);

        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Delete);
        captured.RequestUri!.AbsolutePath.Should().Be($"/api/projects/{projectId}/icon");
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
