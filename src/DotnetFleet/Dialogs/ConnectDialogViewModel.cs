using System.Reactive.Linq;
using DotnetFleet.Api.Client;
using DotnetFleet.ViewModels;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace DotnetFleet.Dialogs;

public partial class ConnectDialogViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;
    private readonly ISettingsService _settings;

    [Reactive] private string _endpointUrl;
    [Reactive] private string? _error;
    [Reactive] private bool _isBusy;

    public ConnectDialogViewModel(FleetApiClient client, ISettingsService settings)
    {
        _client = client;
        _settings = settings;
        _endpointUrl = settings.GetEndpoint() ?? client.BaseAddress?.ToString().TrimEnd('/') ?? "http://localhost:5000";
    }

    public IObservable<bool> CanConnect => this.WhenAnyValue(
        x => x.EndpointUrl, x => x.IsBusy,
        (url, busy) => !busy && !string.IsNullOrWhiteSpace(url));

    public async Task<bool> TryConnectAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            var url = EndpointUrl.TrimEnd('/');
            using var probe = new HttpClient
            {
                BaseAddress = new Uri(url + "/"),
                Timeout = TimeSpan.FromSeconds(3)
            };
            var response = await probe.GetAsync("health");
            if (!response.IsSuccessStatusCode)
            {
                Error = $"Unexpected response: {(int)response.StatusCode} {response.ReasonPhrase}";
                return false;
            }

            _client.SetBaseAddress(url);
            _settings.SetEndpoint(url);
            return true;
        }
        catch (TaskCanceledException)
        {
            Error = "Cannot connect: timeout reaching the coordinator.";
            return false;
        }
        catch (Exception ex)
        {
            Error = $"Cannot connect: {ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
