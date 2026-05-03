using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media;
using DotnetFleet.Api.Client;
using DotnetFleet.Core.Domain;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Zafiro.Avalonia.Dialogs;
using Zafiro.UI;
using Zafiro.UI.Commands;
using Zafiro.UI.Navigation;
using DialogOption = Zafiro.Avalonia.Dialogs.Option;

namespace DotnetFleet.ViewModels;

public partial class ProjectDetailViewModel : ReactiveObject, IHaveHeader
{
    private readonly FleetApiClient _client;
    private readonly INavigator _navigator;
    private readonly IFileSystemPicker _fileSystemPicker;
    private readonly IDialog _dialog;

    public Project Project { get; }
    public ProjectSecretsViewModel ProjectSecrets { get; }
    public PackageBuildOptionsViewModel BuildOptions { get; }

    [Reactive] private bool _isLoading;
    [Reactive] private string? _error;

    public ObservableCollection<JobViewModel> Jobs { get; } = [];

    public ProjectDetailViewModel(Project project, FleetApiClient client, INavigator navigator, IFileSystemPicker fileSystemPicker, IDialog dialog)
    {
        Project = project;
        _client = client;
        _navigator = navigator;
        _fileSystemPicker = fileSystemPicker;
        _dialog = dialog;

        ProjectSecrets = new ProjectSecretsViewModel(project.Id, client);
        BuildOptions = new PackageBuildOptionsViewModel(project.Id, client);

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadJobsAsync);
        DeployCommand = ReactiveCommand.CreateFromTask(DeployAsync);
        BuildPackagesCommand = ReactiveCommand.CreateFromTask(ShowBuildPackagesDialogAsync);
        ClearFinishedJobsCommand = ReactiveCommand.CreateFromTask(ClearFinishedJobsAsync);
        EditCommand = ReactiveCommand.Create(() =>
        {
            _navigator.Go(() => new EditProjectViewModel(Project, _client, _navigator, projectsForRefresh: null));
        });

        RefreshCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message);
        DeployCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message);
        BuildPackagesCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message);
        ClearFinishedJobsCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message);
        RefreshCommand.Execute(Unit.Default).Subscribe();

        Header = Observable.Return<object>(new SectionHeader(
            Project.Name,
            $"{Project.GitUrl} @ {Project.Branch}",
            new HeaderAction("Queue Deploy", "mdi-rocket-launch", DeployCommand, isPrimary: true),
            new HeaderAction("Queue Build", "mdi-package-variant-closed", BuildPackagesCommand, isPrimary: true),
            new HeaderAction("Clear Builds", "mdi-broom", ClearFinishedJobsCommand),
            new HeaderAction("Refresh", "mdi-refresh", RefreshCommand),
            new HeaderAction("Project Secrets", "mdi-dots-horizontal", ProjectSecrets),
            new HeaderAction("Edit", "mdi-pencil", EditCommand)));
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> DeployCommand { get; }
    public ReactiveCommand<Unit, Unit> BuildPackagesCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearFinishedJobsCommand { get; }
    public ReactiveCommand<Unit, Unit> EditCommand { get; }
    public IObservable<object> Header { get; }

    private async Task LoadJobsAsync()
    {
        IsLoading = true;
        try
        {
            var jobs = await _client.GetProjectJobsAsync(Project.Id);
            Jobs.Clear();
            foreach (var j in jobs.OrderByDescending(j => j.EnqueuedAt))
                Jobs.Add(new JobViewModel(j, _client, _navigator, this, _fileSystemPicker));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task DeployAsync()
    {
        Error = null;
        var job = await _client.EnqueueDeployAsync(Project.Id);
        Jobs.Insert(0, new JobViewModel(job, _client, _navigator, this, _fileSystemPicker));
    }

    private async Task ShowBuildPackagesDialogAsync()
    {
        await BuildOptions.LoadPackageProjects();

        ICloseable? closeDialog = null;
        var queueCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var request = BuildOptions.CreateRequest();
            if (request is null) return;

            await QueuePackageBuildAsync(request);
            closeDialog?.Close();
        });
        queueCommand.ThrownExceptions.Subscribe(ex => BuildOptions.Error = ex.Message);

        await _dialog.Show(BuildOptions, "Queue build", (_, closeable) =>
        {
            closeDialog = closeable;
            return new IOption[]
            {
                new DialogOption("Cancel",
                    ReactiveCommand.Create(closeable.Dismiss).Enhance(),
                    new Settings { IsCancel = true, Role = OptionRole.Cancel }),
                new DialogOption("Queue Build",
                    queueCommand.Enhance(),
                    new Settings { IsDefault = true, Role = OptionRole.Primary }),
            };
        }, size: DialogSize.Wide);
    }

    private async Task QueuePackageBuildAsync(PackageBuildRequest request)
    {
        Error = null;
        var job = await _client.EnqueuePackageBuildAsync(Project.Id, request);
        Jobs.Insert(0, new JobViewModel(job, _client, _navigator, this, _fileSystemPicker));
    }

    private async Task ClearFinishedJobsAsync()
    {
        await _client.DeleteFinishedProjectJobsAsync(Project.Id);
        await LoadJobsAsync();
    }
}

