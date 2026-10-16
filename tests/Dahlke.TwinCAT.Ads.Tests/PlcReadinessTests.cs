using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// The liveness verdict behind <c>AdsConnection.IsAliveAsync</c>: the pool publishes Connected, and keeps it, only
/// for a PLC that runs a program whose symbol table is the one the connection started with.
/// </summary>
public class PlcReadinessTests
{
    [Theory]
    [InlineData(AdsState.Run)]
    [InlineData(AdsState.Stop)]
    public void A_plc_with_a_loaded_program_is_ready(AdsState state)
        => Assert.Equal(PlcReadiness.Verdict.Ready, new PlcReadiness().Judge(state, symbolVersion: 3));

    [Theory]
    [InlineData(AdsState.Config)]
    [InlineData(AdsState.Init)]
    [InlineData(AdsState.Start)]
    [InlineData(AdsState.Reset)]
    [InlineData(AdsState.Invalid)]
    [InlineData(AdsState.Error)]
    public void A_plc_between_states_is_not_ready(AdsState state)
        => Assert.Equal(PlcReadiness.Verdict.NotRunning, new PlcReadiness().Judge(state, symbolVersion: 3));

    [Fact]
    public void A_running_plc_without_a_symbol_table_is_not_ready()
        => Assert.Equal(PlcReadiness.Verdict.NoSymbolTable, new PlcReadiness().Judge(AdsState.Run, symbolVersion: null));

    [Fact]
    public void A_new_symbol_version_ends_the_connection_and_stays_ended()
    {
        // An activation or online change: every handle and cached symbol of this connection may be wrong now.
        var readiness = new PlcReadiness();
        Assert.Equal(PlcReadiness.Verdict.Ready, readiness.Judge(AdsState.Run, 3));
        Assert.Equal(PlcReadiness.Verdict.Ready, readiness.Judge(AdsState.Run, 3));

        Assert.Equal(PlcReadiness.Verdict.SymbolTableChanged, readiness.Judge(AdsState.Run, 4));
        Assert.Equal(PlcReadiness.Verdict.SymbolTableChanged, readiness.Judge(AdsState.Run, 4));
    }

    [Fact]
    public void The_symbol_version_is_taken_from_the_first_ready_answer()
    {
        // A probe during the activation must not pin the old version: the connection is not published until ready.
        var readiness = new PlcReadiness();
        Assert.Equal(PlcReadiness.Verdict.NotRunning, readiness.Judge(AdsState.Config, 3));
        Assert.Equal(PlcReadiness.Verdict.NoSymbolTable, readiness.Judge(AdsState.Run, null));

        Assert.Equal(PlcReadiness.Verdict.Ready, readiness.Judge(AdsState.Run, 4));
        Assert.Equal(PlcReadiness.Verdict.Ready, readiness.Judge(AdsState.Run, 4));
    }
}
