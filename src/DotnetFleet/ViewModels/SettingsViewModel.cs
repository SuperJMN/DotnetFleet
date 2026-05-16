using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using DotnetFleet.Api.Client;
using ReactiveUI;
using Zafiro.UI;
using Zafiro.UI.Navigation;
using Zafiro.UI.Shell.Utils;

namespace DotnetFleet.ViewModels;

[Section(name: "Settings", icon: "mdi-cog-outline", sortIndex: 4)]
public sealed class SettingsViewModel : ReactiveObject, IHaveHeader, IDisposable
{
    private readonly CompositeDisposable disposables = [];

    public SettingsViewModel(AppBootstrapper bootstrapper, INotificationService notificationService)
    {
        LogoutCommand = ReactiveCommand.CreateFromTask(bootstrapper.Logout);
        disposables.Add(LogoutCommand.Results().HandleErrorsWith(notificationService, Maybe.From("Sign in failed")));

        Header = Observable.Return<object>(new SectionHeader("Settings"));
    }

    public ReactiveCommand<Unit, Maybe<Result<FleetApiClient>>> LogoutCommand { get; }
    public IObservable<object> Header { get; }

    public void Dispose() => disposables.Dispose();
}
