using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Interfaces;
using DotnetFleet.WorkerService;
using FluentAssertions;
using NSubstitute;

namespace DotnetFleet.Tests;

public class LocalWorkerJobSourceTests
{
    private readonly IFleetStorage _storage = Substitute.For<IFleetStorage>();
    private readonly LocalWorkerJobSource _sut;

    public LocalWorkerJobSourceTests() => _sut = new LocalWorkerJobSource(_storage);

    [Fact]
    public async Task GetNextJob_delegates_to_storage_DequeueNextJob()
    {
        var workerId = Guid.NewGuid();
        var expected = new DeploymentJob { Id = Guid.NewGuid() };
        _storage.DequeueNextJobAsync(default).ReturnsForAnyArgs(expected);

        var result = await _sut.GetNextJobAsync(workerId);

        result.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task ReportJobStarted_sets_Running_status_and_workerId()
    {
        var jobId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var job = new DeploymentJob { Id = jobId, Status = JobStatus.Queued };

        _storage.GetJobAsync(jobId).ReturnsForAnyArgs(job);

        await _sut.ReportJobStartedAsync(jobId, workerId);

        job.Status.Should().Be(JobStatus.Running);
        job.WorkerId.Should().Be(workerId);
        job.StartedAt.Should().NotBeNull();
        await _storage.ReceivedWithAnyArgs(1).UpdateJobAsync(default!);
    }

    [Fact]
    public async Task ReportJobCompleted_success_sets_Succeeded_status()
    {
        var jobId = Guid.NewGuid();
        var job = new DeploymentJob { Id = jobId, Status = JobStatus.Running };
        _storage.GetJobAsync(jobId).ReturnsForAnyArgs(job);

        await _sut.ReportJobCompletedAsync(jobId, success: true, errorMessage: null);

        job.Status.Should().Be(JobStatus.Succeeded);
        job.FinishedAt.Should().NotBeNull();
        job.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task ReportJobCompleted_failure_sets_Failed_status_and_message()
    {
        var jobId = Guid.NewGuid();
        var job = new DeploymentJob { Id = jobId, Status = JobStatus.Running };
        _storage.GetJobAsync(jobId).ReturnsForAnyArgs(job);

        await _sut.ReportJobCompletedAsync(jobId, success: false, errorMessage: "exit code 1");

        job.Status.Should().Be(JobStatus.Failed);
        job.ErrorMessage.Should().Be("exit code 1");
    }

    [Fact]
    public async Task SendLogChunk_persists_all_lines()
    {
        var jobId = Guid.NewGuid();
        var lines = new[] { "line1", "line2", "line3" };

        await _sut.SendLogChunkAsync(jobId, lines);

        await _storage.ReceivedWithAnyArgs(1).AddLogEntriesAsync(
            Arg.Is<IEnumerable<LogEntry>>(entries => entries.Count() == 3));
    }

    [Fact]
    public async Task ReportJobStarted_noop_when_job_not_found()
    {
        _storage.GetJobAsync(default).ReturnsForAnyArgs((DeploymentJob?)null);

        var act = () => _sut.ReportJobStartedAsync(Guid.NewGuid(), Guid.NewGuid());

        await act.Should().NotThrowAsync();
        await _storage.DidNotReceiveWithAnyArgs().UpdateJobAsync(default!);
    }
}
