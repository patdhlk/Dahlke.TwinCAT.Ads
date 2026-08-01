using System.Reflection;
using TwinCAT.Ads;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// <see cref="AdsConnectionBase"/> — the scaffold a hand-written <see cref="IAdsConnection"/>
/// double derives from so it declares only the members the code under test reaches.
///
/// Coverage:
/// - Every operation throws <see cref="NotSupportedException"/> until overridden, and the message
///   names the DERIVING type and the member — the whole diagnostic value of the default.
/// - Nothing returns a plausible value in place of throwing (no null read, no empty browse), which
///   is what would let a test pass while exercising an unspecified path.
/// - Overriding one member leaves the rest throwing — the motivating scenario.
/// - Every interface member is overridable, asserted by reflection so a member added to
///   <see cref="IAdsConnection"/> non-virtually here fails rather than shipping un-fakeable.
/// - The state trio (State / IsConnected / ConnectionStateChanged) is coherent and moved by
///   <see cref="AdsConnectionBase.SetConnectionState"/>: change-guarded, new state visible to
///   handlers, every handler invoked even when one throws, failures surfaced rather than swallowed.
/// - WithTimeout validates then returns itself.
/// </summary>
public class AdsConnectionBaseTests
{
    // =========================================================================
    // Doubles
    // =========================================================================

    /// <summary>Overrides nothing — every member is the base's default.</summary>
    private sealed class BareDouble : AdsConnectionBase;

    /// <summary>Overrides identity so state transitions can name a target.</summary>
    private sealed class IdentifiedDouble : AdsConnectionBase
    {
        public override string PlcId => "plc1";

        /// <summary>Exposes the protected transition so a [Fact] can drive it.</summary>
        public void Move(ConnectionState state) => SetConnectionState(state);
    }

    /// <summary>The motivating case: one member answers, everything else still throws.</summary>
    private sealed class ReadOnlyDouble : AdsConnectionBase
    {
        public override Task<T> ReadValueAsync<T>(string symbolPath, CancellationToken ct = default)
            => Task.FromResult((T)Convert.ChangeType(42, typeof(T)));
    }

    /// <summary>Sources its state from elsewhere — pins that IsConnected follows an override.</summary>
    private sealed class AlwaysDisconnectedDouble : AdsConnectionBase
    {
        public override ConnectionState State => ConnectionState.Disconnected;
    }

    // =========================================================================
    // Every operation throws, and says which member on which double
    // =========================================================================

