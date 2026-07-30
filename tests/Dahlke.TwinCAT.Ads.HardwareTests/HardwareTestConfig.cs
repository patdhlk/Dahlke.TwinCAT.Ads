namespace Dahlke.TwinCAT.Ads.HardwareTests;

/// <summary>
/// Reads hardware test configuration from environment variables.
/// </summary>
internal static class HardwareTestConfig
{
    /// <summary>AMS Net ID of the target PLC. Required. Set via TWINCAT_TEST_AMSNETID.</summary>
    public static string AmsNetId =>
        Environment.GetEnvironmentVariable("TWINCAT_TEST_AMSNETID")
        ?? throw new InvalidOperationException("TWINCAT_TEST_AMSNETID env var is not set.");

    /// <summary>ADS port. Default 851. Set via TWINCAT_TEST_PORT.</summary>
    public static int Port
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("TWINCAT_TEST_PORT");
            return int.TryParse(raw, out var port) ? port : 851;
        }
    }

    /// <summary>Fully-qualified name of a writable INT symbol. Set via TWINCAT_TEST_SYMBOL_INT.</summary>
    public static string? SymbolInt =>
        Environment.GetEnvironmentVariable("TWINCAT_TEST_SYMBOL_INT");

    /// <summary>
    /// Fully-qualified name of a STRUCT (or FUNCTION_BLOCK) symbol. Set via
    /// TWINCAT_TEST_SYMBOL_STRUCT.
    /// </summary>
    /// <remarks>
    /// Read-only as far as these tests are concerned — nothing writes it, so its declared members
    /// can be anything. It MUST however be stable for the duration of the run: the struct facts
    /// compare the value a notification decodes to against the value a fresh read returns, and a
    /// symbol the PLC program is continuously mutating would make that comparison meaningless.
    /// Point this at a struct the test program leaves alone.
    /// </remarks>
    public static string? SymbolStruct =>
        Environment.GetEnvironmentVariable("TWINCAT_TEST_SYMBOL_STRUCT");

    /// <summary>
    /// Fully-qualified name of an ARRAY symbol. Set via TWINCAT_TEST_SYMBOL_ARRAY.
    /// </summary>
    /// <remarks>
    /// Same stability requirement as <see cref="SymbolStruct"/>, and for the same reason. The array
    /// symbol is the one that matters most: an array notification is the only container shape whose
    /// raw value comes from <c>IAccessorValueFactory.CreateValue</c> over the notification payload
    /// (a struct with sub-symbols skips the payload and reads its members), so this is where a
    /// divergence between the payload decode and a real read would actually show up.
    /// </remarks>
    public static string? SymbolArray =>
        Environment.GetEnvironmentVariable("TWINCAT_TEST_SYMBOL_ARRAY");

    /// <summary>
    /// Fully-qualified name of an alarm array symbol (<c>ARRAY[..] OF ST_ErrorEntry</c>).
    /// Set via TWINCAT_TEST_SYMBOL_ALARMS.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="SymbolStruct"/> and <see cref="SymbolArray"/> this symbol need NOT be
    /// stable: the alarm test asserts the array BINDS, not that a notification matches a
    /// re-read, so a live alarm list is a perfectly good — arguably better — target.
    /// </remarks>
    public static string? SymbolAlarms =>
        Environment.GetEnvironmentVariable("TWINCAT_TEST_SYMBOL_ALARMS");

    /// <summary>Returns true when at least the INT symbol is configured.</summary>
    public static bool HasSymbolInt => !string.IsNullOrWhiteSpace(SymbolInt);

    /// <summary>Returns true when a struct symbol is configured.</summary>
    public static bool HasSymbolStruct => !string.IsNullOrWhiteSpace(SymbolStruct);

    /// <summary>Returns true when an array symbol is configured.</summary>
    public static bool HasSymbolArray => !string.IsNullOrWhiteSpace(SymbolArray);

    /// <summary>Returns true when an alarm array symbol is configured.</summary>
    public static bool HasSymbolAlarms => !string.IsNullOrWhiteSpace(SymbolAlarms);
}
