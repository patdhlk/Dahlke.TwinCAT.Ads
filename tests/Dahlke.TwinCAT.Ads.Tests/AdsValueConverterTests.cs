using Microsoft.Extensions.Logging.Abstractions;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// <see cref="AdsValueConverter"/> — the shared conversion core used by both the
/// typed read path (which THROWS on failure) and the typed-notification path (which
/// DROPS on failure). These tests pin the drop-semantics entry point
/// (<see cref="AdsValueConverter.TryConvertForNotification{T}"/>) that
/// <see cref="TypedCallbackAdapter"/> relies on; the throwing read path is covered by
/// the typed-read tests, which must remain green after the refactor.
/// </summary>
public class AdsValueConverterTests
{
    [Fact]
    public void TryConvert_ExactType_ReturnsValue()
    {
        var ok = AdsValueConverter.TryConvertForNotification<int>(42, "A.x", NullLogger.Instance, out var result);
        Assert.True(ok);
        Assert.Equal(42, result);
    }

    [Fact]
    public void TryConvert_StringToInt_Converts()
    {
        var ok = AdsValueConverter.TryConvertForNotification<int>("42", "A.x", NullLogger.Instance, out var result);
        Assert.True(ok);
        Assert.Equal(42, result);
    }

    [Fact]
    public void TryConvert_IntToDouble_Widens()
    {
        var ok = AdsValueConverter.TryConvertForNotification<double>(7, "A.x", NullLogger.Instance, out var result);
        Assert.True(ok);
        Assert.Equal(7.0, result);
    }

    [Fact]
    public void TryConvert_Incompatible_ReturnsFalse()
    {
        var ok = AdsValueConverter.TryConvertForNotification<int>(Guid.NewGuid(), "A.x", NullLogger.Instance, out var result);
        Assert.False(ok);
        Assert.Equal(default, result);
    }

    [Fact]
    public void TryConvert_NullWithValueType_ReturnsFalse()
    {
        var ok = AdsValueConverter.TryConvertForNotification<int>(null, "A.x", NullLogger.Instance, out var result);
        Assert.False(ok);
        Assert.Equal(default, result);
    }

    [Fact]
    public void TryConvert_NullWithReferenceType_ReturnsTrueNull()
    {
        var ok = AdsValueConverter.TryConvertForNotification<string>(null, "A.x", NullLogger.Instance, out var result);
        Assert.True(ok);
        Assert.Null(result);
    }

    [Fact]
    public void TryConvert_NullWithNullableValueType_ReturnsTrueNull()
    {
        var ok = AdsValueConverter.TryConvertForNotification<int?>(null, "A.x", NullLogger.Instance, out var result);
        Assert.True(ok);
        Assert.Null(result);
    }

    [Fact]
    public void TryConvert_NullLogger_DoesNotThrow()
    {
        // logger is allowed to be null (the converter must null-check before logging).
        var ok = AdsValueConverter.TryConvertForNotification<int>(Guid.NewGuid(), "A.x", null, out _);
        Assert.False(ok);
    }

    // ---- Read-path message wording: "Symbol", not "Simulated symbol" ----
    // ConvertForRead is now called on real connections too, so its actionable
    // InvalidCastException messages must not claim the symbol is simulated.

    [Fact]
    public void ConvertForRead_NullValueType_MessageSaysSymbol_NotSimulated()
    {
        var ex = Assert.Throws<InvalidCastException>(
            () => AdsValueConverter.ConvertForRead<int>(null, "X.y"));
        Assert.Contains("Symbol 'X.y'", ex.Message);
        Assert.DoesNotContain("Simulated symbol", ex.Message);
    }

    [Fact]
    public void ConvertForRead_IncompatibleType_MessageSaysSymbol_NotSimulated()
    {
        var ex = Assert.Throws<InvalidCastException>(
            () => AdsValueConverter.ConvertForRead<int>(Guid.NewGuid(), "X.y"));
        Assert.Contains("Symbol 'X.y'", ex.Message);
        Assert.DoesNotContain("Simulated symbol", ex.Message);
    }

    // ---- Non-generic entry point: ConvertForRead(object?, Type, string) ----
    // Callers that only know the target type at runtime convert via this core;
    // the generic ConvertForRead<T> is now a thin wrapper over it.

