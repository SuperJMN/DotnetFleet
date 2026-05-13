using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DotnetFleet.Api.Client;
using DotnetFleet.Core.Domain;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace DotnetFleet.ViewModels;

public partial class ProjectSecretsViewModel : ReactiveObject, IDisposable
{
    private readonly FleetApiClient _client;
    private readonly Guid _projectId;
    private readonly CompositeDisposable _disposables = [];

    [Reactive] private bool _isLoading;
    [Reactive] private string? _error;
    [Reactive] private string _newName = "";
    [Reactive] private string _newValue = "";

    public ObservableCollection<ProjectSecretViewModel> Secrets { get; } = [];

    public ProjectSecretsViewModel(Guid projectId, FleetApiClient client)
    {
        _projectId = projectId;
        _client = client;

        var canAdd = this.WhenAnyValue(x => x.NewName, x => x.NewValue,
            (n, v) => !string.IsNullOrWhiteSpace(n) && !string.IsNullOrWhiteSpace(v));

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        AddCommand = ReactiveCommand.CreateFromTask(AddAsync, canAdd);

        _disposables.Add(RefreshCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message));
        _disposables.Add(AddCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message));
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> AddCommand { get; }
    public void Dispose() => _disposables.Dispose();

    private async Task LoadAsync()
    {
        IsLoading = true;
        Error = null;
        try
        {
            var secrets = await _client.GetProjectSecretsAsync(_projectId);
            ObservableCollectionSync.Sync(
                Secrets,
                secrets,
                secret => secret.Id,
                viewModel => viewModel.Secret.Id,
                secret => new ProjectSecretViewModel(secret, _projectId, _client, this),
                (viewModel, secret) => viewModel.ApplySecretUpdate(secret));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task AddAsync()
    {
        var secret = await _client.CreateProjectSecretAsync(_projectId, NewName.Trim(), NewValue);
        Secrets.Add(new ProjectSecretViewModel(secret, _projectId, _client, this));
        NewName = "";
        NewValue = "";
    }

    internal void Remove(ProjectSecretViewModel vm) => Secrets.Remove(vm);
}

public partial class ProjectSecretViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;
    private readonly Guid _projectId;
    private readonly ProjectSecretsViewModel _parent;

    public Secret Secret { get; private set; }

    [Reactive] private string _name;
    [Reactive] private string _value;
    [Reactive] private bool _isEditing;

    public ProjectSecretViewModel(Secret secret, Guid projectId, FleetApiClient client, ProjectSecretsViewModel parent)
    {
        Secret = secret;
        _projectId = projectId;
        _client = client;
        _parent = parent;
        _name = secret.Name;
        _value = secret.Value;

        DeleteCommand = ReactiveCommand.CreateFromTask(DeleteAsync);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);
        EditCommand = ReactiveCommand.Create(() => { IsEditing = true; });
        CancelCommand = ReactiveCommand.Create(Cancel);

        DeleteCommand.ThrownExceptions.Subscribe(_ => { });
        SaveCommand.ThrownExceptions.Subscribe(_ => { });
    }

    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> EditCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public void ApplySecretUpdate(Secret updated)
    {
        if (updated.Id != Secret.Id) return;

        Secret = updated;
        if (!IsEditing)
        {
            Name = updated.Name;
            Value = updated.Value;
        }

        this.RaisePropertyChanged(nameof(Secret));
    }

    private async Task DeleteAsync()
    {
        await _client.DeleteProjectSecretAsync(_projectId, Secret.Id);
        _parent.Remove(this);
    }

    private async Task SaveAsync()
    {
        await _client.UpdateProjectSecretAsync(_projectId, Secret.Id, Name.Trim(), Value);
        Secret.Name = Name.Trim();
        Secret.Value = Value;
        IsEditing = false;
    }

    private void Cancel()
    {
        Name = Secret.Name;
        Value = Secret.Value;
        IsEditing = false;
    }
}
