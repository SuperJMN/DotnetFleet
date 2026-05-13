using System.Net;
using System.Text;
using DotnetFleet.Tool;

namespace DotnetFleet.Tests;

public class FleetAdminClientTests
{
    [Fact]
    public async Task UnregisterWorkerByName_WhenWorkerExists_ShouldDeleteMatchedWorker()
    {
        var workerId = Guid.NewGuid();
        var handler = new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/auth/login")
                return Json(HttpStatusCode.OK, """{"token":"jwt","username":"admin","role":"Admin"}""");

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/api/workers")
                return Json(HttpStatusCode.OK, $$"""[{"id":"{{workerId}}","name":"DESKTOP-NMC4AGI","status":"Offline","isEmbedded":false,"lastSeenAt":null,"maxDiskUsageGb":10,"repoStoragePath":null}]""");

            if (request.Method == HttpMethod.Delete && request.RequestUri?.AbsolutePath == $"/api/workers/{workerId}")
                return Json(HttpStatusCode.OK, $$"""{"id":"{{workerId}}","name":"DESKTOP-NMC4AGI","failedJobs":2}""");

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = CreateClient(handler);

        var result = await client.UnregisterWorkerByName("desktop-nmc4agi", "admin", "admin");

        result.Status.Should().Be(WorkerUnregisterStatus.Deleted);
        result.WorkerId.Should().Be(workerId);
        result.WorkerName.Should().Be("DESKTOP-NMC4AGI");
        result.FailedJobs.Should().Be(2);
        handler.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Delete);
        handler.Requests.Last().Headers.Authorization?.Parameter.Should().Be("jwt");
    }

    [Fact]
    public async Task UnregisterWorkerByName_WhenWorkerIsMissing_ShouldNotDeleteAnything()
    {
        var handler = new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/auth/login")
                return Json(HttpStatusCode.OK, """{"token":"jwt","username":"admin","role":"Admin"}""");

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/api/workers")
                return Json(HttpStatusCode.OK, "[]");

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = CreateClient(handler);

        var result = await client.UnregisterWorkerByName("DESKTOP-NMC4AGI", "admin", "admin");

        result.Status.Should().Be(WorkerUnregisterStatus.NotFound);
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task UnregisterWorkerByName_WhenDeleteReturnsNotFound_ShouldReportEndpointUnavailable()
    {
        var workerId = Guid.NewGuid();
        var handler = new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/auth/login")
                return Json(HttpStatusCode.OK, """{"token":"jwt","username":"admin","role":"Admin"}""");

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/api/workers")
                return Json(HttpStatusCode.OK, $$"""[{"id":"{{workerId}}","name":"DESKTOP-NMC4AGI","status":"Offline","isEmbedded":false,"lastSeenAt":null,"maxDiskUsageGb":10,"repoStoragePath":null}]""");

            if (request.Method == HttpMethod.Delete && request.RequestUri?.AbsolutePath == $"/api/workers/{workerId}")
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = CreateClient(handler);

        var result = await client.UnregisterWorkerByName("DESKTOP-NMC4AGI", "admin", "admin");

        result.Status.Should().Be(WorkerUnregisterStatus.DeleteEndpointUnavailable);
        result.WorkerId.Should().Be(workerId);
        result.WorkerName.Should().Be("DESKTOP-NMC4AGI");
    }

    [Fact]
    public async Task UnregisterWorkerByName_WhenLoginFails_ShouldReturnAuthenticationFailed()
    {
        var handler = new StubHandler(request =>
            request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/auth/login"
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient(handler);

        var result = await client.UnregisterWorkerByName("DESKTOP-NMC4AGI", "admin", "wrong");

        result.Status.Should().Be(WorkerUnregisterStatus.AuthenticationFailed);
        handler.Requests.Should().ContainSingle();
    }

    private static FleetAdminClient CreateClient(HttpMessageHandler handler) =>
        new(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000")
        });

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(handle(request));
        }
    }
}
