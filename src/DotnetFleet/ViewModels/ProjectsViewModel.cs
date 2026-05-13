using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DotnetFleet.Api.Client;
using DotnetFleet.Core.Domain;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Zafiro.Avalonia.Dialogs;
using Zafiro.UI;
using Zafiro.UI.Commands;
using Zafiro.UI.Navigation;
using Zafiro.UI.Shell.Utils;
using DialogOption = Zafiro.Avalonia.Dialogs.Option;

namespace DotnetFleet.ViewModels;

[Section(name: "Projects", icon: "mdi-folder-outline", sortIndex: 0)]
public partial class ProjectsViewModel : ReactiveObject, IHaveHeader, IDisposable
{
    private readonly FleetApiClient _client;
    private readonly IFileSystemPicker _fileSystemPicker;
    private readonly IDialog _dialog;
    private readonly CompositeDisposable _disposables = [];
    internal readonly INavigator Navigator;

    [Reactive] private ProjectViewModel? _selectedProject;
    [Reactive] private bool _isLoading;

    public ObservableCollection<ProjectViewModel> Projects { get; } = [];

    public ProjectsViewModel(FleetApiClient client, INavigator navigator, IFileSystemPicker fileSystemPicker, IDialog dialog)
    {
        _client = client;
        _fileSystemPicker = fileSystemPicker;
        _dialog = dialog;
        Navigator = navigator;

        var refresh = ReactiveCommand.CreateFromTask(LoadProjectsAsync);
        _disposables.Add(refresh.ThrownExceptions.Subscribe(_ => { }));
        RefreshCommand = refresh.Enhance("Refresh");

        var addProject = ReactiveCommand.CreateFromTask(OpenAddProjectAsync);
        _disposables.Add(addProject.ThrownExceptions.Subscribe(_ => { }));
        AddProjectCommand = addProject.Enhance("Add Project");

        Header = Observable.Return<object>(new SectionHeader("Projects",
            new HeaderAction("Add Project", "mdi-plus", AddProjectCommand, isPrimary: true),
            new HeaderAction("Refresh", "mdi-refresh", RefreshCommand)));

        _disposables.Add(AutoRefresh.Start(_client.AuthenticatedChanges, refresh, AutoRefreshIntervals.Section));
    }

    public IEnhancedCommand<Unit> RefreshCommand { get; }
    public IEnhancedCommand<Unit> AddProjectCommand { get; }
    public IObservable<object> Header { get; }
    public void Dispose() => _disposables.Dispose();

    private async Task LoadProjectsAsync()
    {
        IsLoading = true;
        try
        {
            var list = await _client.GetProjectsAsync();
            ObservableCollectionSync.Sync(
                Projects,
                list,
                project => project.Id,
                viewModel => viewModel.Project.Id,
                project => new ProjectViewModel(project, _client, Navigator, this, _fileSystemPicker, _dialog),
                (viewModel, project) => viewModel.ApplyProjectUpdate(project));

            foreach (var project in Projects)
                _ = project.LoadProjectIcon();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task OpenAddProjectAsync()
    {
        var vm = new AddProjectViewModel(_client);

        var created = await _dialog.Show(vm, "Add Project", (_, closeable) =>
        {
            var saveCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                if (await vm.TrySaveAsync())
                {
                    closeable.Close();
                }
            }, vm.CanSave).Enhance();

            return new IOption[]
            {
                new DialogOption("Cancel",
                    ReactiveCommand.Create(closeable.Dismiss).Enhance(),
                    new Settings { IsCancel = true, Role = OptionRole.Cancel }),
                new DialogOption("Save",
                    saveCommand,
                    new Settings { IsDefault = true, Role = OptionRole.Primary }),
            };
        });

        if (created)
        {
            await LoadProjectsAsync();
        }
    }
}

public partial class ProjectViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;
    private readonly INavigator _navigator;
    private readonly ProjectsViewModel _parent;
    private readonly IFileSystemPicker _fileSystemPicker;
    private readonly IDialog _dialog;
    private Guid? loadedIconProjectId;

    public Project Project { get; private set; }
    [Reactive] private byte[]? _projectIconBytes;
    [Reactive] private bool _hasProjectIcon;
    [Reactive] private bool _hasNoProjectIcon = true;
    [Reactive] private bool _isIconLoading;

    public ProjectViewModel(
        Project project,
        FleetApiClient client,
        INavigator navigator,
        ProjectsViewModel parent,
        IFileSystemPicker fileSystemPicker,
        IDialog dialog)
    {
        Project = project;
        _client = client;
        _navigator = navigator;
        _parent = parent;
        _fileSystemPicker = fileSystemPicker;
        _dialog = dialog;

        OpenCommand = ReactiveCommand.Create(Open);
        EditCommand = ReactiveCommand.Create(Edit);
        DeleteCommand = ReactiveCommand.CreateFromTask(DeleteAsync);
    }

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<Unit, Unit> EditCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }

    public void ApplyProjectUpdate(Project updated)
    {
        if (updated.Id != Project.Id) return;

        var iconInvalidated =
            !string.Equals(Project.GitUrl, updated.GitUrl, StringComparison.Ordinal)
            || !string.Equals(Project.Branch, updated.Branch, StringComparison.Ordinal)
            || !string.Equals(Project.GitToken, updated.GitToken, StringComparison.Ordinal);

        Project = updated;
        this.RaisePropertyChanged(nameof(Project));

        if (iconInvalidated)
        {
            loadedIconProjectId = null;
            SetProjectIcon(null);
        }
    }

    public async Task LoadProjectIcon()
    {
        if (IsIconLoading || loadedIconProjectId == Project.Id)
            return;

        var projectId = Project.Id;
        IsIconLoading = true;

        try
        {
            var bytes = await _client.GetProjectIcon(projectId);
            if (Project.Id != projectId)
                return;

            SetProjectIcon(bytes);
            loadedIconProjectId = projectId;
        }
        catch
        {
            if (Project.Id == projectId)
                SetProjectIcon(null);
        }
        finally
        {
            if (Project.Id == projectId)
                IsIconLoading = false;
        }
    }

    private void Open()
    {
        _navigator.Go(() => new ProjectDetailViewModel(Project, _client, _navigator, _fileSystemPicker, _dialog));
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

    private void SetProjectIcon(byte[]? bytes)
    {
        ProjectIconBytes = bytes is { Length: > 0 } ? bytes : null;
        HasProjectIcon = ProjectIconBytes is not null;
        HasNoProjectIcon = ProjectIconBytes is null;
    }
}
