using PhotoManager.Application.Library;

namespace PhotoManager.Infrastructure.Files;

/// <summary>Confere backup por caminho relativo + tamanho (rápido, sem ler conteúdo) e copia o que falta preservando as datas.</summary>
public sealed class BackupVerifier : IBackupVerifier
{
    public Task<BackupReport> VerifyAsync(BackupTarget target, IProgress<int>? progress = null, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        if (!Directory.Exists(target.SourceFolder)) throw new IOException($"A pasta de origem não está disponível: {target.SourceFolder}");
        if (!Directory.Exists(target.TargetFolder)) throw new IOException($"O disco/pasta de backup não está conectado: {target.TargetFolder}");
        var missing = new List<string>();
        var different = new List<string>();
        long missingBytes = 0;
        var checkedCount = 0;
        foreach (var file in new DirectoryInfo(target.SourceFolder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.Hidden }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(target.SourceFolder, file.FullName);
            var copy = new FileInfo(Path.Combine(target.TargetFolder, relative));
            if (!copy.Exists) { missing.Add(relative); missingBytes += file.Length; }
            else if (copy.Length != file.Length) { different.Add(relative); missingBytes += file.Length; }
            if (++checkedCount % 200 == 0) progress?.Report(checkedCount);
        }
        progress?.Report(checkedCount);
        return new BackupReport(checkedCount, missing, different, missingBytes);
    }, cancellationToken);

    public Task<int> CopyMissingAsync(BackupTarget target, BackupReport report, IProgress<int>? progress = null, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var copied = 0;
        foreach (var relative in report.Missing.Concat(report.Different))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.Combine(target.SourceFolder, relative);
            var destination = Path.Combine(target.TargetFolder, relative);
            if (!File.Exists(source)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = destination + ".pmpart";
            File.Copy(source, temporary, overwrite: true);
            File.SetLastWriteTimeUtc(temporary, File.GetLastWriteTimeUtc(source));
            File.Move(temporary, destination, overwrite: true);                         // "diferente" = cópia do backup desatualizada/corrompida
            copied++;
            progress?.Report(copied);
        }
        return copied;
    }, cancellationToken);
}
