using Sextant.Core;
using Microsoft.Extensions.Logging;

namespace Sextant.Mcp;

/// <summary>
/// Bridges Microsoft.Extensions.Logging to <see cref="FileLogger"/>.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLogger _fileLogger;

    public FileLoggerProvider(FileLogger fileLogger)
    {
        _fileLogger = fileLogger;
    }

    public ILogger CreateLogger(string categoryName) => new FileLoggerAdapter(_fileLogger, categoryName);

    public void Dispose() { } // FileLogger lifetime managed separately via DI

    private sealed class FileLoggerAdapter : ILogger
    {
        private readonly FileLogger _fileLogger;
        private readonly string _category;

        public FileLoggerAdapter(FileLogger fileLogger, string category)
        {
            _fileLogger = fileLogger;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var level = logLevel switch
            {
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERROR",
                LogLevel.Critical => "CRIT",
                _ => "INFO"
            };

            var message = formatter(state, exception);
            _fileLogger.Write($"[{level}] {_category}: {message}");

            if (exception != null)
                _fileLogger.Write($"[{level}] {_category}: {exception}");
        }
    }
}
