using System.Reactive.Linq;
using CSharpFunctionalExtensions;
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
        var credentials = settings.GetCredentials();
        if (credentials is not null)
        {
            _username = credentials.Username;
            _password = credentials.Password;
        }
    }

    public IObservable<bool> CanLogin => this.WhenAnyValue(
        x => x.Username, x => x.Password, x => x.IsBusy,
        (u, p, busy) => !busy && !string.IsNullOrWhiteSpace(u) && !string.IsNullOrWhiteSpace(p));

    public async Task<Result> TryLogin()
    {
        Error = null;
        IsBusy = true;
        try
        {
            var result = await _client.LoginAsync(Username, Password);
            if (result is null)
            {
                Error = "Login failed — no token returned.";
                return Result.Failure(Error);
            }

            _settings.SetToken(result.Token);
            _settings.SetCredentials(Username, Password);
            _client.SetToken(result.Token);
            return Result.Success();
        }
        catch (Exception ex)
        {
            Error = ex.Message.Contains("401") || ex.Message.Contains("Unauthorized")
                ? "Invalid username or password."
                : $"Login error: {ex.Message}";
            return Result.Failure(Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> TryLoginAsync() => (await TryLogin()).IsSuccess;
}
