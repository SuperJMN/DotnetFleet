using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using DotnetFleet.Api.Client;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace DotnetFleet.ViewModels;

public partial class LoginViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;
    private readonly ISettingsService _settings;
    private readonly Subject<Unit> _loggedIn = new();

    [Reactive] private string _username = string.Empty;
    [Reactive] private string _password = string.Empty;
    [Reactive] private string? _error;
    [Reactive] private bool _isBusy;

    public IObservable<Unit> LoggedIn => _loggedIn.AsObservable();

    public LoginViewModel(FleetApiClient client, ISettingsService settings)
    {
        _client = client;
        _settings = settings;

        var canLogin = this.WhenAnyValue(
            x => x.Username, x => x.Password,
            (u, p) => !string.IsNullOrWhiteSpace(u) && !string.IsNullOrWhiteSpace(p));

        LoginCommand = ReactiveCommand.CreateFromTask(ExecuteLoginAsync, canLogin);
        LoginCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message);
    }

    public ReactiveCommand<Unit, Unit> LoginCommand { get; }

    private async Task ExecuteLoginAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            var result = await _client.LoginAsync(Username, Password);
            if (result is null)
            {
                Error = "Login failed — no token returned.";
                return;
            }
            _settings.SetToken(result.Token);
            _client.SetToken(result.Token);
            _loggedIn.OnNext(Unit.Default);
        }
        catch (Exception ex)
        {
            Error = ex.Message.Contains("401") || ex.Message.Contains("Unauthorized")
                ? "Invalid username or password."
                : $"Login error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
