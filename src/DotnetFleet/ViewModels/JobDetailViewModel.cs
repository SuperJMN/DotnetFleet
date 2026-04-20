using System.Collections.ObjectModel;
using System.Reactive;
using DotnetFleet.Api.Client;
using DotnetFleet.Core.Domain;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace DotnetFleet.ViewModels;

public partial class JobDetailViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;
    private readonly MainViewModel _main;
    private readonly ProjectDetailViewModel? _parentProject;

    public DeploymentJob Job { get; }

    [Reactive] private string _statusText = string.Empty;
    [Reactive] private bool _isStreaming;

    public ObservableCollection<string> Logs { get; } = [];

    private CancellationTokenSource? _cts;

    public JobDetailViewModel(DeploymentJob job, FleetApiClient client, MainViewModel main, ProjectDetailViewModel? parentProject = null)
    {
        Job = job;
        _client = client;
        _main = main;
        _parentProject = parentProject;
        _statusText = job.Status.ToString();

        BackCommand = ReactiveCommand.Create(GoBack);

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
            // Load completed job logs via streaming (will end naturally)
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
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Logs.Add(line));
        }

        // Refresh job status once streaming completes
        var updated = await _client.GetJobAsync(Job.Id);
        if (updated is not null)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusText = updated.Status.ToString());
    }

    private void GoBack()
    {
        _cts?.Cancel();
        _main.NavigateTo((object?)_parentProject ?? _main.Projects);
    }
}
