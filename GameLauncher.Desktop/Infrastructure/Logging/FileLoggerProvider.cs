using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Infrastructure.Logging;

/// <summary>
/// Writes log entries to a daily rolling file.
/// </summary>
/// <remarks>
/// <para>
/// Microsoft.Extensions.Logging ships console and debug providers but no file
/// provider, and a desktop app that has already shipped cannot be attached to a
/// debugger. This fills that gap without taking a third-party logging
/// dependency.
/// </para>
/// <para>
/// Writes are synchronous and flushed immediately. That is a deliberate trade:
/// an asynchronous buffered writer performs better, but loses the final and most
/// interesting entries precisely when the process is crashing. Entries are a few
/// hundred bytes and the volume is low, so the cost is not measurable in
/// practice.
/// </para>
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly FileLogWriter _writer;
    private readonly LogLevel _minimumLevel;

    /// <summary>
    /// Initialises a new provider writing into the supplied directory.
    /// </summary>
    /// <param name="logDirectory">Directory to write log files into. Created if missing.</param>
    /// <param name="minimumLevel">Lowest level that will be written.</param>
    /// <param name="retainedFileCount">How many daily files to keep before deleting the oldest.</param>
    /// <exception cref="ArgumentException"><paramref name="logDirectory"/> is null or blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="retainedFileCount"/> is less than one.</exception>
    public FileLoggerProvider(string logDirectory, LogLevel minimumLevel = LogLevel.Information, int retainedFileCount = 7)
    {
        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            throw new ArgumentException("Log directory must be provided.", nameof(logDirectory));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(retainedFileCount, 1);

        _minimumLevel = minimumLevel;
        _writer = new FileLogWriter(logDirectory, retainedFileCount);
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, _writer, _minimumLevel));

    /// <inheritdoc />
    public void Dispose()
    {
        _loggers.Clear();
        _writer.Dispose();
    }

    /// <summary>
    /// The per-category <see cref="ILogger"/> handed out by this provider.
    /// </summary>
    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly FileLogWriter _writer;
        private readonly LogLevel _minimumLevel;

        internal FileLogger(string category, FileLogWriter writer, LogLevel minimumLevel)
        {
            // Trim the namespace so lines stay readable; the leaf type is what identifies the source.
            var lastDot = category.LastIndexOf('.');
            _category = lastDot >= 0 && lastDot < category.Length - 1
                ? category[(lastDot + 1)..]
                : category;

            _writer = writer;
            _minimumLevel = minimumLevel;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            ArgumentNullException.ThrowIfNull(formatter);

            var builder = new StringBuilder(160)
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                .Append(" [").Append(Abbreviate(logLevel)).Append("] ")
                .Append(_category).Append(": ")
                .Append(formatter(state, exception));

            if (exception is not null)
            {
                builder.AppendLine().Append(exception);
            }

            _writer.Write(builder.ToString());
        }

        /// <summary>Fixed-width level tag, so entries stay column-aligned in a text editor.</summary>
        private static string Abbreviate(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???"
        };
    }

    /// <summary>
    /// Scope implementation for a provider that does not support scopes.
    /// </summary>
    private sealed class NullScope : IDisposable
    {
        internal static readonly NullScope Instance = new();

        private NullScope()
        {
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }

    /// <summary>
    /// Serialises writes from every category into the current day's file.
    /// </summary>
    private sealed class FileLogWriter : IDisposable
    {
        private readonly string _directory;
        private readonly int _retainedFileCount;
        private readonly object _gate = new();

        private StreamWriter? _stream;
        private DateOnly _openedForDate;
        private bool _disposed;

        internal FileLogWriter(string directory, int retainedFileCount)
        {
            _directory = directory;
            _retainedFileCount = retainedFileCount;
        }

        internal void Write(string line)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                try
                {
                    var today = DateOnly.FromDateTime(DateTime.Now);
                    if (_stream is null || today != _openedForDate)
                    {
                        Roll(today);
                    }

                    _stream?.WriteLine(line);
                    _stream?.Flush();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Logging must never take the app down. If the log file is
                    // unavailable — locked, disk full, permissions revoked — drop
                    // the entry and keep the process running. The debug provider
                    // is still attached and will have received the same entry.
                    _stream?.Dispose();
                    _stream = null;
                }
            }
        }

        /// <summary>Opens the file for <paramref name="date"/> and prunes old files.</summary>
        private void Roll(DateOnly date)
        {
            _stream?.Dispose();

            Directory.CreateDirectory(_directory);

            var path = Path.Combine(
                _directory,
                $"gamelauncher-{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.log");

            _stream = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = false
            };

            _openedForDate = date;
            Prune();
        }

        /// <summary>Deletes the oldest files beyond the retention count.</summary>
        private void Prune()
        {
            try
            {
                var stale = Directory
                    .EnumerateFiles(_directory, "gamelauncher-*.log")
                    .OrderByDescending(path => path, StringComparer.Ordinal)
                    .Skip(_retainedFileCount);

                foreach (var path in stale)
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Retention is best-effort; failing to prune is not worth surfacing.
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _stream?.Flush();
                _stream?.Dispose();
                _stream = null;
            }
        }
    }
}
