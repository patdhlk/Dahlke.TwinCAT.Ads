using Dahlke.TwinCAT.Ads.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using TwinCAT.Ads;
using TwinCAT.TypeSystem;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Pins <c>AdsConnection.GetEnumMembersAsync</c> end to end, without hardware, via the internal
/// <c>SetSymbolLoaderForTesting</c> seam — the same seam and reasoning as
/// <see cref="AdsConnectionSymbolBrowsingTests"/>.
/// </summary>
/// <remarks>
/// <see cref="AdsEnumMetadataTests"/> only exercises <see cref="SimulatedAdsConnection"/>'s
/// seedable map. Every behaviour the real implementation depends on — the <c>Convert.ToInt64</c>
/// widening, case-insensitive type lookup, the not-an-enum path, the not-found path, and the
/// cache — is pinned here instead, against a faked <c>IDynamicSymbolLoader.DataTypes</c>. Without
/// this class, a bug in the real resolution path would still pass every existing test and surface
/// only against hardware — the exact failure mode this whole feature exists to prevent for its own
/// callers.
/// </remarks>
public class AdsConnectionEnumMetadataTests
{
    private static AdsConnection CreateConnection(params IDataType[] dataTypes)
    {
        var options = new PlcTargetOptions { DisplayName = "PLC 1", TimeoutMs = 3000 };
        var connection = new AdsConnection("plc1", options, new NullLoggerFactory());
        connection.SetSymbolLoaderForTesting(new FakeDynamicSymbolLoader(dataTypes, []));
        return connection;
    }

    [Fact]
    public async Task IntBackedEnum_Resolves_WithCorrectValues()
    {
        // Int16-backed (INT) values prove Convert.ToInt64 widens a signed 16-bit value correctly
        // rather than by coincidence (an unsigned-only conversion path would still pass byte/ushort
        // cases but get this one wrong for a negative or >32767 value).
        var enumType = new FakeEnumType("deaReturnType",
            [new FakeEnumValue("SUCCESS", (short)0), new FakeEnumValue("ERROR", (short)1)]);
        using var connection = CreateConnection(enumType);

        var members = await connection.GetEnumMembersAsync("deaReturnType", CancellationToken.None);

        Assert.Equal(2, members.Count);
        Assert.Equal("SUCCESS", members[0].Name);
        Assert.Equal(0L, members[0].Value);
        Assert.Equal("ERROR", members[1].Name);
        Assert.Equal(1L, members[1].Value);
    }

    [Fact]
    public async Task UnsignedBackedEnum_Resolves_WithCorrectValues()
    {
        // USINT (byte)-backed, matching the brief's own reference-rack example numbering.
        var enumType = new FakeEnumType("deaReturnType",
            [new FakeEnumValue("ERROR", (byte)0), new FakeEnumValue("SUCCESS", (byte)5)]);
        using var connection = CreateConnection(enumType);

        var members = await connection.GetEnumMembersAsync("deaReturnType", CancellationToken.None);

        Assert.Equal(2, members.Count);
        Assert.Equal("ERROR", members[0].Name);
        Assert.Equal(0L, members[0].Value);
        Assert.Equal("SUCCESS", members[1].Name);
        Assert.Equal(5L, members[1].Value);
    }

    [Fact]
    public async Task MultipleDistinctTypes_ResolveIndependently_ByName()
    {
        // Closes the gap the brief's own seeded tests left open: a single-type test can't tell
        // "resolved the requested type" apart from "ignored typeName and returned whatever was
        // seeded". Two distinct types, each asserted by its own name, can.
        var first = new FakeEnumType("deaReturnType", [new FakeEnumValue("SUCCESS", (short)0)]);
        var second = new FakeEnumType("deaAlarmClass", [new FakeEnumValue("WARNING", (short)7)]);
        using var connection = CreateConnection(first, second);

        var firstMembers = await connection.GetEnumMembersAsync("deaReturnType", CancellationToken.None);
        var secondMembers = await connection.GetEnumMembersAsync("deaAlarmClass", CancellationToken.None);

        Assert.Equal("SUCCESS", Assert.Single(firstMembers).Name);
        Assert.Equal("WARNING", Assert.Single(secondMembers).Name);
        Assert.Equal(7L, secondMembers[0].Value);
    }

