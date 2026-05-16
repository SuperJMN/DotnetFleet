using System.Net;
using System.Net.Http.Json;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using DotnetFleet.Api.Client;
using DotnetFleet.Core.Domain;
using DotnetFleet.ViewModels;
using ReactiveUI.Builder;

namespace DotnetFleet.Tests;

public sealed class BuildsViewModelTests
{
    private static readonly byte[] IconBytes = [1, 2, 3];

    static BuildsViewModelTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

    [Fact]
    public async Task ClearFinishedJobsCommand_WhenExecuted_ShouldDeleteFinishedJobsAndReloadBuilds()
    {
        var projectId = Guid.NewGuid();
        var finished = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Status = JobStatus.Succeeded,
            EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            FinishedAt = DateTimeOffset.UtcNow
        };
        var running = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Status = JobStatus.Running,
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        var requests = new List<(HttpMethod Method, string Path)>();
        var getJobsCalls = 0;
        var client = CreateClient(request =>
        {
            requests.Add((request.Method, request.RequestUri!.AbsolutePath));

            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/api/projects")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new[]
                    {
                        new Project { Id = projectId, Name = "Fleet", GitUrl = "https://example.test/repo.git", Branch = "main" }
                    })
                };
            }

            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/api/jobs")
            {
                getJobsCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(getJobsCalls == 1
                        ? new[] { finished, running }
                        : [running])
                };
            }

            if (request.Method == HttpMethod.Delete && request.RequestUri.AbsolutePath == "/api/jobs/finished")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { deleted = 1 })
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var context = Substitute.For<IConnectedFleetClientContext>();
        context.Require().Returns(Task.FromResult(Maybe.From(Result.Success(client))));
        using var vm = CreateViewModel(context);

        await vm.RefreshCommand.Execute().FirstAsync();
        await vm.ClearFinishedJobsCommand.Execute().FirstAsync();

        vm.Builds.Should().ContainSingle().Which.Job.Id.Should().Be(running.Id);
        getJobsCalls.Should().Be(2);
        requests.Should().ContainSingle(request =>
            request.Method == HttpMethod.Delete && request.Path == "/api/jobs/finished");
    }

    [Fact]
    public async Task RefreshCommand_ShouldLoadProjectIconsForBuildRows()
    {
        var projectId = Guid.NewGuid();
        var job = new DeploymentJob
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Status = JobStatus.Succeeded,
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        var iconPath = $"/api/projects/{projectId}/icon";
        var requests = new List<(HttpMethod Method, string Path)>();
        var client = CreateClient(request =>
        {
            requests.Add((request.Method, request.RequestUri!.AbsolutePath));

            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/api/projects")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new[]
                    {
                        new Project { Id = projectId, Name = "Fleet", GitUrl = "https://example.test/repo.git", Branch = "main" }
                    })
                };
            }

            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/api/jobs")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new[] { job })
                };
            }

            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == iconPath)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(IconBytes)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var context = Substitute.For<IConnectedFleetClientContext>();
        context.Require().Returns(Task.FromResult(Maybe.From(Result.Success(client))));
        using var vm = CreateViewModel(context);

        await vm.RefreshCommand.Execute().FirstAsync();

        var build = vm.Builds.Should().ContainSingle().Subject;
        build.ProjectIconBytes.Should().Equal(IconBytes);
        build.HasProjectIcon.Should().BeTrue();
        build.HasNoProjectIcon.Should().BeFalse();
        requests.Should().ContainSingle(request =>
            request.Method == HttpMethod.Get && request.Path == iconPath);
    }

    [Fact]
    public async Task Header_ShouldExposeClearBuildsAction()
    {
        using var vm = CreateViewModel(Substitute.For<IConnectedFleetClientContext>());

        var header = (SectionHeader)await vm.Header.FirstAsync();

        var action = header.Actions.Should().ContainSingle(action => action.Text == "Clear Builds").Subject;
        action.IconCode.Should().Be("mdi-broom");
        action.Command.Should().BeSameAs(vm.ClearFinishedJobsCommand);
    }

    private static BuildsViewModel CreateViewModel(IConnectedFleetClientContext context) =>
        new(
            context,
            Substitute.For<Zafiro.UI.Navigation.INavigator>(),
            Substitute.For<Zafiro.UI.IFileSystemPicker>(),
            Substitute.For<Zafiro.UI.INotificationService>());

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
