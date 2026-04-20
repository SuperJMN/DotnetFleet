using DotnetFleet.Api.Client;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace DotnetFleet.ViewModels;

public partial class MainViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;
    private readonly ISettingsService _settings;
    private readonly AppViewModel _app;

    [Reactive] private object? _currentContent;

    public ProjectsViewModel Projects { get; }
    public WorkersViewModel Workers { get; }
    public SecretsViewModel Secrets { get; }

    public MainViewModel(FleetApiClient client, ISettingsService settings, AppViewModel app)
    {
        _client = client;
        _settings = settings;
        _app = app;

        Projects = new ProjectsViewModel(client, this);
        Workers = new WorkersViewModel(client);
        Secrets = new SecretsViewModel(client);

        CurrentContent = Projects;

        LogoutCommand = ReactiveCommand.Create(Logout);
        SelectProjectsCommand = ReactiveCommand.Create(() => NavigateTo(Projects));
        SelectWorkersCommand = ReactiveCommand.Create(() => NavigateTo(Workers));
        SelectSecretsCommand = ReactiveCommand.Create(() => NavigateTo(Secrets));
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> LogoutCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> SelectProjectsCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> SelectWorkersCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> SelectSecretsCommand { get; }

    public void NavigateTo(object viewModel) => CurrentContent = viewModel;

    private void Logout()
    {
        _settings.SetToken(null);
        _client.ClearToken();
        _app.NavigateToLogin();
    }
}
