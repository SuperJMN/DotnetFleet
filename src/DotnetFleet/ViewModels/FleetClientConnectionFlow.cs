using Avalonia.Threading;
using CSharpFunctionalExtensions;
using DotnetFleet.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using Zafiro.Avalonia.Dialogs;
using Zafiro.UI.Commands;

namespace DotnetFleet.ViewModels;

public sealed class FleetClientConnectionFlow(IServiceProvider services, IDialog dialog) : IFleetClientConnectionFlow
{
    public Task<Maybe<Result>> Connect()
    {
        var viewModel = services.GetRequiredService<ConnectDialogViewModel>();
        return Show(viewModel, "Coordinator endpoint", "Connect", x => x.TryConnect(), x => x.CanConnect);
    }

    public Task<Maybe<Result>> Login()
    {
        var viewModel = services.GetRequiredService<LoginDialogViewModel>();
        return Show(viewModel, "Sign in", "Sign in", x => x.TryLogin(), x => x.CanLogin);
    }

    private async Task<Maybe<Result>> Show<TViewModel>(
        TViewModel viewModel,
        string title,
        string actionText,
        Func<TViewModel, Task<Result>> action,
        Func<TViewModel, IObservable<bool>> canExecute)
        where TViewModel : class
    {
        var captured = Maybe<Result>.None;
        var completed = await dialog.Show<TViewModel>(viewModel, title, (vm, closeable) =>
        {
            var command = ReactiveCommand.CreateFromTask(async () =>
            {
                captured = Maybe.From(await action(vm));
                closeable.Close();
            }, canExecute(vm)).Enhance();

            return new IOption[]
            {
                new Option("Cancel",
                    ReactiveCommand.Create(closeable.Dismiss).Enhance(),
                    new Settings { IsCancel = true, Role = OptionRole.Cancel }),
                new Option(actionText,
                    command,
                    new Settings { IsDefault = true, Role = OptionRole.Primary }),
            };
        });

        if (!completed)
            return Maybe<Result>.None;

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        return captured.HasValue
            ? captured
            : Maybe.From(Result.Failure($"{title} did not complete."));
    }
}
