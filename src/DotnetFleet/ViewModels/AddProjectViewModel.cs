using System.Reactive;
using DotnetFleet.Api.Client;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Zafiro.UI.Navigation;

namespace DotnetFleet.ViewModels;

public partial class AddProjectViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;
    private readonly INavigator _navigator;
    private readonly ProjectsViewModel _projects;

    [Reactive] private string _name = string.Empty;
    [Reactive] private string _gitUrl = string.Empty;
    [Reactive] private string _branch = "main";
    [Reactive] private string _pollingInterval = "0";
    [Reactive] private string _gitToken = string.Empty;
    [Reactive] private string? _error;
    [Reactive] private bool _isBusy;

    public AddProjectViewModel(FleetApiClient client, INavigator navigator, ProjectsViewModel projects)
    {
        _client = client;
        _navigator = navigator;
        _projects = projects;

        var canSave = this.WhenAnyValue(
            x => x.Name, x => x.GitUrl, x => x.Branch,
            (n, g, b) => !string.IsNullOrWhiteSpace(n) && !string.IsNullOrWhiteSpace(g) && !string.IsNullOrWhiteSpace(b));

        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, canSave);
        CancelCommand = ReactiveCommand.Create(Cancel);

        SaveCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message);
    }

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    private async Task SaveAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            int polling = int.TryParse(PollingInterval, out var p) ? p : 0;
            var token = string.IsNullOrWhiteSpace(GitToken) ? null : GitToken;
            await _client.CreateProjectAsync(Name, GitUrl, Branch, polling, token);
            _projects.RefreshCommand.Execute(Unit.Default).Subscribe();
            await _navigator.GoBack();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Cancel() => _navigator.GoBack();
}
