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

    /// <summary>Returns true when at least the INT symbol is configured.</summary>
    public static bool HasSymbolInt => !string.IsNullOrWhiteSpace(SymbolInt);
}
