using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using DotnetFleet.Api.Client;
using DotnetFleet.Core.Domain;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Zafiro.DivineBytes;
using Zafiro.UI;
using Zafiro.UI.Navigation;
using Zafiro.UI.Shell.Utils;

namespace DotnetFleet.ViewModels;

[Section(name: "Secrets", icon: "mdi-key-outline", sortIndex: 2)]
public partial class SecretsViewModel : ReactiveObject, IHaveHeader
{
    private readonly FleetApiClient _client;
    private readonly IFileSystemPicker _picker;

    [Reactive] private bool _isLoading;
    [Reactive] private string? _error;
    [Reactive] private string _newName = "";
    [Reactive] private string _newValue = "";

    public ObservableCollection<SecretViewModel> Secrets { get; } = [];

    public SecretsViewModel(FleetApiClient client, IFileSystemPicker picker)
    {
        _client = client;
        _picker = picker;

        var canAdd = this.WhenAnyValue(x => x.NewName, x => x.NewValue,
            (n, v) => !string.IsNullOrWhiteSpace(n) && !string.IsNullOrWhiteSpace(v));

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        AddCommand = ReactiveCommand.CreateFromTask(AddAsync, canAdd);
        ImportFromEnvCommand = ReactiveCommand.CreateFromTask(ImportFromEnvAsync);

        RefreshCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message);
        AddCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message);
        ImportFromEnvCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message);

        Header = Observable.Return<object>(new SectionHeader("Global Secrets",
            new HeaderAction("Refresh", "mdi-refresh", RefreshCommand)));

        _client.AuthenticatedChanges
            .Where(authenticated => authenticated)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => RefreshCommand.Execute(Unit.Default).Subscribe(_ => { }, _ => { }));
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> AddCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportFromEnvCommand { get; }
    public IObservable<object> Header { get; }

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

    private async Task ImportFromEnvAsync()
    {
        Error = null;

        var pickResult = await _picker.PickForOpen(
            new FileTypeFilter("Environment files (*.env)", ["*.env", ".env", "*"]),
            new FileTypeFilter("All files", ["*"]));

        if (pickResult.IsFailure)
        {
            throw new InvalidOperationException(pickResult.Error);
        }

        var maybeFile = pickResult.Value;
        if (maybeFile.HasNoValue)
        {
            return;
        }

        var file = maybeFile.Value;
        var lines = new List<string>();
        await using (var stream = file.ToStream())
        using (var reader = new StreamReader(stream))
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lines.Add(line);
            }
        }

        var parsedEntries = lines
            .Select(ParseEnvLine)
            .Where(e => e is not null)
            .Select(e => e!.Value)
            .ToList();

        var existingByName = Secrets.ToDictionary(s => s.Name, StringComparer.Ordinal);

        foreach (var (name, value) in parsedEntries)
        {
            if (existingByName.TryGetValue(name, out var existing))
            {
                await _client.UpdateSecretAsync(existing.Secret.Id, name, value);
                existing.Name = name;
                existing.Value = value;
                continue;
            }

            var created = await _client.CreateSecretAsync(name, value);
            var vm = new SecretViewModel(created, _client, this);
            Secrets.Add(vm);
            existingByName[name] = vm;
        }
    }

    private static (string Name, string Value)? ParseEnvLine(string line)
    {
        var text = line.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith('#'))
        {
            return null;
        }

        if (text.StartsWith("export ", StringComparison.Ordinal))
        {
            text = text["export ".Length..].TrimStart();
        }

        var separatorIndex = text.IndexOf('=');
        if (separatorIndex <= 0)
        {
            return null;
        }

        var name = text[..separatorIndex].Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var value = text[(separatorIndex + 1)..].Trim();
        if (value.Length >= 2)
        {
            var first = value[0];
            var last = value[^1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
            {
                value = value[1..^1];
            }
        }

        return (name, value);
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
