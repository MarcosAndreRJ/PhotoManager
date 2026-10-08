using System.Text.Json;
using PhotoManager.Application.Transfer;

namespace PhotoManager.Infrastructure.Configuration;

/// <summary>Layout da Transferência (pastas, modos, ordenação, proporção, fixadas e recentes) numa chave do settings.json.</summary>
public sealed class TransferSettingsStore(LocalApplicationConfiguration configuration) : ITransferSettings
{
    private const string Key = "transfer.layout";

    public TransferLayout Load()
    {
        try { return configuration.Get(Key) is { Length: > 0 } json ? JsonSerializer.Deserialize<TransferLayout>(json) ?? TransferLayout.Default : TransferLayout.Default; }
        catch (JsonException) { return TransferLayout.Default; }                       // arquivo antigo/corrompido: começa do padrão
    }

    public void Save(TransferLayout layout)
    {
        try { configuration.Set(Key, JsonSerializer.Serialize(layout)); }
        catch (IOException) { /* guardar o layout é conveniência; nunca derruba a tela */ }
    }
}
