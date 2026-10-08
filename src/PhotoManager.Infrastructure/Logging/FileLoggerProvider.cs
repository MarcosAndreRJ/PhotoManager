using Microsoft.Extensions.Logging;

namespace PhotoManager.Infrastructure.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly object _sync = new();

    public FileLoggerProvider(string directory)
    {
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "photomanager.log");
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _filePath, _sync);
    public void Dispose() { }

    private sealed class FileLogger(string category, string filePath, object sync) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTimeOffset.Now:O} [{logLevel}] {category}: {formatter(state, exception)}{Environment.NewLine}{(exception is null ? string.Empty : exception + Environment.NewLine)}";
            lock (sync) File.AppendAllText(filePath, line);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
