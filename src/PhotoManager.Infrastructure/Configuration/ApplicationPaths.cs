namespace PhotoManager.Infrastructure.Configuration;

public sealed class ApplicationPaths
{
    public ApplicationPaths()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("PHOTOMANAGER_ROOT");
        Root = string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoManager")
            : overrideRoot;
        Data = Path.Combine(Root, "Data");
        Cache = Path.Combine(Root, "Cache");
        Logs = Path.Combine(Root, "Logs");
    }

    public string Root { get; }
    public string Data { get; }
    public string Cache { get; }
    public string Logs { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Cache);
        Directory.CreateDirectory(Logs);
    }
}
