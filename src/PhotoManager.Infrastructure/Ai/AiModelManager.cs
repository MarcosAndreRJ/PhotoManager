using System.Net.Http;
using System.Security.Cryptography;
using PhotoManager.Application.Ai;

namespace PhotoManager.Infrastructure.Ai;

/// <summary>Baixa modelos de IA só quando o usuário pede (Configurações), com progresso, cancelamento e conferência de hash.</summary>
public sealed class AiModelManager(string modelsFolder, HttpMessageHandler? handler = null) : IAiModelManager
{
    private readonly HttpClient _http = CreateClient(handler);

    public string ModelsFolder { get; } = modelsFolder;

    public bool IsInstalled(AiModelInfo model) => File.Exists(PathOf(model));

    public async Task DownloadAsync(AiModelInfo model, IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ModelsFolder);
        var target = PathOf(model);
        var temporary = target + ".part";
        try
        {
            using var response = await _http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = File.Create(temporary))
            {
                var buffer = new byte[1 << 16];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    progress?.Report(new ModelDownloadProgress(model, received, total));
                }
            }
            if (model.Sha256 is { Length: > 0 } expected)
            {
                await using var check = File.OpenRead(temporary);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(check, cancellationToken));
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"O arquivo baixado de {model.Name} não confere (hash diferente). Tente de novo.");
            }
            File.Move(temporary, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); } catch (IOException) { }
            throw;
        }
    }

    public void Remove(AiModelInfo model)
    {
        if (File.Exists(PathOf(model))) File.Delete(PathOf(model));
    }

    private string PathOf(AiModelInfo model) => Path.Combine(ModelsFolder, model.FileName);

    private static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PhotoManager/1.0 (desktop; modelos de IA locais)");
        client.Timeout = TimeSpan.FromHours(2);
        return client;
    }
}
