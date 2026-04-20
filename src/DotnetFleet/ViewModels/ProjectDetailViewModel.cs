using System.Collections.ObjectModel;
using System.Reactive;
using DotnetFleet.Api.Client;
using DotnetFleet.Core.Domain;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace DotnetFleet.ViewModels;

public partial class ProjectDetailViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;
    private readonly MainViewModel _main;

    public Project Project { get; }
    public ProjectSecretsViewModel ProjectSecrets { get; }

    [Reactive] private bool _isLoading;
    [Reactive] private string? _error;

    public ObservableCollection<JobViewModel> Jobs { get; } = [];

    public ProjectDetailViewModel(Project project, FleetApiClient client, MainViewModel main)
    {
        Project = project;
        _client = client;
        _main = main;

        ProjectSecrets = new ProjectSecretsViewModel(project.Id, client);

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadJobsAsync);
        DeployCommand = ReactiveCommand.CreateFromTask(DeployAsync);
        BackCommand = ReactiveCommand.Create(() => _main.NavigateTo(_main.Projects));
        EditCommand = ReactiveCommand.Create(() =>
        {
            var vm = new EditProjectViewModel(Project, _client, _main, _main.Projects);
            _main.NavigateTo(vm);
        });

        RefreshCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message);
        DeployCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message);
        RefreshCommand.Execute(Unit.Default).Subscribe();
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> DeployCommand { get; }
    public ReactiveCommand<Unit, Unit> BackCommand { get; }
    public ReactiveCommand<Unit, Unit> EditCommand { get; }

    private async Task LoadJobsAsync()
    {
        IsLoading = true;
        try
        {
            var jobs = await _client.GetProjectJobsAsync(Project.Id);
            Jobs.Clear();
            foreach (var j in jobs.OrderByDescending(j => j.EnqueuedAt))
                Jobs.Add(new JobViewModel(j, _client, _main, this));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task DeployAsync()
    {
        var job = await _client.EnqueueDeployAsync(Project.Id);
        Jobs.Insert(0, new JobViewModel(job, _client, _main, this));
        _main.NavigateTo(new JobDetailViewModel(job, _client, _main, this));
    }
}

public partial class JobViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;
    private readonly MainViewModel _main;
    private readonly ProjectDetailViewModel _projectDetail;

    public DeploymentJob Job { get; }

    public JobViewModel(DeploymentJob job, FleetApiClient client, MainViewModel main, ProjectDetailViewModel projectDetail)
    {
        Job = job;
        _client = client;
        _main = main;
        _projectDetail = projectDetail;

        OpenCommand = ReactiveCommand.Create(Open);
    }

    public string StatusBadge => Job.Status switch
    {
        JobStatus.Queued => "⏳ Queued",
        JobStatus.Assigned => "🔄 Assigned",
        JobStatus.Running => "🚀 Running",
        JobStatus.Succeeded => "✅ Succeeded",
        JobStatus.Failed => "❌ Failed",
        JobStatus.Cancelled => "🚫 Cancelled",
        _ => Job.Status.ToString()
    };

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> OpenCommand { get; }

    private void Open()
    {
        var vm = new JobDetailViewModel(Job, _client, _main, _projectDetail);
        _main.NavigateTo(vm);
    }
}
