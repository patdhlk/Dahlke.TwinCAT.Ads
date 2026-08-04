using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Dahlke.EtherCAT.Esi.Tests;

/// <summary>
/// One captured <see cref="Microsoft.Extensions.Logging.ILogger"/> write: category, level, the
/// exception object if any, and the formatted message.
/// </summary>
public sealed record CapturedLogEntry(string Category, LogLevel Level, Exception? Exception, string Message);

/// <summary>
/// A minimal, thread-safe <see cref="ILoggerProvider"/> that records every
/// <see cref="Microsoft.Extensions.Logging"/> write made during a test run, so a test can assert
/// on what got logged — in particular, that nothing unexpected did. No integration test in this
/// suite could previously see a logged-but-swallowed exception (an unhandled exception logged by
/// the ASP.NET Core hosting layer on a request task nothing awaits, for instance): every existing
/// assertion is over the HTTP response or over application state, neither of which a background
/// logging call touches.
/// </summary>
/// <remarks>
/// Spliced in via <c>IWebHostBuilder.ConfigureLogging(logging => logging.AddProvider(...))</c>,
/// not <c>ConfigureTestServices</c> — it needs to reach the actual
/// <see cref="Microsoft.Extensions.Logging.ILoggerFactory"/>'s provider list that the hosting
/// layer's own loggers (e.g. <c>Microsoft.AspNetCore.Hosting.Diagnostics</c>) are created from.
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

        // Log writes arrive from request-processing threads, concurrently with whatever thread a
        // test is asserting from; ConcurrentQueue.Enqueue is the only shared-state touch here.
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue(new CapturedLogEntry(_categoryName, logLevel, exception, formatter(state, exception)));
    }
}
