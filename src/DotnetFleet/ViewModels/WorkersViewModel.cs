using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using DotnetFleet.Api.Client;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Zafiro.UI.Navigation;
using Zafiro.UI.Shell.Utils;

namespace DotnetFleet.ViewModels;

[Section(name: "Workers", icon: "mdi-server", sortIndex: 1)]
public partial class WorkersViewModel : ReactiveObject, IHaveHeader
{
    private readonly FleetApiClient _client;

    [Reactive] private bool _isLoading;

    public ObservableCollection<WorkerItemViewModel> Workers { get; } = [];

    public WorkersViewModel(FleetApiClient client)
    {
        _client = client;
        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        RefreshCommand.ThrownExceptions.Subscribe(_ => { });

        Header = Observable.Return<object>(new SectionHeader("Workers",
            new HeaderAction("Refresh", "mdi-refresh", RefreshCommand)));

        _client.AuthenticatedChanges
            .Where(authenticated => authenticated)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => RefreshCommand.Execute(Unit.Default).Subscribe(_ => { }, _ => { }));
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public IObservable<object> Header { get; }

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

    public string CapabilityLabel
    {
        get
        {
            if (Worker.ProcessorCount <= 0 && Worker.TotalMemoryMb <= 0)
                return string.Empty;

            var cores = Worker.ProcessorCount > 0 ? $"{Worker.ProcessorCount} core(s)" : null;
            var memory = Worker.TotalMemoryMb > 0
                ? $"{Worker.TotalMemoryMb / 1024.0:F1} GB"
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
        }
        finally
        {
            IsSaving = false;
        }
    }
}
