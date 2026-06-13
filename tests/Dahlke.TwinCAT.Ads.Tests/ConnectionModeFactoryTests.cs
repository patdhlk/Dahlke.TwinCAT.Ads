using Microsoft.Extensions.Logging.Abstractions;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Tests for the per-target <see cref="ConnectionMode"/> dispatch in the REAL
/// <c>AdsConnectionFactory</c>: a <see cref="ConnectionMode.Simulated"/> target
/// yields a <see cref="SimulatedAdsConnection"/> seeded from
/// <see cref="PlcTargetOptions.InitialValues"/>; a <see cref="ConnectionMode.Real"/>
/// target yields a real <c>AdsConnection</c>.
/// </summary>
public class ConnectionModeFactoryTests
{
    // The real factory is internal; the test project shares the assembly's
    // internal surface (InternalsVisibleTo) — same as FakeConnectionFactory
    // implementing the internal IAdsConnectionFactory.
    private static IAdsConnectionFactory CreateFactory() =>
        new AdsConnectionFactory(NullLoggerFactory.Instance);

    [Fact]
    public void Mode_Default_IsReal()
    {
        var options = new PlcTargetOptions();
        Assert.Equal(ConnectionMode.Real, options.Mode);
    }

    [Fact]
    public void Create_SimulatedMode_ReturnsSimulatedConnection()
    {
        var factory = CreateFactory();
        var options = new PlcTargetOptions
        {
            Mode = ConnectionMode.Simulated,
            DisplayName = "Sim PLC",
        };

        var connection = factory.Create("sim1", options);

        Assert.IsType<SimulatedAdsConnection>(connection);
        Assert.Equal("sim1", connection.PlcId);
        Assert.Equal("Sim PLC", connection.DisplayName);
        Assert.True(connection.IsConnected);
    }

    [Fact]
    public void Create_RealMode_ReturnsRealAdsConnection()
    {
        var factory = CreateFactory();
        var options = new PlcTargetOptions
        {
            Mode = ConnectionMode.Real,
            AmsNetId = "1.2.3.4.5.6",
            DisplayName = "Real PLC",
        };

        var connection = factory.Create("real1", options);

        Assert.IsType<AdsConnection>(connection);
    }

    [Fact]
    public async Task Create_SimulatedMode_SeedsInitialValues()
    {
        var factory = CreateFactory();
        var options = new PlcTargetOptions
        {
            Mode = ConnectionMode.Simulated,
            DisplayName = "Sim PLC",
            InitialValues = new()
            {
                ["MAIN.bRunning"] = true,
                ["MAIN.nCounter"] = 42,
            },
        };

        var connection = factory.Create("sim1", options);

        Assert.Equal(true, await connection.ReadValueAsync("MAIN.bRunning", CancellationToken.None));
        Assert.Equal(42, await connection.ReadValueAsync("MAIN.nCounter", CancellationToken.None));
    }
}
