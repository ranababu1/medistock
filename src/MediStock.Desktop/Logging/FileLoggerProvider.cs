using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MediStock.Desktop.Logging;

/// <summary>
/// Minimal rolling file logger. The application must stay diagnosable on a
/// pharmacy PC where no debugger or log collector is available, so a plain
/// dated text file under %LOCALAPPDATA% is a deliberate, low-tech choice.
/// </summary>
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int RetentionDays = 14;

    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly object _writeGate = new();
    private readonly string _directory;

    public FileLoggerProvider(string? directory = null, LogLevel minimumLevel = LogLevel.Information)
    {
        _directory = directory ?? DefaultDirectory;
        MinimumLevel = minimumLevel;

        try
        {
            Directory.CreateDirectory(_directory);
            RemoveExpiredFiles();
        }
        catch (IOException)
        {
            // Logging must never take the application down.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public LogLevel MinimumLevel { get; }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MediStock",
        "logs");

    public static string CurrentLogFile =>
        Path.Combine(DefaultDirectory, $"medistock-{DateTime.Today:yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    public void Dispose() => _loggers.Clear();

    internal bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= MinimumLevel;

    internal void Write(DateTimeOffset timestamp, LogLevel level, string category, EventId eventId, string message, Exception? exception)
    {
        var builder = new StringBuilder(256);
        builder.Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(level.ToString().ToUpperInvariant().PadRight(5))
            .Append(' ')
            .Append(category)
            .Append('[').Append(eventId.Id).Append("] ")
            .Append(message);

        if (exception is not null)
        {
            builder.AppendLine();
            builder.Append(Indent(exception.ToString()));
        }

        var line = builder.ToString();

        try
        {
            lock (_writeGate)
            {
                File.AppendAllText(
                    Path.Combine(_directory, $"medistock-{timestamp.LocalDateTime:yyyyMMdd}.log"),
                    line + Environment.NewLine,
                    Encoding.UTF8);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Indent(string text) =>
        string.Join(Environment.NewLine, text.Split('\n').Select(line => "    " + line.TrimEnd('\r')));

    private void RemoveExpiredFiles()
    {
        var cutoff = DateTime.Today.AddDays(-RetentionDays);

        foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("medistock-*.log"))
        {
            if (file.LastWriteTime < cutoff)
            {
                try
                {
                    file.Delete();
                }
                catch (IOException)
                {
                }
            }
        }
    }
}

internal sealed class FileLogger : ILogger
{
    private readonly FileLoggerProvider _provider;
    private readonly string _category;

    public FileLogger(FileLoggerProvider provider, string category)
    {
        _provider = provider;
        _category = category;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => _provider.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel) || formatter is null)
        {
            return;
        }

        _provider.Write(DateTimeOffset.Now, logLevel, _category, eventId, formatter(state, exception), exception);
    }
}
