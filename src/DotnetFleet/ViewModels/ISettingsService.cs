using System.IO;
using System.Text.Json;

namespace DotnetFleet.ViewModels;

public interface ISettingsService
{
    string? GetEndpoint();
    void SetEndpoint(string? endpoint);
    string? GetToken();
    void SetToken(string? token);
    void ClearToken();
}

public class InMemorySettingsService : ISettingsService
{
    private string? _endpoint;
    private string? _token;

    public string? GetEndpoint() => _endpoint;
    public void SetEndpoint(string? endpoint) => _endpoint = endpoint;
    public string? GetToken() => _token;
    public void SetToken(string? token) => _token = token;
    public void ClearToken() => _token = null;
}

public class FileSettingsService : ISettingsService
{
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DotnetFleet", "settings.json");

    private Dictionary<string, string?> _data = [];

    public FileSettingsService() => Load();

    public string? GetEndpoint() => _data.GetValueOrDefault("endpoint");
    public void SetEndpoint(string? value) { _data["endpoint"] = value; Save(); }
    public string? GetToken() => _data.GetValueOrDefault("token");
    public void SetToken(string? value) { _data["token"] = value; Save(); }
    public void ClearToken() { _data.Remove("token"); Save(); }

    private void Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
                _data = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(SettingsFile)) ?? [];
        }
        catch { /* ignore corrupt settings */ }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(_data));
    }
}
