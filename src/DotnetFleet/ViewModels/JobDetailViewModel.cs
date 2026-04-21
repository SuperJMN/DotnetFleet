using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using AvaloniaTerminal;
using DynamicData;
using DynamicData.Binding;
using DotnetFleet.Api.Client;
using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Logging;
using DotnetFleet.Views.Logging;
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
    [Reactive] private bool _canCancel;
    [Reactive] private LogSeverity _minSeverity = LogSeverity.None;
    [Reactive] private string _searchText = string.Empty;
    [Reactive] private string _searchResultText = string.Empty;

    public ReadOnlyObservableCollection<LogLine> FilteredLogs { get; }

    public TerminalControlModel? TerminalModel { get; private set; }

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
        _statusText = FormatStatus(job.Status);

        BackCommand = ReactiveCommand.Create(GoBack);
        CopyLogsCommand = ReactiveCommand.CreateFromTask(CopyLogsToClipboard);
        NextSearchResultCommand = ReactiveCommand.Create(NavigateNextSearchResult);
        CancelJobCommand = ReactiveCommand.CreateFromTask(CancelJobAsync,
            this.WhenAnyValue(x => x.CanCancel));

        var minSeverityChanges = this.WhenAnyValue(x => x.MinSeverity);

        _filterSubscription = _logs.Connect()
            .Filter(minSeverityChanges.Select(min => (Func<LogLine, bool>)(line => (int)line.Severity >= (int)min)))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Bind(out var filtered)
            .Subscribe();

        FilteredLogs = filtered;

        // Drive terminal search from SearchText changes
        this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .DistinctUntilChanged()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(OnSearchTextChanged);

        if (job.Status is JobStatus.Running or JobStatus.Queued or JobStatus.Assigned)
        {
            CanCancel = true;
            StartStreaming();
        }
        else
            LoadLogsAsync().ConfigureAwait(false);
    }

    public ReactiveCommand<Unit, Unit> BackCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyLogsCommand { get; }
    public ReactiveCommand<Unit, Unit> NextSearchResultCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelJobCommand { get; }

    public void SetTerminalModel(TerminalControlModel model) => TerminalModel = model;

    private void OnSearchTextChanged(string? text)
    {
        if (TerminalModel is null) return;

        if (string.IsNullOrEmpty(text))
        {
            SearchResultText = $"{FilteredLogs.Count} lines";
            return;
        }

        var count = TerminalModel.Search(text);
        SearchResultText = count > 0 ? $"{count} matches" : "No matches";
        if (count > 0)
            TerminalModel.SelectNextSearchResult();
    }

    private void NavigateNextSearchResult()
    {
        TerminalModel?.SelectNextSearchResult();
    }

    private async Task CopyLogsToClipboard()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        var clipboard = desktop.MainWindow?.Clipboard;
        if (clipboard is null) return;

        var text = string.Join(Environment.NewLine, FilteredLogs.Select(l => l.Text));
        await clipboard.SetTextAsync(text);
    }

    private async Task CancelJobAsync()
    {
        try
        {
            await _client.CancelJobAsync(Job.Id);
            CanCancel = false;
            StatusText = JobStatus.Cancelled.ToString();
        }
        catch (Exception ex)
        {
            StatusText = $"Cancel failed: {ex.Message}";
        }
    }

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
        _ = StreamLogsAsync(_cts.Token).ContinueWith(_ =>
        {
            IsStreaming = false;
            CanCancel = false;
        });
    }

    private async Task StreamLogsAsync(CancellationToken ct)
    {
        const int maxBatchSize = 200;
        var buffer = new List<LogLine>(maxBatchSize);
        var lastFlush = Environment.TickCount64;

        await foreach (var evt in _client.StreamJobEventsAsync(Job.Id, ct))
        {
            if (evt.Type == FleetApiClient.SseEventType.Status)
            {
                if (Enum.TryParse<JobStatus>(evt.Data, out var status))
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusText = FormatStatus(status));
                continue;
            }

            buffer.Add(LogLine.FromText(evt.Data));

            var elapsed = Environment.TickCount64 - lastFlush;
            if (buffer.Count < maxBatchSize && elapsed < 100)
                continue;

            var batch = buffer.ToList();
            buffer.Clear();
            lastFlush = Environment.TickCount64;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _logs.AddRange(batch);
                FeedBatchToTerminal(batch);
            });
        }

        if (buffer.Count > 0)
        {
            var remaining = buffer.ToList();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _logs.AddRange(remaining);
                FeedBatchToTerminal(remaining);
            });
        }

        var updated = await _client.GetJobAsync(Job.Id);
        if (updated is not null)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusText = FormatStatus(updated.Status));
    }

    private static string FormatStatus(JobStatus status) => status switch
    {
        JobStatus.Queued => "⏳ Queued — Waiting for worker",
        JobStatus.Assigned => "🔄 Assigned",
        JobStatus.Running => "🚀 Running",
        JobStatus.Succeeded => "✅ Succeeded",
        JobStatus.Failed => "❌ Failed",
        JobStatus.Cancelled => "🚫 Cancelled",
        _ => status.ToString()
    };

    private void FeedBatchToTerminal(List<LogLine> batch)
    {
        if (TerminalModel is null) return;
        foreach (var line in batch)
            TerminalModel.Feed(LogAnsi.Format(line) + "\r\n");
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
