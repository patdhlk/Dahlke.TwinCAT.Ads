using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Tests for <see cref="SimulatedAdsConnectionPool"/> — verifies that
/// <see cref="PlcTargetOptions.InitialValues"/> are seeded into the connection
/// when the pool starts.
/// </summary>
public class SimulatedAdsConnectionPoolTests
{
    private static SimulatedAdsConnectionPool CreatePool(
        params (string id, PlcTargetOptions opts)[] targets)
    {
        var dict = new Dictionary<string, PlcTargetOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, opts) in targets)
            dict[id] = opts;

        var adsOptions = new TwinCatAdsOptions { Targets = dict };

        return new SimulatedAdsConnectionPool(
            Options.Create(adsOptions),
            NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task StartAsync_Seeds_InitialValues_IntoConnection()
    {
        // Arrange: a simulated target with two seeded values
        var pool = CreatePool(
            ("sim1", new PlcTargetOptions
            {
                Mode = ConnectionMode.Simulated,
                DisplayName = "Sim",
                InitialValues = new Dictionary<string, object?>
                {
                    ["MAIN.bEnabled"] = true,
                    ["MAIN.nSpeed"]   = 1500,
                },
            }));

        // Act
        await pool.StartAsync(CancellationToken.None);

        // Assert — both values are readable through the pool's connection
        var connection = pool.GetConnection("sim1");
        Assert.NotNull(connection);

        var bEnabled = await connection!.ReadValueAsync("MAIN.bEnabled", CancellationToken.None);
        Assert.Equal(true, bEnabled);

        var nSpeed = await connection.ReadValueAsync("MAIN.nSpeed", CancellationToken.None);
        Assert.Equal(1500, nSpeed);

        await pool.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_EmptyInitialValues_ConnectionIsEmpty()
    {
        // Regression: when InitialValues is empty, reading an unknown symbol is null
        var pool = CreatePool(
            ("sim1", new PlcTargetOptions
            {
                Mode = ConnectionMode.Simulated,
                DisplayName = "Sim",
            }));

        await pool.StartAsync(CancellationToken.None);

        var connection = pool.GetConnection("sim1");
        Assert.NotNull(connection);

        var value = await connection!.ReadValueAsync("MAIN.bEnabled", CancellationToken.None);
        Assert.Null(value);

        await pool.StopAsync(CancellationToken.None);
    }
}
