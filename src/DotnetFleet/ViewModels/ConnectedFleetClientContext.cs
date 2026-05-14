using CSharpFunctionalExtensions;
using DotnetFleet.Api.Client;

namespace DotnetFleet.ViewModels;

public interface IConnectedFleetClientContext
{
    Task<Maybe<Result<FleetApiClient>>> Require();
}

public interface IFleetClientConnectionFlow
{
    Task<Maybe<Result>> Connect();
    Task<Maybe<Result>> Login();
}

public sealed class ConnectedFleetClientContext(
    FleetApiClient client,
    ISettingsService settings,
    IBackendHealthMonitor health,
    IFleetClientConnectionFlow flow) : IConnectedFleetClientContext
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<Maybe<Result<FleetApiClient>>> Require()
    {
        await gate.WaitAsync();
        try
        {
            return await RequireCore();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Maybe<Result<FleetApiClient>>> RequireCore()
    {
        ApplyStoredEndpoint();
        ApplyStoredToken();

        var connected = await EnsureConnected();
        if (connected.HasNoValue)
            return Maybe<Result<FleetApiClient>>.None;
        if (connected.Value.IsFailure)
            return Maybe.From(Result.Failure<FleetApiClient>(connected.Value.Error));

        var authenticated = await EnsureAuthenticated();
        if (authenticated.HasNoValue)
            return Maybe<Result<FleetApiClient>>.None;

        return Maybe.From(authenticated.Value.Map(() => client));
    }

    private void ApplyStoredEndpoint()
    {
        if (client.BaseAddress is not null)
            return;

        var endpoint = settings.GetEndpoint();
        if (!string.IsNullOrWhiteSpace(endpoint))
            client.SetBaseAddress(endpoint);
    }

    private void ApplyStoredToken()
    {
        if (client.IsAuthenticated)
            return;

        var token = settings.GetToken();
        if (!string.IsNullOrWhiteSpace(token))
            client.SetToken(token);
    }

    private async Task<Maybe<Result>> EnsureConnected()
    {
        if (client.BaseAddress is not null)
        {
            var snapshot = await health.CheckNowAsync();
            if (snapshot.State == BackendHealth.Healthy)
                return Maybe.From(Result.Success());
        }

        var result = await flow.Connect();
        if (result.HasNoValue || result.Value.IsFailure)
            return result;

        var check = await health.CheckNowAsync();
        return check.State == BackendHealth.Healthy
            ? Maybe.From(Result.Success())
            : Maybe.From(Result.Failure(check.Message ?? "Coordinator endpoint is not reachable."));
    }

    private async Task<Maybe<Result>> EnsureAuthenticated()
    {
        if (!client.IsAuthenticated)
            return await LoginWithStoredCredentialsOrPrompt();

        var session = await Result.Try(() => client.GetSession());
        if (session.IsSuccess)
            return Maybe.From(Result.Success());

        client.ClearToken();
        settings.ClearToken();
        return await LoginWithStoredCredentialsOrPrompt();
    }

    private async Task<Maybe<Result>> LoginWithStoredCredentialsOrPrompt()
    {
        var cachedLogin = await LoginWithStoredCredentials();
        if (cachedLogin.IsSuccess)
            return Maybe.From(Result.Success());

        if (cachedLogin.IsFailure)
            settings.ClearCredentials();

        return await flow.Login();
    }

    private async Task<Result> LoginWithStoredCredentials()
    {
        var credentials = settings.GetCredentials();
        if (credentials is null)
            return Result.Failure("No stored credentials.");

        var login = await Result.Try(() => client.LoginAsync(credentials.Username, credentials.Password));
        if (login.IsFailure || login.Value is null)
            return Result.Failure(login.IsFailure ? login.Error : "Login failed.");

        settings.SetToken(login.Value.Token);
        client.SetToken(login.Value.Token);
        return Result.Success();
    }
}
