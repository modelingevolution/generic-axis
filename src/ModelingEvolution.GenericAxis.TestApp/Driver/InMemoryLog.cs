using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace ModelingEvolution.GenericAxis.TestApp.Driver;

/// <summary>
/// The last <see cref="Capacity"/> log lines of one driver session, for the <c>/driver</c> page. Lines are an immutable
/// list swapped atomically, so the page renders a snapshot and never iterates a list that changes under it.
/// </summary>
public sealed class InMemoryLog : ILoggerProvider
{
    public const int Capacity = 200;

    private ImmutableList<string> _lines = [];

    public ImmutableList<string> Lines => _lines;

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName[(categoryName.LastIndexOf('.') + 1)..]);

    private void Add(string line) =>
        ImmutableInterlocked.Update(ref _lines, (list, l) => (list.Count >= Capacity ? list.RemoveAt(0) : list).Add(l), line);

    public void Dispose()
    {
    }

    private sealed class Logger(InMemoryLog sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var level = logLevel switch
            {
                LogLevel.Debug => "dbug",
                LogLevel.Information => "info",
                LogLevel.Warning => "warn",
                LogLevel.Error => "fail",
                LogLevel.Critical => "crit",
                _ => "trce",
            };
            var line = $"{DateTime.Now:HH:mm:ss.fff} {level} {category}: {formatter(state, exception)}";
            if (exception is not null) line += $" — {exception.GetType().Name}: {exception.Message}";
            sink.Add(line);
        }
    }
}
