namespace Dahlke.TwinCAT.Ads;

/// <summary>Identity of the ADS device behind a connection.</summary>
/// <param name="Name">The device name reported by the runtime, for example <c>TCatPlcCtrl</c>.</param>
/// <param name="Version">Dotted version string in <c>major.minor.build</c> form.</param>
/// <remarks>
/// Deliberately excludes the device's ADS state, which changes independently and is read via
/// <see cref="IAdsConnection.GetAdsStateAsync"/>. Callers that want both compose them.
/// </remarks>
public sealed record AdsDeviceInfo(string Name, string Version);
