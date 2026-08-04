using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Dahlke.EtherCAT.Diagnostics.Tests;

/// <summary>
/// One captured <see cref="Microsoft.Extensions.Logging.ILogger"/> write: category, level, the
/// exception object if any, and the formatted message.
/// </summary>
public sealed record CapturedLogEntry(string Category, LogLevel Level, Exception? Exception, string Message);

/// <summary>
/// A minimal, thread-safe <see cref="ILoggerProvider"/> that records every
/// <see cref="Microsoft.Extensions.Logging"/> write made during a test run, so a test can assert on
/// what got logged. <see cref="EtherCatMonitorDegradationTests"/> needs it because two of the
/// monitor's contracts are log-only — a misconfigured poll cycle budget has to name the option and
/// the offending value, and both overrun routes have to log on the TRANSITION rather than once per
/// cycle — and a NullLogger would leave either free to regress.
/// </summary>
/// <remarks>
/// Constructed directly and handed to <c>new LoggerFactory([provider])</c>; there is no host here to
/// splice a provider into. <c>Adsify.Tests</c> and <c>Dahlke.EtherCAT.Esi.Tests</c> carry their own
/// copies of this type, which is a deliberate duplication: it is a few lines of test scaffolding,
/// and sharing it would mean a shared test-support assembly none of the three projects otherwise
/// needs.
/// </remarks>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

    /// <summary>Every entry captured so far. Safe to enumerate while writes are still arriving.</summary>
    public IReadOnlyCollection<CapturedLogEntry> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose() { }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly ConcurrentQueue<CapturedLogEntry> _entries;

        public CapturingLogger(string categoryName, ConcurrentQueue<CapturedLogEntry> entries)
        {
            _categoryName = categoryName;
            _entries = entries;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue(new CapturedLogEntry(_categoryName, logLevel, exception, formatter(state, exception)));
    }
}
