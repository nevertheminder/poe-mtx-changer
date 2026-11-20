using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace PoeMtxChanger.Core.Logging;

public sealed record LogEntry(DateTime Time, LogLevel Level, string Category, string Message);

/// <summary>Writes one log file per day and keeps the most recent entries in memory for the UI. Local only — no telemetry.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly Lock _lock = new();
    private readonly ConcurrentQueue<LogEntry> _recent = new();
    private const int RecentCapacity = 500;

    public Func<LogLevel> MinimumLevel { get; set; } = () => LogLevel.Information;
    public event Action<LogEntry>? EntryWritten;

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public IReadOnlyCollection<LogEntry> Recent => _recent.ToArray();
    public string CurrentFile => Path.Combine(_directory, $"poemtxchanger-{DateTime.Now:yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(LogEntry e, Exception? ex)
    {
        var line = $"{e.Time:yyyy-MM-dd HH:mm:ss.fff} [{e.Level,-11}] {e.Category}: {e.Message}";
        if (ex != null) line += Environment.NewLine + ex;
        lock (_lock)
        {
            try { File.AppendAllText(CurrentFile, line + Environment.NewLine); }
            catch (IOException) { }
        }
        _recent.Enqueue(e);
        while (_recent.Count > RecentCapacity && _recent.TryDequeue(out _)) { }
        EntryWritten?.Invoke(e);
    }

    public void Dispose() { }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= owner.MinimumLevel();

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var shortCat = category[(category.LastIndexOf('.') + 1)..];
            owner.Write(new LogEntry(DateTime.Now, logLevel, shortCat, formatter(state, exception) + (exception != null ? $" ({exception.GetType().Name}: {exception.Message})" : "")), exception);
        }
    }
}
