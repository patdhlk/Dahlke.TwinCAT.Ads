namespace Dahlke.TwinCAT.Ads.Testing;

/// <summary>
/// Thrown when a <see cref="TestPlcTarget"/> assertion fails.
/// </summary>
/// <remarks>
/// A plain exception rather than a test framework's assertion type, because this package
/// deliberately depends on no test framework — every runner reports an unexpected
/// exception as a failure, so xunit, NUnit, MSTest and a bare console harness all work.
/// </remarks>
public sealed class PlcAssertionException : Exception
{
    /// <summary>Initialises a new instance with no message.</summary>
    public PlcAssertionException() { }

    /// <summary>Initialises a new instance with the given message.</summary>
    /// <param name="message">The failure description.</param>
    public PlcAssertionException(string message) : base(message) { }

    /// <summary>Initialises a new instance with the given message and inner exception.</summary>
    /// <param name="message">The failure description.</param>
    /// <param name="innerException">The underlying cause.</param>
    public PlcAssertionException(string message, Exception innerException)
        : base(message, innerException) { }
}
