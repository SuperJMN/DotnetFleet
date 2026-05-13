using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using AvaloniaTerminal;
using CSharpFunctionalExtensions;
using DynamicData;
using DynamicData.Binding;
using DotnetFleet.Api.Client;
using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Logging;
using DotnetFleet.Views.Logging;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Zafiro.DivineBytes;
using Zafiro.UI;
using Zafiro.UI.Navigation;
using IOPath = System.IO.Path;

namespace DotnetFleet.ViewModels;

public partial class JobDetailViewModel : ReactiveObject, IDisposable
{
    private readonly FleetApiClient _client;
    private readonly INavigator _navigator;
    private readonly IFileSystemPicker? _fileSystemPicker;
    private readonly ProjectDetailViewModel? _parentProject;
    private readonly SourceList<LogLine> _logs = new();
    private readonly JobPhaseTree _phaseTree = new(FormatPhaseName);
    private readonly IDisposable _filterSubscription;
    private readonly IDisposable _refreshSubscription;

    public DeploymentJob Job { get; private set; }

    [Reactive] private string _statusText = string.Empty;
    [Reactive] private bool _isStreaming;
    [Reactive] private bool _canCancel;
    [Reactive] private string? _errorMessage;
    [Reactive] private LogSeverity _minSeverity = LogSeverity.None;
    [Reactive] private string _searchText = string.Empty;
    [Reactive] private string _searchResultText = string.Empty;
    [Reactive] private string? _currentPhase;
    [Reactive] private DateTimeOffset? _currentPhaseStartedAt;
    [Reactive] private bool _isDetailedLogVisible;

    public ReadOnlyObservableCollection<JobPhaseRowContainer> Phases => _phaseTree.Phases;
    public ObservableCollection<PackageArtifactViewModel> Artifacts { get; } = new();

    public bool HasPhases => Phases.Count > 0;
    public bool HasArtifacts => Artifacts.Count > 0;
    public bool HasCurrentPhase => !string.IsNullOrEmpty(_currentPhase);
    public string CurrentPhaseDisplay => string.IsNullOrEmpty(_currentPhase)
        ? string.Empty
        : FormatPhaseName(_currentPhase!);
    public string DetailedLogButtonText => IsDetailedLogVisible ? "Hide detailed log" : "Detailed log";

    public bool HasError => !string.IsNullOrWhiteSpace(_errorMessage);

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

    public JobDetailViewModel(
        DeploymentJob job,
        FleetApiClient client,
        INavigator navigator,
        ProjectDetailViewModel? parentProject = null,
        IFileSystemPicker? fileSystemPicker = null)
    {
        Job = job;
        _client = client;
        _navigator = navigator;
        _fileSystemPicker = fileSystemPicker;
        _parentProject = parentProject;
        _statusText = FormatStatus(job.Status);
        _errorMessage = job.ErrorMessage;

        CopyLogsCommand = ReactiveCommand.CreateFromTask(CopyLogsToClipboard);
        RefreshArtifactsCommand = ReactiveCommand.CreateFromTask(() => RefreshArtifactsAsync(default));
        NextSearchResultCommand = ReactiveCommand.Create(NavigateNextSearchResult);
        ToggleDetailedLogCommand = ReactiveCommand.Create(ToggleDetailedLog);
        CancelJobCommand = ReactiveCommand.CreateFromTask(CancelJobAsync,
            this.WhenAnyValue(x => x.CanCancel));
        RefreshArtifactsCommand.ThrownExceptions.Subscribe(ex => ErrorMessage = ex.Message);
        var refreshSnapshot = ReactiveCommand.CreateFromTask(() => RefreshSnapshotAsync(default));
        refreshSnapshot.ThrownExceptions.Subscribe(_ => { });
        _refreshSubscription = AutoRefresh.Start(Observable.Return(true), refreshSnapshot, AutoRefreshIntervals.Detail);

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
        {
            LoadLogsAsync().ConfigureAwait(false);
        }
    }

