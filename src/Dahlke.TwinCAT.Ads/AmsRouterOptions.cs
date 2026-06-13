namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Options for the embedded AMS TCP/IP router.
/// Populated from the <c>AmsRouter</c> configuration section.
/// </summary>
public sealed class AmsRouterOptions
{
    /// <summary>
    /// The AMS Net ID for the embedded router to bind to.
    /// When <see langword="null"/> or empty the embedded router is disabled
    /// and the system router is used instead.
    /// </summary>
    public string? NetId { get; set; }
}
