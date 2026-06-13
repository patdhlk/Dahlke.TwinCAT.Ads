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
}
