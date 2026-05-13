using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using DotnetFleet.Api.Client;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Zafiro.UI;
using Zafiro.UI.Navigation;
using Zafiro.UI.Shell.Utils;

namespace DotnetFleet.ViewModels;

[Section(name: "Builds", icon: "mdi-history", sortIndex: 1)]
public partial class BuildsViewModel : ReactiveObject, IHaveHeader
{
    private readonly FleetApiClient client;
    private readonly INavigator navigator;
    private readonly IFileSystemPicker fileSystemPicker;

    [Reactive] private bool isLoading;
    [Reactive] private string? error;

    public ObservableCollection<JobViewModel> Builds { get; } = [];

    public BuildsViewModel(FleetApiClient client, INavigator navigator, IFileSystemPicker fileSystemPicker)
    {
        this.client = client;
        this.navigator = navigator;
        this.fileSystemPicker = fileSystemPicker;

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadBuilds);
        RefreshCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message);

        Header = Observable.Return<object>(new SectionHeader("Builds",
            "All projects, oldest first",
            new HeaderAction("Refresh", "mdi-refresh", RefreshCommand)));

        client.AuthenticatedChanges
            .Where(authenticated => authenticated)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => RefreshCommand.Execute(Unit.Default).Subscribe(_ => { }, _ => { }));
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public IObservable<object> Header { get; }

    private async Task LoadBuilds()
    {
        IsLoading = true;
        Error = null;
        try
        {
            var projectsTask = client.GetProjectsAsync();
            var jobsTask = client.GetAllJobsAsync();

            await Task.WhenAll(projectsTask, jobsTask);

            var projects = await projectsTask;
            var jobs = await jobsTask;
            var projectNames = projects.ToDictionary(project => project.Id, project => project.Name);

            Builds.Clear();
            foreach (var job in jobs.OrderBy(job => job.EnqueuedAt))
            {
                Builds.Add(new JobViewModel(
                    job,
                    client,
                    navigator,
                    projectDetail: null,
                    fileSystemPicker: fileSystemPicker,
                    projectName: ResolveProjectName(job.ProjectId, projectNames)));
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string ResolveProjectName(Guid projectId, IReadOnlyDictionary<Guid, string> projectNames)
    {
        return projectNames.TryGetValue(projectId, out var name)
            ? name
            : $"Project {projectId.ToString("N")[..8]}";
    }
}
