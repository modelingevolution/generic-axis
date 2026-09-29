using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ModelingEvolution.GenericAxis.TestApp.Tests;

/// <summary>An <see cref="ILogger"/> that keeps every entry, for asserting what was logged and at which level.</summary>
internal sealed class RecordingLogger : ILogger
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries.ToArray();

    public IReadOnlyList<string> At(LogLevel level) => Entries.Where(e => e.Level == level).Select(e => e.Message).ToArray();

    public void Clear() => _entries.Clear();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        _entries.Enqueue((logLevel, formatter(state, exception)));
}
