using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using DotnetFleet.Api.Client;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Zafiro.UI;
using Zafiro.UI.Navigation;
using Zafiro.UI.Shell.Utils;

namespace DotnetFleet.ViewModels;

[Section(name: "Builds", icon: "mdi-history", sortIndex: 1)]
public partial class BuildsViewModel : ReactiveObject, IHaveHeader, IDisposable
{
    private readonly IConnectedFleetClientContext clientContext;
    private readonly INavigator navigator;
    private readonly IFileSystemPicker fileSystemPicker;
    private readonly INotificationService notificationService;
    private readonly CompositeDisposable disposables = [];

    [Reactive] private bool isLoading;
    [Reactive] private string? error;

    public ObservableCollection<JobViewModel> Builds { get; } = [];

    public BuildsViewModel(
        IConnectedFleetClientContext clientContext,
        INavigator navigator,
        IFileSystemPicker fileSystemPicker,
        INotificationService notificationService)
    {
        this.clientContext = clientContext;
        this.navigator = navigator;
        this.fileSystemPicker = fileSystemPicker;
        this.notificationService = notificationService;

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadBuilds);
        disposables.Add(RefreshCommand.Results().HandleErrorsWith(notificationService, Maybe.From("Cannot load builds")));

        Header = Observable.Return<object>(new SectionHeader("Builds",
            "All projects, newest first",
            new HeaderAction("Refresh", "mdi-refresh", RefreshCommand)));
    }

    public ReactiveCommand<Unit, Maybe<Result>> RefreshCommand { get; }
    public IObservable<object> Header { get; }
    public void Dispose() => disposables.Dispose();

    private async Task<Maybe<Result>> LoadBuilds()
    {
        IsLoading = true;
        Error = null;
        try
        {
            return await clientContext.Require().Bind(async client =>
            {
                var projectsTask = client.GetProjectsAsync();
                var jobsTask = client.GetAllJobsAsync();

                await Task.WhenAll(projectsTask, jobsTask);

                var projects = await projectsTask;
                var jobs = await jobsTask;
                var projectNames = projects.ToDictionary(project => project.Id, project => project.Name);

                ObservableCollectionSync.Sync(
                    Builds,
                    jobs.OrderByDescending(job => job.EnqueuedAt),
                    job => job.Id,
                    viewModel => viewModel.Job.Id,
                    job => new JobViewModel(
                        job,
                        client,
                        navigator,
                        projectDetail: null,
                        fileSystemPicker: fileSystemPicker,
                        projectName: ResolveProjectName(job.ProjectId, projectNames),
                        clientContext: clientContext,
                        notificationService: notificationService),
                    (viewModel, job) => viewModel.ApplyJobUpdate(job, ResolveProjectName(job.ProjectId, projectNames)));
            });
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
