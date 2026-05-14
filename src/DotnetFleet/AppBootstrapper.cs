using CSharpFunctionalExtensions;
using DotnetFleet.Api.Client;
using DotnetFleet.ViewModels;

namespace DotnetFleet;

public class AppBootstrapper
{
    private readonly FleetApiClient _client;
    private readonly ISettingsService _settings;
    private readonly IBackendHealthMonitor _health;
    private readonly IConnectedFleetClientContext _clientContext;
    private readonly IFleetClientConnectionFlow _connectionFlow;

    public AppBootstrapper(
        FleetApiClient client,
        ISettingsService settings,
        IBackendHealthMonitor health,
        IConnectedFleetClientContext clientContext,
        IFleetClientConnectionFlow connectionFlow)
    {
        _client = client;
        _settings = settings;
        _health = health;
        _clientContext = clientContext;
        _connectionFlow = connectionFlow;

        _client.Unauthorized.Subscribe(_ =>
        {
            _client.ClearToken();
            _settings.ClearToken();
        });
    }

    public async Task RunAsync()
    {
        var endpoint = _settings.GetEndpoint();
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            _client.SetBaseAddress(endpoint);
        }

        var token = _settings.GetToken();
        if (!string.IsNullOrWhiteSpace(token))
        {
            _client.SetToken(token);
        }

        _health.Start();
        await Task.CompletedTask;
    }

    public async Task<Maybe<Result<FleetApiClient>>> ShowConnect()
    {
        var result = await _connectionFlow.Connect();
        if (result.HasNoValue)
            return Maybe<Result<FleetApiClient>>.None;
        if (result.Value.IsFailure)
            return Maybe.From(Result.Failure<FleetApiClient>(result.Value.Error));

        return await _clientContext.Require();
    }

    public async Task<Maybe<Result<FleetApiClient>>> Logout()
    {
        _settings.ClearToken();
        _settings.ClearCredentials();
        _client.ClearToken();
        return await _clientContext.Require();
    }
}
