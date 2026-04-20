using System.Collections.ObjectModel;
using System.Reactive;
using DotnetFleet.Api.Client;
using DotnetFleet.Core.Domain;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace DotnetFleet.ViewModels;

public partial class ProjectsViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;
    private readonly MainViewModel _main;

    [Reactive] private ProjectViewModel? _selectedProject;
    [Reactive] private bool _isLoading;

    public ObservableCollection<ProjectViewModel> Projects { get; } = [];

    public ProjectsViewModel(FleetApiClient client, MainViewModel main)
    {
        _client = client;
        _main = main;

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadProjectsAsync);
        AddProjectCommand = ReactiveCommand.Create(OpenAddProject);

        RefreshCommand.ThrownExceptions.Subscribe(_ => { });
        RefreshCommand.Execute(Unit.Default).Subscribe();
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> AddProjectCommand { get; }

    private async Task LoadProjectsAsync()
    {
        IsLoading = true;
        try
        {
            var list = await _client.GetProjectsAsync();
            Projects.Clear();
            foreach (var p in list)
                Projects.Add(new ProjectViewModel(p, _client, _main, this));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void OpenAddProject()
    {
        var vm = new AddProjectViewModel(_client, _main, this);
        _main.NavigateTo(vm);
    }
}

public partial class ProjectViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;
    private readonly MainViewModel _main;
    private readonly ProjectsViewModel _parent;

    public Project Project { get; }

    public ProjectViewModel(Project project, FleetApiClient client, MainViewModel main, ProjectsViewModel parent)
    {
        Project = project;
        _client = client;
        _main = main;
        _parent = parent;

        OpenCommand = ReactiveCommand.Create(Open);
        EditCommand = ReactiveCommand.Create(Edit);
        DeleteCommand = ReactiveCommand.CreateFromTask(DeleteAsync);
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> OpenCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> EditCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> DeleteCommand { get; }

    private void Open()
    {
        var vm = new ProjectDetailViewModel(Project, _client, _main);
        _main.NavigateTo(vm);
    }

    private void Edit()
    {
        var vm = new EditProjectViewModel(Project, _client, _main, _parent);
        _main.NavigateTo(vm);
    }

    private async Task DeleteAsync()
    {
        await _client.DeleteProjectAsync(Project.Id);
        _parent.RefreshCommand.Execute(System.Reactive.Unit.Default).Subscribe();
    }
}
