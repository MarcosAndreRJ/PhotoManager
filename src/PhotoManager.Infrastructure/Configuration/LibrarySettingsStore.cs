using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoManager.Application.Library;

namespace PhotoManager.Infrastructure.Configuration;

/// <summary>Preferências da Biblioteca (ordenação, grade, tema, predefinições de exportação, IA, mapa) numa chave do settings.json.</summary>
public sealed class LibrarySettingsStore(LocalApplicationConfiguration configuration) : ILibrarySettings
{
    private const string Key = "library.preferences";
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    public LibraryPreferences Load()
    {
        try { return configuration.Get(Key) is { Length: > 0 } json ? JsonSerializer.Deserialize<LibraryPreferences>(json, Options) ?? LibraryPreferences.Default : LibraryPreferences.Default; }
        catch (JsonException) { return LibraryPreferences.Default; }
    }

    public void Save(LibraryPreferences preferences)
    {
        try { configuration.Set(Key, JsonSerializer.Serialize(preferences, Options)); }
        catch (IOException) { }
    }
}