    [Fact]
    public async Task TypeNameLookup_IsCaseInsensitive()
    {
        var enumType = new FakeEnumType("deaReturnType", [new FakeEnumValue("SUCCESS", (short)0)]);
        using var connection = CreateConnection(enumType);

        var members = await connection.GetEnumMembersAsync("DEARETURNTYPE", CancellationToken.None);

        Assert.Single(members);
        Assert.Equal("SUCCESS", members[0].Name);
    }

    [Fact]
    public async Task ResolvedButNotAnEnum_Throws_NamingTheType()
    {
        var structType = new FakeNonEnumDataType("ST_Motor", DataTypeCategory.Struct);
        using var connection = CreateConnection(structType);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => connection.GetEnumMembersAsync("ST_Motor", CancellationToken.None));

        Assert.Contains("ST_Motor", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnresolvedType_Throws_DeviceSymbolNotFound()
    {
        using var connection = CreateConnection(); // no data types registered

        var ex = await Assert.ThrowsAsync<AdsErrorException>(
            () => connection.GetEnumMembersAsync("NoSuchType", CancellationToken.None));

        Assert.Equal(AdsErrorCode.DeviceSymbolNotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task SecondCall_ForSameType_DoesNotReResolve()
    {
        var enumType = new FakeEnumType("deaReturnType", [new FakeEnumValue("SUCCESS", (short)0)]);
        var loader = new FakeDynamicSymbolLoader([enumType], []);
        var options = new PlcTargetOptions { DisplayName = "PLC 1", TimeoutMs = 3000 };
        using var connection = new AdsConnection("plc1", options, new NullLoggerFactory());
        connection.SetSymbolLoaderForTesting(loader);

        await connection.GetEnumMembersAsync("deaReturnType", CancellationToken.None);
        await connection.GetEnumMembersAsync("deaReturnType", CancellationToken.None);

        var dataTypes = (FakeDataTypeCollection)loader.DataTypes;
        Assert.Equal(1, dataTypes.EnumerationCount);
    }

    /// <summary>
    /// Reproduces the cache-write race deterministically, on one thread: a Disconnect happening
    /// WHILE a resolve is in flight must not let that resolve's result land in the cache after the
    /// clear. <see cref="FakeEnumValueCollection.OnEnumerated"/> fires synchronously from inside
    /// the resolve (during <c>.Select(...).ToArray()</c>, before the cache write), so calling
    /// <see cref="AdsConnection.Disconnect"/> from it faithfully simulates "a disconnect landed
    /// mid-resolve" without any real concurrency or timing dependency.
    /// </summary>
    [Fact]
    public async Task ConcurrentDisconnect_DuringResolve_DoesNotCacheStaleMembers()
    {
        var options = new PlcTargetOptions { DisplayName = "PLC 1", TimeoutMs = 3000 };
        using var connection = new AdsConnection("plc1", options, new NullLoggerFactory());

        var raceValues = new FakeEnumValueCollection([new FakeEnumValue("OLD", 0)]);
        raceValues.OnEnumerated = connection.Disconnect;
        var raceEnumType = new FakeEnumType("MyEnum", raceValues);
        connection.SetSymbolLoaderForTesting(new FakeDynamicSymbolLoader([raceEnumType], []));

        var first = await connection.GetEnumMembersAsync("MyEnum", CancellationToken.None);
        Assert.Equal("OLD", first[0].Name); // the in-flight resolve still serves its own caller

        // "Reconnect" with fresh metadata standing in for what a PLC download changed.
        var freshEnumType = new FakeEnumType("MyEnum", [new FakeEnumValue("NEW", 1)]);
        connection.SetSymbolLoaderForTesting(new FakeDynamicSymbolLoader([freshEnumType], []));

        var second = await connection.GetEnumMembersAsync("MyEnum", CancellationToken.None);

        // Without the generation guard, the raced first call would have written "OLD" into the
        // cache AFTER Disconnect's clear, and this second call would hit that stale cache entry
        // instead of resolving fresh — returning "OLD" again rather than "NEW".
        Assert.Equal("NEW", second[0].Name);
    }
}
