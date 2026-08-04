using Dahlke.EtherCAT.Diagnostics;
using Dahlke.TwinCAT.Ads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dahlke.EtherCAT.Diagnostics.Tests;

/// <summary>
/// Builds an <see cref="EtherCatClient"/> over the library's simulated raw-channel store, so the
/// client's read paths can be exercised against seeded bytes with no hardware and no router.
///
/// The store carries no protocol knowledge: it does not know what an index group means. A test
/// seeds the exact bytes the client's decoder should read back, and an unseeded read answers
/// <c>DeviceInvalidOffset</c> — the code real hardware gives for a bad offset — so the
/// error-classification paths run here too.
/// </summary>
internal sealed class SimulatedRawChannelFixture : IDisposable
{
    /// <summary>The rig's EtherCAT master Net ID. Any Net ID would do; this one keeps the
    /// seeded values recognisable against the hardware notes.</summary>
    internal const string MasterNetId = "5.138.44.199.2.1";

    /// <summary>AMSPORT_R0_MASTER — the diagnostic port every master-level read uses.</summary>
    internal const int MasterPort = 0xFFFF;

    private readonly ServiceProvider _provider;

    internal IAdsRawChannelFactory Factory { get; }
    internal EtherCatClient Client { get; }

    internal SimulatedRawChannelFixture()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTwinCatAds(options =>
        {
            options.RawChannels.Mode = ConnectionMode.Simulated;

            // TwinCatAdsOptionsValidator rejects an empty Targets collection outright — see
            // Dahlke.TwinCAT.Ads.Tests.RawChannelConfigurationBindingTests, which carries the
            // same minimal simulated target for the same reason. Raw channels address whatever
            // (amsNetId, port) a caller names and have no use for this target; it exists purely
            // to satisfy startup validation.
            options.Targets["unused"] = new PlcTargetOptions { Mode = ConnectionMode.Simulated };
        });

        _provider = services.BuildServiceProvider();
        Factory = _provider.GetRequiredService<IAdsRawChannelFactory>();
        Client = new EtherCatClient(NullLogger<EtherCatClient>.Instance, Factory);
    }

    public void Dispose() => _provider.Dispose();

    /// <summary>Seeds one slot on the master's diagnostic port.</summary>
    internal void SeedMaster(uint indexGroup, uint indexOffset, params byte[] data) =>
        Seed(MasterPort, indexGroup, indexOffset, data);

    /// <summary>Seeds one slot on an arbitrary port — used for CoE, which addresses the slave
    /// by ADS port rather than by index offset.</summary>
    internal void Seed(int port, uint indexGroup, uint indexOffset, params byte[] data)
    {
        if (!Factory.TryGetSimulated(MasterNetId, port, out var simulated) || simulated is null)
            throw new InvalidOperationException(
                "raw channel factory is not in simulation mode — check AdsRawChannelOptions.Mode");

        simulated.Seed(indexGroup, indexOffset, data);
    }

    /// <summary>Little-endian uint16, the encoding every EtherCAT master count and address uses.</summary>
    internal static byte[] U16(ushort value) => [(byte)(value & 0xFF), (byte)(value >> 8)];

    /// <summary>Little-endian uint32.</summary>
    internal static byte[] U32(uint value) =>
    [
        (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF),
        (byte)((value >> 16) & 0xFF), (byte)(value >> 24),
    ];
}