    [Fact]
    public void ConvertForRead_NonGeneric_StringToInt_ReturnsBoxedInt()
    {
        var result = AdsValueConverter.ConvertForRead("42", typeof(int), "A.x");
        Assert.Equal(42, result);
        Assert.IsType<int>(result);
    }

    [Fact]
    public void ConvertForRead_NonGeneric_NullWithValueType_ThrowsWithSymbolContext()
    {
        var ex = Assert.Throws<InvalidCastException>(
            () => AdsValueConverter.ConvertForRead(null, typeof(int), "A.x"));
        Assert.Contains("Symbol 'A.x'", ex.Message);
    }

    [Fact]
    public void ConvertForRead_NonGeneric_NullWithReferenceType_ReturnsNull()
    {
        var result = AdsValueConverter.ConvertForRead(null, typeof(string), "A.x");
        Assert.Null(result);
    }

    // =========================================================================
    // .NET enums. A PLC enum arrives as its backing integer; Convert.ChangeType
    // cannot produce an enum from anything, so this used to fail for every PLC
    // enum read or bound as a .NET enum.
    // =========================================================================

    public enum PackMLState : short { Stopped = 2, Idle = 4, Execute = 6 }

    [Fact]
    public void ConvertForRead_Int16ToEnum_ConvertsByNumber()
    {
        Assert.Equal(PackMLState.Execute, AdsValueConverter.ConvertForRead<PackMLState>((short)6, "A.eState"));
    }

    [Fact]
    public void ConvertForRead_WiderIntegerToEnum_ConvertsThroughTheBackingType()
    {
        Assert.Equal(PackMLState.Idle, AdsValueConverter.ConvertForRead<PackMLState>(4, "A.eState"));
    }

    [Fact]
    public void ConvertForRead_UndeclaredValue_ConvertsLikeACast()
    {
        // A newer PLC may know a state this HMI does not: a C# cast keeps the number, and so does this.
        Assert.Equal((PackMLState)17, AdsValueConverter.ConvertForRead<PackMLState>((short)17, "A.eState"));
    }

    [Fact]
    public void ConvertForRead_MemberNameToEnum_ConvertsCaseInsensitively()
    {
        Assert.Equal(PackMLState.Idle, AdsValueConverter.ConvertForRead<PackMLState>("idle", "A.eState"));
    }

    [Fact]
    public void ConvertForRead_Int16ToNullableEnum_Converts()
    {
        Assert.Equal(PackMLState.Stopped, AdsValueConverter.ConvertForRead<PackMLState?>((short)2, "A.eState"));
    }

    [Theory]
    [InlineData(70000)]    // does not fit the INT backing type
    [InlineData(4.5)]      // not an integral value
    [InlineData("Unknown")]
    public void ConvertForRead_UnconvertibleToEnum_ThrowsWithSymbolContext(object value)
    {
        var ex = Assert.Throws<InvalidCastException>(() => AdsValueConverter.ConvertForRead<PackMLState>(value, "A.eState"));
        Assert.Contains("A.eState", ex.Message);
    }

    [Fact]
    public void TryConvert_EnumNotification_Converts()
    {
        var ok = AdsValueConverter.TryConvertForNotification<PackMLState>((short)4, "A.eState", NullLogger.Instance, out var result);
        Assert.True(ok);
        Assert.Equal(PackMLState.Idle, result);
    }

    public sealed record LineStatus(PackMLState EState, float RSpeed);

    [Fact]
    public void WrapDecoded_BindsAStructWithAnEnumMemberFromTheDecodedTree()
    {
        // The shape the decoding subscription overload delivers for a PLC struct: a tree by member
        // name, enums as their backing INT. A typed subscription binds it; through the untyped
        // overload it received Beckhoff's own container value and dropped every notification.
        LineStatus? received = null;
        var callback = TypedCallbackAdapter.WrapDecoded<LineStatus>((_, s) => received = s, NullLogger.Instance);

        callback(new AdsNotification("MAIN.stLine",
            new Dictionary<string, object?> { ["eState"] = (short)6, ["rSpeed"] = 3000f },
            "ST_LineStatus", DateTimeOffset.FromUnixTimeSeconds(0)));

        Assert.Equal(new LineStatus(PackMLState.Execute, 3000f), received);
    }
}
