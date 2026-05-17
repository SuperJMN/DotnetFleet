using CSharpFunctionalExtensions;
using DotnetDeployer.Fleet.App.ViewModels;
using System.Reactive.Subjects;

namespace DotnetDeployer.Fleet.Tests;

public sealed class FleetClientPrerequisiteExtensionsTests
{
    [Fact]
    public async Task Bind_WhenPrerequisiteIsCancelled_ShouldReturnNoneWithoutExecutingSelector()
    {
        var executed = false;

        var result = await Task.FromResult(Maybe<Result<int>>.None)
            .Bind(_ =>
            {
                executed = true;
                return Task.FromResult("unused");
            });

        result.HasNoValue.Should().BeTrue();
        executed.Should().BeFalse();
    }

    [Fact]
    public async Task Bind_WhenPrerequisiteFails_ShouldPropagateFailure()
    {
        var result = await Task.FromResult(Maybe.From(Result.Failure<int>("Connection required")))
            .Bind(_ => Task.FromResult("unused"));

        result.Value.IsFailure.Should().BeTrue();
        result.Value.Error.Should().Be("Connection required");
    }

    [Fact]
    public void Results_ShouldIgnoreCancellationsAndExposeFailures()
    {
        using var source = new Subject<Maybe<Result<int>>>();
        var received = new List<Result<int>>();

        using var subscription = source.Results().Subscribe(received.Add);

        source.OnNext(Maybe<Result<int>>.None);
        source.OnNext(Result.Failure<int>("No endpoint"));
        source.OnNext(Result.Success(42));

        received.Should().HaveCount(2);
        received[0].Error.Should().Be("No endpoint");
        received[1].Value.Should().Be(42);
    }
}