public partial class PackageBuildOptionsViewModel : ReactiveObject
{
    private readonly Guid _projectId;
    private readonly FleetApiClient _client;

    [Reactive] private bool _isLoading;
    [Reactive] private string? _error;
    [Reactive] private string _packageProject = "";

    public PackageBuildOptionsViewModel(Guid projectId, FleetApiClient client)
    {
        _projectId = projectId;
        _client = client;

        foreach (var platform in PackagePlatformViewModel.CreateDefaults())
            PackagePlatforms.Add(platform);

        foreach (var target in PackagePlatforms.SelectMany(platform => platform.Targets))
        {
            target.WhenAnyValue(x => x.IsSelected)
                .Subscribe(_ =>
                {
                    Error = null;
                    this.RaisePropertyChanged(nameof(SelectedPackageTargetCount));
                });
        }
    }

    public ObservableCollection<PackagePlatformViewModel> PackagePlatforms { get; } = [];
    public ObservableCollection<string> PackageProjects { get; } = [];
    public bool HasPackageProjects => PackageProjects.Count > 0;
    public int SelectedPackageTargetCount => PackagePlatforms.Sum(p => p.Targets.Count(t => t.IsSelected));

    public async Task LoadPackageProjects()
    {
        IsLoading = true;
        try
        {
            var projects = await _client.GetPackageProjectsAsync(_projectId);
            PackageProjects.Clear();
            foreach (var project in projects)
                PackageProjects.Add(project);

            if (PackageProjects.Count == 1 && string.IsNullOrWhiteSpace(PackageProject))
                PackageProject = PackageProjects[0];

            this.RaisePropertyChanged(nameof(HasPackageProjects));
        }
        catch
        {
            // Package project discovery is advisory; users can still type the project manually.
        }
        finally
        {
            IsLoading = false;
        }
    }

    public PackageBuildRequest? CreateRequest()
    {
        Error = null;
        if (PackageProjects.Count > 1 && string.IsNullOrWhiteSpace(PackageProject))
        {
            Error = "Select the package project from deployer.yaml.";
            return null;
        }

        var targets = PackagePlatforms
            .SelectMany(p => p.Targets)
            .Where(t => t.IsSelected)
            .Select(t => new PackageBuildTarget
            {
                Format = t.Format,
                Architecture = t.Architecture
            })
            .ToList();

        if (targets.Count == 0)
        {
            Error = "Select at least one package target.";
            return null;
        }

        return new PackageBuildRequest
        {
            PackageProject = string.IsNullOrWhiteSpace(PackageProject) ? null : PackageProject.Trim(),
            Targets = targets
        };
    }
}

public partial class PackagePlatformViewModel : ReactiveObject
{
    public PackagePlatformViewModel(string name, IEnumerable<PackageFormatViewModel> formats)
    {
        Name = name;
        Formats = new ObservableCollection<PackageFormatViewModel>(formats);
    }

    public string Name { get; }
    public ObservableCollection<PackageFormatViewModel> Formats { get; }
    public IEnumerable<PackageTargetOptionViewModel> Targets => Formats.SelectMany(format => format.Architectures);

    public static IEnumerable<PackagePlatformViewModel> CreateDefaults()
    {
        yield return new PackagePlatformViewModel("Windows",
        [
            new("Setup (.exe)", "exe-setup",
            [
                new("Setup (.exe)", "exe-setup", "X64", "x64", isSelected: true),
                new("Setup (.exe)", "exe-setup", "X86", "x86")
            ]),
            new("Self-extracting EXE", "exe-sfx",
            [
                new("Self-extracting EXE", "exe-sfx", "X64", "x64"),
                new("Self-extracting EXE", "exe-sfx", "X86", "x86")
            ]),
            new("MSIX", "msix",
            [
                new("MSIX", "msix", "X64", "x64"),
                new("MSIX", "msix", "X86", "x86")
            ])
        ]);

        yield return new PackagePlatformViewModel("Linux",
        [
            new("DEB", "deb",
            [
                new("DEB", "deb", "X64", "x64"),
                new("DEB", "deb", "ARM64", "arm64")
            ]),
            new("RPM", "rpm",
            [
                new("RPM", "rpm", "X64", "x64"),
                new("RPM", "rpm", "ARM64", "arm64")
            ]),
            new("AppImage", "appimage",
            [
                new("AppImage", "appimage", "X64", "x64")
            ]),
            new("Flatpak", "flatpak",
            [
                new("Flatpak", "flatpak", "X64", "x64")
            ])
        ]);

        yield return new PackagePlatformViewModel("macOS",
        [
            new("DMG", "dmg",
            [
                new("DMG", "dmg", "X64", "x64"),
                new("DMG", "dmg", "ARM64", "arm64")
            ])
        ]);

        yield return new PackagePlatformViewModel("Android",
        [
            new("APK", "apk",
            [
                new("APK", "apk", "X64", "x64")
            ]),
            new("AAB", "aab",
            [
                new("AAB", "aab", "X64", "x64")
            ])
        ]);
    }
}

