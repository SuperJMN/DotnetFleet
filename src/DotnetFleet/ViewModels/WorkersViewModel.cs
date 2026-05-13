using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DotnetFleet.Api.Client;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Zafiro.UI.Navigation;
using Zafiro.UI.Shell.Utils;

namespace DotnetFleet.ViewModels;

[Section(name: "Workers", icon: "mdi-server", sortIndex: 2)]
public partial class WorkersViewModel : ReactiveObject, IHaveHeader, IDisposable
{
    private readonly FleetApiClient _client;
    private readonly CompositeDisposable _disposables = [];

    [Reactive] private bool _isLoading;

    public ObservableCollection<WorkerItemViewModel> Workers { get; } = [];

    public WorkersViewModel(FleetApiClient client)
    {
        _client = client;
        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        _disposables.Add(RefreshCommand.ThrownExceptions.Subscribe(_ => { }));

        Header = Observable.Return<object>(new SectionHeader("Workers",
            new HeaderAction("Refresh", "mdi-refresh", RefreshCommand)));

        _disposables.Add(AutoRefresh.Start(_client.AuthenticatedChanges, RefreshCommand, AutoRefreshIntervals.Section));
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public IObservable<object> Header { get; }
    public void Dispose() => _disposables.Dispose();

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var workers = await _client.GetWorkersAsync();
            ObservableCollectionSync.Sync(
                Workers,
                workers,
                worker => worker.Id,
                viewModel => viewModel.Worker.Id,
                worker => new WorkerItemViewModel(worker, _client),
                (viewModel, worker) => viewModel.ApplyWorkerUpdate(worker));
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

    public FleetApiClient.WorkerInfo Worker { get; private set; }

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

    public void ApplyWorkerUpdate(FleetApiClient.WorkerInfo updated)
    {
        if (updated.Id != Worker.Id) return;

        var canSyncDiskValue = !IsSaving && Math.Abs(MaxDiskUsageGb - Worker.MaxDiskUsageGb) < 0.001;
        Worker = updated;
        if (canSyncDiskValue)
            MaxDiskUsageGb = updated.MaxDiskUsageGb;

        this.RaisePropertyChanged(nameof(Worker));
        this.RaisePropertyChanged(nameof(StatusLabel));
        this.RaisePropertyChanged(nameof(EmbeddedLabel));
        this.RaisePropertyChanged(nameof(CapabilityLabel));
    }

    public string StatusLabel => Worker.Status switch
    {
        Core.Domain.WorkerStatus.Online => "🟢 Online",
        Core.Domain.WorkerStatus.Busy => "🔵 Busy",
        Core.Domain.WorkerStatus.Offline => "🔴 Offline",
        _ => Worker.Status.ToString()
    };

    public string EmbeddedLabel => Worker.IsEmbedded ? "(embedded)" : string.Empty;

    public string CapabilityLabel
    {
        get
        {
            if (Worker.ProcessorCount <= 0 && Worker.TotalMemoryMb <= 0)
                return string.Empty;

            var cores = Worker.ProcessorCount > 0 ? $"{Worker.ProcessorCount} core(s)" : null;
            var memory = Worker.TotalMemoryMb > 0
                ? $"{(Worker.TotalMemoryMb / 1024.0).ToString("F1", CultureInfo.InvariantCulture)} GB"
                : null;
            var arch = !string.IsNullOrWhiteSpace(Worker.Architecture)
                ? Worker.Architecture
                : null;

            return string.Join(" · ", new[] { cores, memory, arch }.Where(s => s is not null));
        }
    }

    private async Task SaveConfigAsync()
    {
        IsSaving = true;
        try
        {
            await _client.UpdateWorkerConfigAsync(Worker.Id, MaxDiskUsageGb);
            Worker = Worker with { MaxDiskUsageGb = MaxDiskUsageGb };
            this.RaisePropertyChanged(nameof(Worker));
        }
        finally
        {
            IsSaving = false;
        }
    }
}
