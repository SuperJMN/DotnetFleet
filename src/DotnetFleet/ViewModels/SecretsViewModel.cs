using System.Collections.ObjectModel;
using System.Reactive;
using DotnetFleet.Api.Client;
using DotnetFleet.Core.Domain;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace DotnetFleet.ViewModels;

public partial class SecretsViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;

    [Reactive] private bool _isLoading;
    [Reactive] private string? _error;
    [Reactive] private string _newName = "";
    [Reactive] private string _newValue = "";

    public ObservableCollection<SecretViewModel> Secrets { get; } = [];

    public SecretsViewModel(FleetApiClient client)
    {
        _client = client;

        var canAdd = this.WhenAnyValue(x => x.NewName, x => x.NewValue,
            (n, v) => !string.IsNullOrWhiteSpace(n) && !string.IsNullOrWhiteSpace(v));

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        AddCommand = ReactiveCommand.CreateFromTask(AddAsync, canAdd);

        RefreshCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message);
        AddCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message);

        RefreshCommand.Execute(Unit.Default).Subscribe();
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> AddCommand { get; }

    private async Task LoadAsync()
    {
        IsLoading = true;
        Error = null;
        try
        {
            var secrets = await _client.GetSecretsAsync();
            Secrets.Clear();
            foreach (var s in secrets)
                Secrets.Add(new SecretViewModel(s, _client, this));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task AddAsync()
    {
        var secret = await _client.CreateSecretAsync(NewName.Trim(), NewValue);
        Secrets.Add(new SecretViewModel(secret, _client, this));
        NewName = "";
        NewValue = "";
    }

    internal void Remove(SecretViewModel vm) => Secrets.Remove(vm);
}

public partial class SecretViewModel : ReactiveObject
{
    private readonly FleetApiClient _client;
    private readonly SecretsViewModel _parent;

    public Secret Secret { get; }

    [Reactive] private string _name;
    [Reactive] private string _value;
    [Reactive] private bool _isEditing;

    public SecretViewModel(Secret secret, FleetApiClient client, SecretsViewModel parent)
    {
        Secret = secret;
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

    private async Task DeleteAsync()
    {
        await _client.DeleteSecretAsync(Secret.Id);
        _parent.Remove(this);
    }

    private async Task SaveAsync()
    {
        await _client.UpdateSecretAsync(Secret.Id, Name.Trim(), Value);
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