public class PackageFormatViewModel
{
    public PackageFormatViewModel(
        string label,
        string format,
        IEnumerable<PackageTargetOptionViewModel> architectures)
    {
        Label = label;
        Format = format;
        Architectures = new ObservableCollection<PackageTargetOptionViewModel>(architectures);
    }

    public string Label { get; }
    public string Format { get; }
    public ObservableCollection<PackageTargetOptionViewModel> Architectures { get; }
}

public partial class PackageTargetOptionViewModel : ReactiveObject
{
    public PackageTargetOptionViewModel(
        string formatLabel,
        string format,
        string architectureLabel,
        string architecture,
        bool isSelected = false)
    {
        FormatLabel = formatLabel;
        Format = format;
        ArchitectureLabel = architectureLabel;
        Architecture = architecture;
        _isSelected = isSelected;
    }

    public string FormatLabel { get; }
    public string Format { get; }
    public string ArchitectureLabel { get; }
    public string Architecture { get; }

    [Reactive] private bool _isSelected;
}

public partial class JobViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;
    private readonly INavigator _navigator;
    private readonly ProjectDetailViewModel _projectDetail;
    private readonly IFileSystemPicker? _fileSystemPicker;

    public DeploymentJob Job { get; }

    private string? _version;
    private string _displayName = string.Empty;

    public JobViewModel(
        DeploymentJob job,
        FleetApiClient client,
        INavigator navigator,
        ProjectDetailViewModel projectDetail,
        IFileSystemPicker? fileSystemPicker = null)
    {
        Job = job;
        _client = client;
        _navigator = navigator;
        _projectDetail = projectDetail;
        _fileSystemPicker = fileSystemPicker;
        _version = job.Version;
        _displayName = ComputeDisplayName(job.Version);

        OpenCommand = ReactiveCommand.Create(Open);
    }

    /// <summary>The detected GitVersion-style version, or null if none was reported yet.</summary>
    public string? Version
    {
        get => _version;
        private set
        {
            if (_version == value) return;
            this.RaiseAndSetIfChanged(ref _version, value);
            DisplayName = ComputeDisplayName(value);
        }
    }

    /// <summary>
    /// Friendly label shown in deployment lists. Falls back to a short prefix of the
    /// job GUID until a version is detected from the worker's log stream.
    /// </summary>
    public string DisplayName
    {
        get => _displayName;
        private set => this.RaiseAndSetIfChanged(ref _displayName, value);
    }

    /// <summary>
    /// Pushes a fresher snapshot of the underlying <see cref="DeploymentJob"/> into this VM,
    /// raising change notifications for the user-visible fields. Used when the deployment is
    /// renamed (a version was detected) or any other field is updated by the coordinator.
    /// </summary>
    public void ApplyJobUpdate(DeploymentJob updated)
    {
        if (updated.Id != Job.Id) return;
        Job.Version = updated.Version;
        Job.Status = updated.Status;
        Version = updated.Version;
        this.RaisePropertyChanged(nameof(StatusText));
        this.RaisePropertyChanged(nameof(StatusIcon));
        this.RaisePropertyChanged(nameof(StatusBrush));
    }

    private string ComputeDisplayName(string? version) =>
        string.IsNullOrWhiteSpace(version) ? Job.Id.ToString("N")[..8] : version!;

    public string StatusText => Job.Status switch
    {
        JobStatus.Queued => "Queued",
        JobStatus.Assigned => "Assigned",
        JobStatus.Running => "Running",
        JobStatus.Succeeded => "Succeeded",
        JobStatus.Failed => "Failed",
        JobStatus.Cancelled => "Cancelled",
        _ => Job.Status.ToString()
    };

    public string KindText => Job.Kind == JobKind.PackageBuild ? "Packages" : "Deploy";

    public IIcon StatusIcon => new Icon(Job.Status switch
    {
        JobStatus.Queued => "mdi-clock-outline",
        JobStatus.Assigned => "mdi-account-arrow-right-outline",
        JobStatus.Running => "mdi-rocket-launch",
        JobStatus.Succeeded => "mdi-check-circle",
        JobStatus.Failed => "mdi-alert-circle",
        JobStatus.Cancelled => "mdi-cancel",
        _ => "mdi-help-circle-outline"
    });

    public IBrush StatusBrush => Job.Status switch
    {
        JobStatus.Queued => new SolidColorBrush(Color.Parse("#9E9E9E")),
        JobStatus.Assigned => new SolidColorBrush(Color.Parse("#42A5F5")),
        JobStatus.Running => new SolidColorBrush(Color.Parse("#29B6F6")),
        JobStatus.Succeeded => new SolidColorBrush(Color.Parse("#43A047")),
        JobStatus.Failed => new SolidColorBrush(Color.Parse("#E53935")),
        JobStatus.Cancelled => new SolidColorBrush(Color.Parse("#FB8C00")),
        _ => Brushes.Gray
    };

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> OpenCommand { get; }

    private void Open()
    {
        _navigator.Go(() => new JobDetailViewModel(Job, _client, _navigator, _projectDetail, _fileSystemPicker));
    }
}
