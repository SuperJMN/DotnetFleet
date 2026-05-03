using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using DotnetFleet.Api.Client;
using DotnetFleet.Core.Domain;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Zafiro.UI;
using Zafiro.UI.Commands;
using Zafiro.UI.Navigation;
using Zafiro.UI.Shell.Utils;

namespace DotnetFleet.ViewModels;

[Section(name: "Projects", icon: "mdi-folder-outline", sortIndex: 0)]
public partial class ProjectsViewModel : ReactiveObject, IHaveHeader
{
    private readonly FleetApiClient _client;
    private readonly IFileSystemPicker _fileSystemPicker;
    internal readonly INavigator Navigator;

    [Reactive] private ProjectViewModel? _selectedProject;
    [Reactive] private bool _isLoading;

    public ObservableCollection<ProjectViewModel> Projects { get; } = [];

    public ProjectsViewModel(FleetApiClient client, INavigator navigator, IFileSystemPicker fileSystemPicker)
    {
        _client = client;
        _fileSystemPicker = fileSystemPicker;
        Navigator = navigator;

        var refresh = ReactiveCommand.CreateFromTask(LoadProjectsAsync);
        refresh.ThrownExceptions.Subscribe(_ => { });
        RefreshCommand = refresh.Enhance("Refresh");

        AddProjectCommand = ReactiveCommand.Create(OpenAddProject).Enhance("Add Project");

        Header = Observable.Return<object>(new SectionHeader("Projects",
            new HeaderAction("Add Project", "mdi-plus", AddProjectCommand, IsPrimary: true),
            new HeaderAction("Refresh", "mdi-refresh", RefreshCommand)));

        // Refresh whenever the client becomes authenticated. BehaviorSubject replays the current
        // value, so this also covers the "already authenticated when the VM is built" case.
        _client.AuthenticatedChanges
            .Where(authenticated => authenticated)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => refresh.Execute(Unit.Default).Subscribe(_ => { }, _ => { }));
    }

    public IEnhancedCommand<Unit> RefreshCommand { get; }
    public IEnhancedCommand<Unit> AddProjectCommand { get; }
    public IObservable<object> Header { get; }

    private async Task LoadProjectsAsync()
    {
        IsLoading = true;
        try
        {
            var list = await _client.GetProjectsAsync();
            Projects.Clear();
            foreach (var p in list)
                Projects.Add(new ProjectViewModel(p, _client, Navigator, this, _fileSystemPicker));
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
    private readonly IFileSystemPicker _fileSystemPicker;

    public Project Project { get; }

    public ProjectViewModel(
        Project project,
        FleetApiClient client,
        INavigator navigator,
        ProjectsViewModel parent,
        IFileSystemPicker fileSystemPicker)
    {
        Project = project;
        _client = client;
        _navigator = navigator;
        _parent = parent;
        _fileSystemPicker = fileSystemPicker;

        OpenCommand = ReactiveCommand.Create(Open);
        EditCommand = ReactiveCommand.Create(Edit);
        DeleteCommand = ReactiveCommand.CreateFromTask(DeleteAsync);
    }

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<Unit, Unit> EditCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }

    private void Open()
    {
        _navigator.Go(() => new ProjectDetailViewModel(Project, _client, _navigator, _fileSystemPicker));
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
