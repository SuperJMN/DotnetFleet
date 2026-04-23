using System.Collections.ObjectModel;
using System.Reactive;
using DotnetFleet.Api.Client;
using DotnetFleet.Core.Domain;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Zafiro.UI.Navigation;
using Zafiro.UI.Shell.Utils;

namespace DotnetFleet.ViewModels;

[Section(name: "projects", icon: "mdi-folder-outline", sortIndex: 0)]
public partial class ProjectsViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;
    internal readonly INavigator Navigator;

    [Reactive] private ProjectViewModel? _selectedProject;
    [Reactive] private bool _isLoading;

    public ObservableCollection<ProjectViewModel> Projects { get; } = [];

    public ProjectsViewModel(FleetApiClient client, INavigator navigator)
    {
        _client = client;
        Navigator = navigator;

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadProjectsAsync);
        AddProjectCommand = ReactiveCommand.Create(OpenAddProject);

        RefreshCommand.ThrownExceptions.Subscribe(_ => { });
        RefreshCommand.Execute(Unit.Default).Subscribe(_ => { }, _ => { });
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
                Projects.Add(new ProjectViewModel(p, _client, Navigator, this));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void OpenAddProject()
    {
        Navigator.Go(() => new AddProjectViewModel(_client, Navigator, this));
    }
}

public partial class ProjectViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;
    private readonly INavigator _navigator;
    private readonly ProjectsViewModel _parent;

    public Project Project { get; }

    public ProjectViewModel(Project project, FleetApiClient client, INavigator navigator, ProjectsViewModel parent)
    {
        Project = project;
        _client = client;
        _navigator = navigator;
        _parent = parent;

        OpenCommand = ReactiveCommand.Create(Open);
        EditCommand = ReactiveCommand.Create(Edit);
        DeleteCommand = ReactiveCommand.CreateFromTask(DeleteAsync);
    }

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<Unit, Unit> EditCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }

    private void Open()
    {
        _navigator.Go(() => new ProjectDetailViewModel(Project, _client, _navigator));
    }

    private void Edit()
    {
        _navigator.Go(() => new EditProjectViewModel(Project, _client, _navigator, _parent));
    }

    private async Task DeleteAsync()
    {
        await _client.DeleteProjectAsync(Project.Id);
        _parent.RefreshCommand.Execute(Unit.Default).Subscribe();
    }
}
