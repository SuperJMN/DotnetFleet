using System.Reactive.Linq;
using CSharpFunctionalExtensions;

namespace DotnetDeployer.Fleet.App.ViewModels;

public static class FleetClientPrerequisiteExtensions
{
    public static async Task<Maybe<Result<TOutput>>> Bind<TInput, TOutput>(
        this Task<Maybe<Result<TInput>>> source,
        Func<TInput, Task<TOutput>> selector)
    {
        var maybe = await source;
        if (maybe.HasNoValue)
            return Maybe<Result<TOutput>>.None;

        var result = maybe.Value;
        if (result.IsFailure)
            return Maybe.From(Result.Failure<TOutput>(result.Error));

        return Maybe.From(await Result.Try(() => selector(result.Value)));
    }

    public static async Task<Maybe<Result>> Bind<TInput>(
        this Task<Maybe<Result<TInput>>> source,
        Func<TInput, Task> selector)
    {
        var maybe = await source;
        if (maybe.HasNoValue)
            return Maybe<Result>.None;

        var result = maybe.Value;
        if (result.IsFailure)
            return Maybe.From(Result.Failure(result.Error));

        return Maybe.From(await Result.Try(() => selector(result.Value)));
    }

    public static IObservable<Result<T>> Results<T>(this IObservable<Maybe<Result<T>>> source)
    {
        return source.SelectMany(maybe => maybe.HasValue
            ? Observable.Return(maybe.Value)
            : Observable.Empty<Result<T>>());
    }

    public static IObservable<Result> Results(this IObservable<Maybe<Result>> source)
    {
        return source.SelectMany(maybe => maybe.HasValue
            ? Observable.Return(maybe.Value)
            : Observable.Empty<Result>());
    }
}
