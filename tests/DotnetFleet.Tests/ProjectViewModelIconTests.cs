using System.Net;
using DotnetFleet.Api.Client;
using DotnetFleet.Core.Domain;
using DotnetFleet.ViewModels;
using ReactiveUI.Builder;

namespace DotnetFleet.Tests;

public sealed class ProjectViewModelIconTests
{
    private static readonly byte[] Png =
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=");

    static ProjectViewModelIconTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
    }

    [Fact]
    public async Task LoadProjectIcon_SetsIconBytesWhenApiReturnsBytes()
    {
        var project = new Project { Id = Guid.NewGuid(), Name = "App" };
        var client = CreateClient(project.Id, new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Png)
        });
        var vm = new ProjectViewModel(
            project,
            client,
            Substitute.For<Zafiro.UI.Navigation.INavigator>(),
            null!,
            Substitute.For<Zafiro.UI.IFileSystemPicker>(),
            Substitute.For<Zafiro.Avalonia.Dialogs.IDialog>());

        await vm.LoadProjectIcon();

        vm.HasProjectIcon.Should().BeTrue();
        vm.HasNoProjectIcon.Should().BeFalse();
        vm.ProjectIconBytes.Should().Equal(Png);
    }

    [Fact]
    public async Task LoadProjectIcon_LeavesFallbackWhenApiReturnsNotFound()
    {
        var project = new Project { Id = Guid.NewGuid(), Name = "App" };
        var client = CreateClient(project.Id, new HttpResponseMessage(HttpStatusCode.NotFound));
        var vm = new ProjectViewModel(
            project,
            client,
            Substitute.For<Zafiro.UI.Navigation.INavigator>(),
            null!,
            Substitute.For<Zafiro.UI.IFileSystemPicker>(),
            Substitute.For<Zafiro.Avalonia.Dialogs.IDialog>());

        await vm.LoadProjectIcon();

        vm.HasProjectIcon.Should().BeFalse();
        vm.HasNoProjectIcon.Should().BeTrue();
        vm.ProjectIconBytes.Should().BeNull();
    }

    private static FleetApiClient CreateClient(Guid projectId, HttpResponseMessage iconResponse)
    {
        var handler = new StubHandler(request =>
            request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == $"/api/projects/{projectId}/icon"
                ? iconResponse
                : new HttpResponseMessage(HttpStatusCode.NotFound));
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
