using Microsoft.Extensions.Logging;

namespace Dahlke.TwinCAT.Ads.Tests.Fakes;

/// <summary>
/// One captured log entry: its level, its formatted message and its exception.
/// </summary>
/// <param name="Level">The level the entry was written at.</param>
/// <param name="Category">The logger category — usually the component's type name.</param>
/// <param name="Message">The formatted message, placeholders already substituted.</param>
/// <param name="Exception">The exception attached to the entry, if any.</param>
public sealed record LogEntry(
    LogLevel Level,
    string Category,
    string Message,
    Exception? Exception);

/// <summary>
/// An <see cref="ILoggerProvider"/> that records everything written through it, for
/// tests whose subject is the LOG rather than a return value.
/// </summary>
/// <remarks>
/// <para>
/// Needed wherever the only observable outcome is what an operator would read. The
/// clearest case is a route the embedded AMS router rejects: it cannot be thrown,
/// because throwing would tear down a router that is otherwise working and make one
/// unreachable device cost every reachable one — so a Warning is the ONLY signal, and
/// a test that does not read the log cannot tell the difference between "warned" and
/// "swallowed".
/// </para>
/// <para>
/// Messages are captured FORMATTED, with placeholders already substituted, so a test
/// can assert that the values an operator needs — a route's name, Net ID and address
/// — actually appear rather than that some message was emitted.
/// </para>
/// </remarks>
public sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly List<LogEntry> _entries = [];

    /// <summary>
    /// Everything recorded so far, in order. A copy, so a test can enumerate it while
    /// the subject under test keeps logging.
    /// </summary>
    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_entries) { return _entries.ToArray(); } }
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose() { }

    private void Record(LogEntry entry)
    {
        lock (_entries) { _entries.Add(entry); }
    }

    private sealed class RecordingLogger(RecordingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            owner.Record(new LogEntry(logLevel, category, formatter(state, exception), exception));

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
