namespace Dahlke.TwinCAT.Ads.HardwareTests;

/// <summary>
/// A test attribute that skips the test unless the hardware gate env var is set.
/// Tests run only when <c>TWINCAT_HARDWARE_TESTS=1</c> OR <c>TWINCAT_TEST_AMSNETID</c> is set.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class HardwareFactAttribute : FactAttribute
{
    private static readonly bool HardwareEnabled =
        Environment.GetEnvironmentVariable("TWINCAT_HARDWARE_TESTS") == "1"
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TWINCAT_TEST_AMSNETID"));

    public HardwareFactAttribute()
    {
        if (!HardwareEnabled)
            Skip = "Hardware tests are disabled. Set TWINCAT_HARDWARE_TESTS=1 or TWINCAT_TEST_AMSNETID to run.";
    }
}
