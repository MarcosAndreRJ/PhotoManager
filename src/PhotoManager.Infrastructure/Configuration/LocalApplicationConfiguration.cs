using System.Text.Json;

namespace PhotoManager.Infrastructure.Configuration;

public sealed class LocalApplicationConfiguration
{
    private readonly ApplicationPaths _paths;
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public LocalApplicationConfiguration(ApplicationPaths paths)
    {
        _paths = paths;
        Load();
    }

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public void Set(string key, string value)
    {
        _values[key] = value;
        Save();
    }

    private void Load()
    {
        var file = Path.Combine(_paths.Data, "settings.json");
        if (!File.Exists(file)) return;
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
        if (values is not null)
            foreach (var pair in values) _values[pair.Key] = pair.Value;
    }

    private void Save()
    {
        _paths.EnsureDirectories();
        var file = Path.Combine(_paths.Data, "settings.json");
        File.WriteAllText(file, JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true }));
    }
}
