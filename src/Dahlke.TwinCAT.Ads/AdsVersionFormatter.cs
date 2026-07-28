using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Formats a Beckhoff <see cref="AdsVersion"/> as the dotted <c>major.minor.build</c> string used
/// by <see cref="AdsDeviceInfo.Version"/>.
/// </summary>
/// <remarks>
/// Pulled out of <see cref="AdsConnection.GetDeviceInfoAsync"/> into its own <see langword="internal"/>
/// class — the same reasoning as <see cref="CancellationDisambiguator"/>: <see cref="AdsVersion"/> is
/// constructible with no hardware, so unit tests in <c>Dahlke.TwinCAT.Ads.Tests</c> (via
/// <c>InternalsVisibleTo</c>) can cover the formatting directly, even though
/// <see cref="AdsConnection"/> itself has no seam for its concrete <c>AdsClient</c> field.
/// </remarks>
internal static class AdsVersionFormatter
{
    /// <summary>
    /// Formats <paramref name="version"/> as <c>"{Version}.{Revision}.{Build}"</c>.
    /// </summary>
    /// <param name="version">The version to format.</param>
    /// <returns>The dotted version string.</returns>
    public static string Format(AdsVersion version)
        => $"{version.Version}.{version.Revision}.{version.Build}";
}
