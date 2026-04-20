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
    [Reactive] private string _searchText = string.Empty;
    [Reactive] private LogSeverity _minSeverity = LogSeverity.None;
    [Reactive] private int _matchCount;
    [Reactive] private int _currentMatchIndex;

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
        ClearSearchCommand = ReactiveCommand.Create(() => SearchText = string.Empty);

        var canNavigate = this.WhenAnyValue(x => x.MatchCount).Select(c => c > 0);
        NextMatchCommand = ReactiveCommand.Create(MoveToNextMatch, canNavigate);
        PrevMatchCommand = ReactiveCommand.Create(MoveToPrevMatch, canNavigate);

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
    public ReactiveCommand<Unit, string> ClearSearchCommand { get; }
    public ReactiveCommand<Unit, Unit> NextMatchCommand { get; }
    public ReactiveCommand<Unit, Unit> PrevMatchCommand { get; }

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
        await foreach (var line in _client.StreamJobLogsAsync(Job.Id, ct))
        {
            var entry = LogLine.FromText(line);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _logs.Add(entry));
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

    private void MoveToNextMatch()
    {
        if (MatchCount <= 0) return;
        CurrentMatchIndex = (CurrentMatchIndex + 1) % MatchCount;
    }

    private void MoveToPrevMatch()
    {
        if (MatchCount <= 0) return;
        CurrentMatchIndex = (CurrentMatchIndex - 1 + MatchCount) % MatchCount;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _filterSubscription.Dispose();
        _logs.Dispose();
    }
}
