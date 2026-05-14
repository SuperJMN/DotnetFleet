using System.Net;
using System.Net.Http.Json;
using DotnetFleet.Api.Client;
using DotnetFleet.Dialogs;
using DotnetFleet.ViewModels;

namespace DotnetFleet.Tests;

public sealed class LoginDialogViewModelTests
{
    [Fact]
    public async Task TryLogin_WhenSuccessful_ShouldStoreTokenAndCredentials()
    {
        var settings = new InMemorySettingsService();
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new FleetApiClient.LoginResponse("token", "admin", "Admin"))
        });
        client.SetBaseAddress("http://localhost:5000");
        var viewModel = new LoginDialogViewModel(client, settings)
        {
            Username = "admin",
            Password = "secret"
        };

        var result = await viewModel.TryLogin();

        result.IsSuccess.Should().BeTrue();
        settings.GetToken().Should().Be("token");
        settings.GetCredentials().Should().Be(new LoginCredentials("admin", "secret"));
        client.IsAuthenticated.Should().BeTrue();
    }

    [Fact]
    public void Constructor_WhenCredentialsAreStored_ShouldPrefillLoginFields()
    {
        var settings = new InMemorySettingsService();
        settings.SetCredentials("admin", "secret");
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var viewModel = new LoginDialogViewModel(client, settings);

        viewModel.Username.Should().Be("admin");
        viewModel.Password.Should().Be("secret");
    }

    private static FleetApiClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> response)
    {
        var handler = new StubHandler(response);
        return new FleetApiClient(handler, handler);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
