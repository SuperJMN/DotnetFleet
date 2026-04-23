using System.Reactive.Linq;
using DotnetFleet.Api.Client;
using DotnetFleet.ViewModels;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace DotnetFleet.Dialogs;

public partial class LoginDialogViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;
    private readonly ISettingsService _settings;

    [Reactive] private string _username = string.Empty;
    [Reactive] private string _password = string.Empty;
    [Reactive] private string? _error;
    [Reactive] private bool _isBusy;

    public LoginDialogViewModel(FleetApiClient client, ISettingsService settings)
    {
        _client = client;
        _settings = settings;
    }

    public IObservable<bool> CanLogin => this.WhenAnyValue(
        x => x.Username, x => x.Password, x => x.IsBusy,
        (u, p, busy) => !busy && !string.IsNullOrWhiteSpace(u) && !string.IsNullOrWhiteSpace(p));

    public async Task<bool> TryLoginAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            var result = await _client.LoginAsync(Username, Password);
            if (result is null)
            {
                Error = "Login failed — no token returned.";
                return false;
            }

            _settings.SetToken(result.Token);
            _client.SetToken(result.Token);
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message.Contains("401") || ex.Message.Contains("Unauthorized")
                ? "Invalid username or password."
                : $"Login error: {ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