    public static TheoryData<string, Func<IAdsConnection, Task>> Operations() => new()
    {
        { nameof(IAdsConnection.ReadValueAsync), c => c.ReadValueAsync<int>("MAIN.X") },
        { nameof(IAdsConnection.ReadValueAsync), c => c.ReadValueAsync("MAIN.X") },
        { nameof(IAdsConnection.ReadValueWithMetadataAsync), c => c.ReadValueWithMetadataAsync("MAIN.X") },
        { nameof(IAdsConnection.WriteValueAsync), c => c.WriteValueAsync("MAIN.X", 1) },
        { nameof(IAdsConnection.WriteValueAsync), c => c.WriteValueAsync("MAIN.X", (object)1) },
        { nameof(IAdsConnection.ReadValuesAsync), c => c.ReadValuesAsync(["MAIN.X"]) },
        { nameof(IAdsConnection.WriteValuesAsync), c => c.WriteValuesAsync(new Dictionary<string, object?> { ["MAIN.X"] = 1 }) },
        { nameof(IAdsConnection.InvokeRpcMethodAsync), c => c.InvokeRpcMethodAsync("MAIN.FB", "M", []) },
        { nameof(IAdsConnection.GetEnumMembersAsync), c => c.GetEnumMembersAsync("E_Mode") },
        { nameof(IAdsConnection.GetAdsStateAsync), c => c.GetAdsStateAsync() },
        { nameof(IAdsConnection.GetDeviceInfoAsync), c => c.GetDeviceInfoAsync() },
        { nameof(IAdsConnection.WriteControlAsync), c => c.WriteControlAsync(AdsState.Run, 0) },
        { nameof(IAdsConnection.SubscribeAsync), c => c.SubscribeAsync("MAIN.X", 100, (_, _) => { }) },
        { nameof(IAdsConnection.SubscribeAsync), c => c.SubscribeAsync<int>("MAIN.X", 100, (_, _) => { }) },
        { nameof(IAdsConnection.SubscribeAsync), c => c.SubscribeAsync("MAIN.X", 100, (AdsNotification _) => { }) },
        { nameof(IAdsConnection.GetSymbolTreeAsync), c => c.GetSymbolTreeAsync(null) },
        { nameof(IAdsConnection.GetSymbolsAsync), c => c.GetSymbolsAsync(null, includeChildren: false) },
        { nameof(IAdsConnection.SearchSymbolsAsync), c => c.SearchSymbolsAsync("MAIN", includeChildren: false) },
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public void UnoverriddenOperation_ThrowsNamingTheDoubleAndTheMember(
        string member, Func<IAdsConnection, Task> operation)
    {
        // Discarded inside a statement lambda rather than returned from an expression one: these
        // throw OUT of the call, so the assertion is about an Action, not a faulted Task.
        var ex = Assert.Throws<NotSupportedException>(() => { _ = operation(new BareDouble()); });

        Assert.Contains($"{nameof(BareDouble)}.{member}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnoverriddenOperation_ThrowsSynchronously_RatherThanReturningAFaultedTask()
    {
        // A faulted Task would surface only at the await, in a stack trace that no longer names
        // the call site. Throwing out of the call keeps the missing override next to the caller.
        // This is the claim the theory above is silently making — named here so an edit that
        // moved the throw into a returned Task would fail a [Fact] that says why it matters.
        var conn = new BareDouble();

        Assert.Throws<NotSupportedException>(() => { _ = conn.ReadValueAsync<int>("MAIN.X"); });
    }

    [Fact]
    public void DeprecatedGetSymbolsOverload_AlsoThrows()
    {
#pragma warning disable CS0618 // Carried only so a derived double satisfies the interface.
        var ex = Assert.Throws<NotSupportedException>(() => { _ = new BareDouble().GetSymbolsAsync(null); });
#pragma warning restore CS0618

        Assert.Contains($"{nameof(BareDouble)}.{nameof(IAdsConnection.GetSymbolsAsync)}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Identity_Throws_BecauseThereIsNoHonestDefaultForIt()
    {
        var conn = new BareDouble();

        Assert.Throws<NotSupportedException>(() => conn.PlcId);
        Assert.Throws<NotSupportedException>(() => conn.DisplayName);
    }

    [Fact]
    public async Task OverridingOneMember_LeavesEveryOtherThrowing()
    {
        var conn = new ReadOnlyDouble();

        Assert.Equal(42, await conn.ReadValueAsync<int>("MAIN.X"));
        Assert.Throws<NotSupportedException>(() => { _ = conn.WriteValueAsync("MAIN.X", 1); });
        Assert.Throws<NotSupportedException>(() => { _ = conn.GetSymbolTreeAsync(null); });
    }

    // =========================================================================
    // The interface stays fakeable as it grows
    // =========================================================================

    [Fact]
    public void EveryInterfaceMember_IsOverridable()
    {
        var map = typeof(AdsConnectionBase).GetInterfaceMap(typeof(IAdsConnection));

        var sealedOff = map.InterfaceMethods
            .Zip(map.TargetMethods, (declared, implementation) => (declared, implementation))
            .Where(pair => !pair.implementation.IsVirtual || pair.implementation.IsFinal)
            // Event add/remove accessors are deliberately non-virtual: the event is field-like so
            // SetConnectionState can raise it, which an override would break rather than help.
            // Property getters are NOT exempt — they carry IsSpecialName too, and State/IsConnected
            // being overridable is part of the claim.
            .Where(pair => !(pair.declared.Name.StartsWith("add_", StringComparison.Ordinal)
                             || pair.declared.Name.StartsWith("remove_", StringComparison.Ordinal)))
            .Select(pair => pair.declared.Name)
            .ToArray();

        Assert.Empty(sealedOff);
    }

    [Fact]
    public void EveryInterfaceMember_HasADefault_SoNoDoubleIsForcedToImplementOne()
    {
        // BareDouble overrides nothing and compiles, which is the claim — asserted here rather
        // than left implicit so the reason the empty subclass exists survives a future edit.
        var abstractMembers = typeof(AdsConnectionBase)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .OfType<MethodBase>()
            .Where(m => m.IsAbstract)
            .Select(m => m.Name)
            .ToArray();

        Assert.Empty(abstractMembers);
    }

    // =========================================================================
    // State trio
    // =========================================================================

    [Fact]
    public void FreshDouble_IsConnected_BecauseADoubleThatAnswersCallsIs()
    {
        var conn = new BareDouble();

        Assert.Equal(ConnectionState.Connected, conn.State);
        Assert.True(conn.IsConnected);
    }

    [Fact]
    public void IsConnected_FollowsAnOverriddenState()
    {
        Assert.False(new AlwaysDisconnectedDouble().IsConnected);
    }

    [Fact]
    public void SetConnectionState_MovesTheStateAndRaisesTheTransition()
    {
        var conn = new IdentifiedDouble();
        var raised = new List<ConnectionStateChangedEventArgs>();
        conn.ConnectionStateChanged += (_, e) => raised.Add(e);

        conn.Move(ConnectionState.Disconnected);

        Assert.Equal(ConnectionState.Disconnected, conn.State);
        Assert.False(conn.IsConnected);

        var args = Assert.Single(raised);
        Assert.Equal("plc1", args.PlcId);
        Assert.Equal(ConnectionState.Disconnected, args.State);
        Assert.Equal(ConnectionState.Connected, args.PreviousState);
    }

    [Fact]
    public void SetConnectionState_ToTheStateAlreadyHeld_RaisesNothing()
    {
        var conn = new IdentifiedDouble();
        conn.Move(ConnectionState.Disconnected);

        var raised = 0;
        conn.ConnectionStateChanged += (_, _) => raised++;
        conn.Move(ConnectionState.Disconnected);

        Assert.Equal(0, raised);
    }

    [Fact]
    public void SetConnectionState_MakesTheNewStateVisibleToHandlers()
    {
        var conn = new IdentifiedDouble();
        ConnectionState? seen = null;
        bool? seenConnected = null;
        conn.ConnectionStateChanged += (_, _) =>
        {
            seen = conn.State;
            seenConnected = conn.IsConnected;
        };

        conn.Move(ConnectionState.Disconnected);

        Assert.Equal(ConnectionState.Disconnected, seen);
        Assert.False(seenConnected);
    }

    [Fact]
    public void SetConnectionState_RunsEveryHandlerEvenWhenOneThrows_AndSurfacesTheFailures()
    {
        var conn = new IdentifiedDouble();
        var after = false;
        conn.ConnectionStateChanged += (_, _) => throw new InvalidOperationException("handler one");
        conn.ConnectionStateChanged += (_, _) => after = true;

        var ex = Assert.Throws<AggregateException>(() => conn.Move(ConnectionState.Disconnected));

        Assert.True(after, "the handler after the throwing one must still run");
        Assert.Equal("handler one", Assert.IsType<InvalidOperationException>(Assert.Single(ex.InnerExceptions)).Message);

        // The transition still happened — a broken subscriber does not roll the state back.
        Assert.Equal(ConnectionState.Disconnected, conn.State);
    }

    [Fact]
    public void SetConnectionState_WithoutAPlcIdOverride_NeedsNoIdentityWhenNobodyIsSubscribed()
    {
        // Moving state so the code under test sees IsConnected go false is the common case, and
        // it tells nobody — so it should not force a PlcId override that has nothing to do with it.
        var conn = new StatefulBareDouble();

        conn.Move(ConnectionState.Disconnected);

        Assert.False(conn.IsConnected);
    }

    [Fact]
    public void SetConnectionState_WithoutAPlcIdOverride_SaysSo_OnceThereIsAHandlerToTell()
    {
        // The event args name the target, so a double that REPORTS a transition needs an identity.
        var conn = new StatefulBareDouble();
        conn.ConnectionStateChanged += (_, _) => { };

        var ex = Assert.Throws<NotSupportedException>(() => conn.Move(ConnectionState.Disconnected));

        Assert.Contains($"{nameof(StatefulBareDouble)}.{nameof(IAdsConnection.PlcId)}", ex.Message, StringComparison.Ordinal);

        // And it threw BEFORE the move, rather than landing in the new state having told nobody.
        Assert.Equal(ConnectionState.Connected, conn.State);
    }

    private sealed class StatefulBareDouble : AdsConnectionBase
    {
        public void Move(ConnectionState state) => SetConnectionState(state);
    }

    [Fact]
    public void SetConnectionState_RejectsAnUndefinedState()
    {
        var conn = new IdentifiedDouble();

        Assert.Throws<ArgumentOutOfRangeException>(() => conn.Move((ConnectionState)99));
    }

    [Fact]
    public void SetConnectionState_RaisesExactlyOncePerTransition_UnderConcurrentCallers()
    {
        var conn = new IdentifiedDouble();
        var disconnects = 0;
        conn.ConnectionStateChanged += (_, e) =>
        {
            if (e.State == ConnectionState.Disconnected)
                Interlocked.Increment(ref disconnects);
        };

        Parallel.For(0, 64, _ => conn.Move(ConnectionState.Disconnected));

        Assert.Equal(1, disconnects);
    }

    // =========================================================================
    // WithTimeout
    // =========================================================================

    [Fact]
    public void WithTimeout_ReturnsTheDoubleItself_BecauseADoubleHasNoBoundToChange()
    {
        var conn = new BareDouble();

        Assert.Same(conn, conn.WithTimeout(TimeSpan.FromSeconds(30)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WithTimeout_RejectsZeroAndNegative_AsARealConnectionDoes(int seconds)
    {
        var conn = new BareDouble();

        Assert.Throws<ArgumentOutOfRangeException>(() => conn.WithTimeout(TimeSpan.FromSeconds(seconds)));
    }
}
