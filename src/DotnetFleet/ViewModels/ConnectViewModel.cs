using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using DotnetFleet.Api.Client;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace DotnetFleet.ViewModels;

public partial class ConnectViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;
    private readonly ISettingsService _settings;
    private readonly Subject<Unit> _connected = new();

    [Reactive] private string _endpointUrl = "http://localhost:5000";
    [Reactive] private string? _error;
    [Reactive] private bool _isBusy;

    public IObservable<Unit> Connected => _connected.AsObservable();

    public ConnectViewModel(FleetApiClient client, ISettingsService settings)
    {
        _client = client;
        _settings = settings;
        _endpointUrl = settings.GetEndpoint() ?? "http://localhost:5000";

        ConnectCommand = ReactiveCommand.CreateFromTask(
            ExecuteConnectAsync,
            this.WhenAnyValue(x => x.EndpointUrl, url => !string.IsNullOrWhiteSpace(url)));

        ConnectCommand.ThrownExceptions
            .Subscribe(ex => Error = ex.Message);
    }

    public ReactiveCommand<Unit, Unit> ConnectCommand { get; }

    private async Task ExecuteConnectAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            var url = EndpointUrl.TrimEnd('/');

            // Probe the dedicated health endpoint with a short timeout.
            using var probe = new HttpClient
            {
                BaseAddress = new Uri(url + "/"),
                Timeout = TimeSpan.FromSeconds(3)
            };
            var response = await probe.GetAsync("health");
            if (response.IsSuccessStatusCode)
            {
                _client.SetBaseAddress(url);
                _settings.SetEndpoint(url);
                _connected.OnNext(Unit.Default);
            }
            else
            {
                Error = $"Unexpected response: {(int)response.StatusCode} {response.ReasonPhrase}";
            }
        }
        catch (TaskCanceledException)
        {
            Error = "Cannot connect: timeout reaching the coordinator.";
        }
        catch (Exception ex)
        {
            Error = $"Cannot connect: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
