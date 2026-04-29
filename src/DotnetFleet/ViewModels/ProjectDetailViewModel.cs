using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media;
using DotnetFleet.Api.Client;
using DotnetFleet.Core.Domain;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Zafiro.UI;
using Zafiro.UI.Navigation;

namespace DotnetFleet.ViewModels;

public partial class ProjectDetailViewModel : ReactiveObject, IHaveHeader
{
    private readonly FleetApiClient _client;
    private readonly INavigator _navigator;

    public Project Project { get; }
    public ProjectSecretsViewModel ProjectSecrets { get; }

    [Reactive] private bool _isLoading;
    [Reactive] private string? _error;

    public ObservableCollection<JobViewModel> Jobs { get; } = [];

    public ProjectDetailViewModel(Project project, FleetApiClient client, INavigator navigator)
    {
        Project = project;
        _client = client;
        _navigator = navigator;

        ProjectSecrets = new ProjectSecretsViewModel(project.Id, client);

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadJobsAsync);
        DeployCommand = ReactiveCommand.CreateFromTask(DeployAsync);
        ClearFinishedJobsCommand = ReactiveCommand.CreateFromTask(ClearFinishedJobsAsync);
        EditCommand = ReactiveCommand.Create(() =>
        {
            _navigator.Go(() => new EditProjectViewModel(Project, _client, _navigator, projectsForRefresh: null));
        });

        RefreshCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message);
        DeployCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message);
        ClearFinishedJobsCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message);
        RefreshCommand.Execute(Unit.Default).Subscribe();

        Header = Observable.Return<object>(new SectionHeader(
            Project.Name,
            $"{Project.GitUrl} @ {Project.Branch}",
            new HeaderAction("Edit", "mdi-pencil", EditCommand),
            new HeaderAction("Clear finished", "mdi-broom", ClearFinishedJobsCommand),
            new HeaderAction("Refresh", "mdi-refresh", RefreshCommand),
            new HeaderAction("Deploy Now", "mdi-rocket-launch", DeployCommand, IsPrimary: true)));
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> DeployCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearFinishedJobsCommand { get; }
    public ReactiveCommand<Unit, Unit> EditCommand { get; }
    public IObservable<object> Header { get; }

    private async Task LoadJobsAsync()
    {
        IsLoading = true;
        try
        {
            var jobs = await _client.GetProjectJobsAsync(Project.Id);
            Jobs.Clear();
            foreach (var j in jobs.OrderByDescending(j => j.EnqueuedAt))
                Jobs.Add(new JobViewModel(j, _client, _navigator, this));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task DeployAsync()
    {
        var job = await _client.EnqueueDeployAsync(Project.Id);
        Jobs.Insert(0, new JobViewModel(job, _client, _navigator, this));
        await _navigator.Go(() => new JobDetailViewModel(job, _client, _navigator, this));
    }

    private async Task ClearFinishedJobsAsync()
    {
        await _client.DeleteFinishedProjectJobsAsync(Project.Id);
        await LoadJobsAsync();
    }
}

public partial class JobViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;
    private readonly INavigator _navigator;
    private readonly ProjectDetailViewModel _projectDetail;

    public DeploymentJob Job { get; }

    private string? _version;
    private string _displayName = string.Empty;

    public JobViewModel(DeploymentJob job, FleetApiClient client, INavigator navigator, ProjectDetailViewModel projectDetail)
    {
        Job = job;
        _client = client;
        _navigator = navigator;
        _projectDetail = projectDetail;
        _version = job.Version;
        _displayName = ComputeDisplayName(job.Version);

        OpenCommand = ReactiveCommand.Create(Open);
    }

    /// <summary>The detected GitVersion-style version, or null if none was reported yet.</summary>
    public string? Version
    {
        get => _version;
        private set
        {
            if (_version == value) return;
            this.RaiseAndSetIfChanged(ref _version, value);
            DisplayName = ComputeDisplayName(value);
        }
    }

    /// <summary>
    /// Friendly label shown in deployment lists. Falls back to a short prefix of the
    /// job GUID until a version is detected from the worker's log stream.
    /// </summary>
    public string DisplayName
    {
        get => _displayName;
        private set => this.RaiseAndSetIfChanged(ref _displayName, value);
    }

    /// <summary>
    /// Pushes a fresher snapshot of the underlying <see cref="DeploymentJob"/> into this VM,
    /// raising change notifications for the user-visible fields. Used when the deployment is
    /// renamed (a version was detected) or any other field is updated by the coordinator.
    /// </summary>
    public void ApplyJobUpdate(DeploymentJob updated)
    {
        if (updated.Id != Job.Id) return;
        Job.Version = updated.Version;
        Job.Status = updated.Status;
        Version = updated.Version;
        this.RaisePropertyChanged(nameof(StatusText));
        this.RaisePropertyChanged(nameof(StatusIcon));
        this.RaisePropertyChanged(nameof(StatusBrush));
    }

    private string ComputeDisplayName(string? version) =>
        string.IsNullOrWhiteSpace(version) ? Job.Id.ToString("N")[..8] : version!;

    public string StatusText => Job.Status switch
    {
        JobStatus.Queued => "Queued",
        JobStatus.Assigned => "Assigned",
        JobStatus.Running => "Running",
        JobStatus.Succeeded => "Succeeded",
        JobStatus.Failed => "Failed",
        JobStatus.Cancelled => "Cancelled",
        _ => Job.Status.ToString()
    };

    public IIcon StatusIcon => new Icon(Job.Status switch
    {
        JobStatus.Queued => "mdi-clock-outline",
        JobStatus.Assigned => "mdi-account-arrow-right-outline",
        JobStatus.Running => "mdi-rocket-launch",
        JobStatus.Succeeded => "mdi-check-circle",
        JobStatus.Failed => "mdi-alert-circle",
        JobStatus.Cancelled => "mdi-cancel",
        _ => "mdi-help-circle-outline"
    });

    public IBrush StatusBrush => Job.Status switch
    {
        JobStatus.Queued => new SolidColorBrush(Color.Parse("#9E9E9E")),
        JobStatus.Assigned => new SolidColorBrush(Color.Parse("#42A5F5")),
        JobStatus.Running => new SolidColorBrush(Color.Parse("#29B6F6")),
        JobStatus.Succeeded => new SolidColorBrush(Color.Parse("#43A047")),
        JobStatus.Failed => new SolidColorBrush(Color.Parse("#E53935")),
        JobStatus.Cancelled => new SolidColorBrush(Color.Parse("#FB8C00")),
        _ => Brushes.Gray
    };

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> OpenCommand { get; }

    private void Open()
    {
        _navigator.Go(() => new JobDetailViewModel(Job, _client, _navigator, _projectDetail));
    }
}
