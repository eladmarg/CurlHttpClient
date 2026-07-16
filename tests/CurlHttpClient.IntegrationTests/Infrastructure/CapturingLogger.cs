using System.Collections.Concurrent;

namespace CurlHttp.IntegrationTests.Infrastructure;

/// <summary>Collects log lines so tests can assert on native verbose output
/// (e.g. curl's TLS session-reuse messages).</summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IEnumerable<string> Lines => _lines;

    public bool Contains(string fragment)
        => _lines.Any(l => l.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _lines.Enqueue(formatter(state, exception));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose()
        {
        }
    }
}
