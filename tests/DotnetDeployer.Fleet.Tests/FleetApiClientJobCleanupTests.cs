using System.Net;
using System.Net.Http.Json;
using DotnetDeployer.Fleet.Api.Client;

namespace DotnetDeployer.Fleet.Tests;

public sealed class FleetApiClientJobCleanupTests
{
    [Fact]
    public async Task DeleteFinishedJobsAsync_ShouldCallGlobalFinishedJobsEndpoint()
    {
        HttpRequestMessage? captured = null;
        var client = CreateClient(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { deleted = 3 })
            };
        });

        var deleted = await client.DeleteFinishedJobsAsync();

        deleted.Should().Be(3);
        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Delete);
        captured.RequestUri!.AbsolutePath.Should().Be("/api/jobs/finished");
    }

    private static FleetApiClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> response)
    {
        var handler = new StubHandler(response);
        var client = new FleetApiClient(handler, handler);
        client.SetBaseAddress("http://localhost:5000");
        return client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
