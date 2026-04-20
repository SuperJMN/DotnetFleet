using System.Collections.ObjectModel;
using System.Reactive;
using DotnetFleet.Api.Client;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace DotnetFleet.ViewModels;

public partial class WorkersViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;

    [Reactive] private bool _isLoading;

    public ObservableCollection<WorkerItemViewModel> Workers { get; } = [];

    public WorkersViewModel(FleetApiClient client)
    {
        _client = client;
        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        RefreshCommand.ThrownExceptions.Subscribe(_ => { });
        RefreshCommand.Execute(Unit.Default).Subscribe();
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var workers = await _client.GetWorkersAsync();
            Workers.Clear();
            foreach (var w in workers)
                Workers.Add(new WorkerItemViewModel(w, _client));
        }
        finally
        {
            IsLoading = false;
        }
    }
}

public partial class WorkerItemViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;

    public FleetApiClient.WorkerInfo Worker { get; }

    [Reactive] private double _maxDiskUsageGb;
    [Reactive] private bool _isSaving;

    public WorkerItemViewModel(FleetApiClient.WorkerInfo worker, FleetApiClient client)
    {
        Worker = worker;
        _client = client;
        _maxDiskUsageGb = worker.MaxDiskUsageGb;

        SaveConfigCommand = ReactiveCommand.CreateFromTask(SaveConfigAsync);
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> SaveConfigCommand { get; }

    public string StatusLabel => Worker.Status switch
    {
        Core.Domain.WorkerStatus.Online => "🟢 Online",
        Core.Domain.WorkerStatus.Busy => "🔵 Busy",
        Core.Domain.WorkerStatus.Offline => "🔴 Offline",
        _ => Worker.Status.ToString()
    };

    public string EmbeddedLabel => Worker.IsEmbedded ? "(embedded)" : string.Empty;

    private async Task SaveConfigAsync()
    {
        IsSaving = true;
        try
        {
            await _client.UpdateWorkerConfigAsync(Worker.Id, MaxDiskUsageGb);
        }
        finally
        {
            IsSaving = false;
        }
    }
}