    private async Task RefreshSnapshotAsync(CancellationToken ct)
    {
        try
        {
            var phasesTask = _client.GetJobPhasesAsync(Job.Id);
            var jobTask = _client.GetJobAsync(Job.Id);
            var artifactsTask = Job.Kind == JobKind.PackageBuild
                ? _client.GetJobArtifactsAsync(Job.Id)
                : Task.FromResult<List<PackageArtifact>>([]);

            await Task.WhenAll(phasesTask, jobTask, artifactsTask);

            var phases = await phasesTask;
            var job = await jobTask;
            var artifacts = await artifactsTask;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (job is not null)
                    ApplyJobSnapshot(job);

                _phaseTree.Refresh(phases);
                ApplyArtifacts(artifacts);
                this.RaisePropertyChanged(nameof(HasCurrentPhase));
                this.RaisePropertyChanged(nameof(HasPhases));
                this.RaisePropertyChanged(nameof(CurrentPhaseDisplay));
            });
        }
        catch { /* ignore transient fetch errors */ }
    }

    /// <summary>
    /// Maps a phase id (e.g. <c>package.generate.deb.x64</c>) to a human label
    /// (e.g. "Package · deb · x64"). Keeps the UI readable without forcing
    /// the backend to localise.
    /// </summary>
    private static string FormatPhaseName(string name)
    {
        return name switch
        {
            "worker.git.clone" => "Cloning repository",
            "worker.deployer.invoke" => "Running DotnetDeployer",
            "worker.artifacts.upload" => "Uploading package artifacts",
            "version.resolve" => "Resolving version",
            "workload.restore" => "Restoring workloads",
            "android.prereqs" => "Installing Android prerequisites",
            "nuget.deploy" => "Publishing to NuGet",
            "nuget.pack" => "Packing NuGet",
            "nuget.push" => "Pushing NuGet",
            "github.deploy" => "Publishing to GitHub Releases",
            "github.pages.deploy" => "Publishing to GitHub Pages",
            "github.release.create" => "Creating GitHub release",
            "github.release.upload" => "Uploading release asset",
            _ when name.StartsWith("package.generate.") =>
                "Packaging · " + name["package.generate.".Length..].Replace('.', ' '),
            _ => name
        };
    }

    public ReactiveCommand<Unit, Unit> CopyLogsCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshArtifactsCommand { get; }
    public ReactiveCommand<Unit, Unit> NextSearchResultCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleDetailedLogCommand { get; }
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

    private void ToggleDetailedLog()
    {
        IsDetailedLogVisible = !IsDetailedLogVisible;
        this.RaisePropertyChanged(nameof(DetailedLogButtonText));
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
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ApplyJobSnapshot(updated);
            });

        await RefreshArtifactsAsync(default);
    }

    private async Task RefreshArtifactsAsync(CancellationToken ct)
    {
        if (Job.Kind != JobKind.PackageBuild)
            return;

        var artifacts = await _client.GetJobArtifactsAsync(Job.Id);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyArtifacts(artifacts));
    }

    private void ApplyJobSnapshot(DeploymentJob updated)
    {
        if (updated.Id != Job.Id) return;

        Job = updated;
        StatusText = FormatStatus(updated.Status);
        ErrorMessage = updated.ErrorMessage;
        CurrentPhase = updated.CurrentPhase;
        CurrentPhaseStartedAt = updated.CurrentPhaseStartedAt;
        CanCancel = updated.Status is JobStatus.Running or JobStatus.Queued or JobStatus.Assigned
                    && updated.CancellationRequestedAt is null;

        this.RaisePropertyChanged(nameof(Job));
        this.RaisePropertyChanged(nameof(HasError));
        this.RaisePropertyChanged(nameof(HasCurrentPhase));
        this.RaisePropertyChanged(nameof(CurrentPhaseDisplay));
    }

    private void ApplyArtifacts(IReadOnlyList<PackageArtifact> artifacts)
    {
        if (Job.Kind != JobKind.PackageBuild)
            return;

        ObservableCollectionSync.Sync(
            Artifacts,
            artifacts,
            artifact => artifact.RelativePath,
            viewModel => viewModel.Artifact.RelativePath,
            artifact => new PackageArtifactViewModel(artifact, this),
            (viewModel, artifact) => viewModel.ApplyArtifactUpdate(artifact));

        this.RaisePropertyChanged(nameof(HasArtifacts));
    }

    private async Task SaveArtifactAsync(PackageArtifact artifact)
    {
        if (_fileSystemPicker is null)
        {
            ErrorMessage = "File saving is not available in this host.";
            this.RaisePropertyChanged(nameof(HasError));
            return;
        }

        var destination = await _fileSystemPicker.PickForSave(
            artifact.FileName,
            Maybe.From(DefaultExtension(artifact.FileName)),
            FileTypeFor(artifact.FileName),
            new FileTypeFilter("All files", ["*"]));

        if (destination.HasNoValue)
            return;

        var source = ByteSource.FromAsyncStreamFactory(() =>
            _client.OpenJobArtifactStreamAsync(Job.Id, artifact.RelativePath));

        var result = await destination.Value.SetContents(source);
        if (result.IsFailure)
        {
            ErrorMessage = result.Error;
            this.RaisePropertyChanged(nameof(HasError));
        }
    }

    private static string? DefaultExtension(string fileName)
    {
        var extension = IOPath.GetExtension(fileName);
        return string.IsNullOrWhiteSpace(extension) ? null : extension.TrimStart('.');
    }

    private static FileTypeFilter FileTypeFor(string fileName)
    {
        var extension = IOPath.GetExtension(fileName);
        return string.IsNullOrWhiteSpace(extension)
            ? new FileTypeFilter("Package files", ["*"])
            : new FileTypeFilter($"{extension.TrimStart('.').ToUpperInvariant()} files (*{extension})", [$"*{extension}"]);
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

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _refreshSubscription.Dispose();
        _filterSubscription.Dispose();
        _phaseTree.Dispose();
        _logs.Dispose();
    }

    public sealed class PackageArtifactViewModel : ReactiveObject
    {
        private readonly JobDetailViewModel parent;

        public PackageArtifactViewModel(PackageArtifact artifact, JobDetailViewModel parent)
        {
            Artifact = artifact;
            this.parent = parent;
            SaveCommand = ReactiveCommand.CreateFromTask(() => this.parent.SaveArtifactAsync(Artifact));
        }

        public PackageArtifact Artifact { get; private set; }
        public string FileName => Artifact.FileName;
        public string SizeText => FormatSize(Artifact.SizeBytes);
        public string Sha256Text => string.IsNullOrWhiteSpace(Artifact.Sha256)
            ? ""
            : $"SHA-256 {Artifact.Sha256[..Math.Min(12, Artifact.Sha256.Length)]}";
        public ReactiveCommand<Unit, Unit> SaveCommand { get; }

        public void ApplyArtifactUpdate(PackageArtifact updated)
        {
            Artifact = updated;
            this.RaisePropertyChanged(nameof(Artifact));
            this.RaisePropertyChanged(nameof(FileName));
            this.RaisePropertyChanged(nameof(SizeText));
            this.RaisePropertyChanged(nameof(Sha256Text));
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024 * 1024)
                return $"{bytes / 1024d / 1024d:0.0} MB";
            if (bytes >= 1024)
                return $"{bytes / 1024d:0.0} KB";
            return $"{bytes} B";
        }
    }
}
