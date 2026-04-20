using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Binding;
using DotnetFleet.Api.Client;
using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Logging;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace DotnetFleet.ViewModels;

public partial class JobDetailViewModel : ReactiveObject, IDisposable
{
    private readonly FleetApiClient _client;
    private readonly MainViewModel _main;
    private readonly ProjectDetailViewModel? _parentProject;
    private readonly SourceList<LogLine> _logs = new();
    private readonly IDisposable _filterSubscription;

    public DeploymentJob Job { get; }

    [Reactive] private string _statusText = string.Empty;
    [Reactive] private bool _isStreaming;
    [Reactive] private LogSeverity _minSeverity = LogSeverity.None;

    public ReadOnlyObservableCollection<LogLine> FilteredLogs { get; }

    public IReadOnlyList<LogSeverity> AvailableSeverities { get; } = new[]
    {
        LogSeverity.None,
        LogSeverity.Info,
        LogSeverity.Warning,
        LogSeverity.Error
    };

    private CancellationTokenSource? _cts;

    public JobDetailViewModel(DeploymentJob job, FleetApiClient client, MainViewModel main, ProjectDetailViewModel? parentProject = null)
    {
        Job = job;
        _client = client;
        _main = main;
        _parentProject = parentProject;
        _statusText = job.Status.ToString();

        BackCommand = ReactiveCommand.Create(GoBack);

        var minSeverityChanges = this.WhenAnyValue(x => x.MinSeverity);

        _filterSubscription = _logs.Connect()
            .Filter(minSeverityChanges.Select(min => (Func<LogLine, bool>)(line => (int)line.Severity >= (int)min)))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Bind(out var filtered)
            .Subscribe();

        FilteredLogs = filtered;

        if (job.Status is JobStatus.Running or JobStatus.Queued or JobStatus.Assigned)
            StartStreaming();
        else
            LoadLogsAsync().ConfigureAwait(false);
    }

    public ReactiveCommand<Unit, Unit> BackCommand { get; }

    private async Task LoadLogsAsync()
    {
        try
        {
            await StreamLogsAsync(new CancellationToken());
        }
        catch { /* ignore */ }
    }

    private void StartStreaming()
    {
        _cts = new CancellationTokenSource();
        IsStreaming = true;
        _ = StreamLogsAsync(_cts.Token).ContinueWith(_ => IsStreaming = false);
    }

    private async Task StreamLogsAsync(CancellationToken ct)
    {
        const int maxBatchSize = 200;
        var buffer = new List<LogLine>(maxBatchSize);
        var lastFlush = Environment.TickCount64;

        await foreach (var line in _client.StreamJobLogsAsync(Job.Id, ct))
        {
            buffer.Add(LogLine.FromText(line));

            var elapsed = Environment.TickCount64 - lastFlush;
            if (buffer.Count < maxBatchSize && elapsed < 100)
                continue;

            var batch = buffer.ToList();
            buffer.Clear();
            lastFlush = Environment.TickCount64;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _logs.AddRange(batch));
        }

        if (buffer.Count > 0)
        {
            var remaining = buffer.ToList();
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _logs.AddRange(remaining));
        }

        var updated = await _client.GetJobAsync(Job.Id);
        if (updated is not null)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusText = updated.Status.ToString());
    }

    private void GoBack()
    {
        _cts?.Cancel();
        _main.NavigateTo((object?)_parentProject ?? _main.Projects);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _filterSubscription.Dispose();
        _logs.Dispose();
    }
}
