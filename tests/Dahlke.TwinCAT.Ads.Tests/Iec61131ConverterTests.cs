using System.Globalization;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// <see cref="Iec61131Converter"/> — the table-driven IEC 61131-3 type-name domain
/// knowledge. These tests pin two tiers: the STRICT core (canonical uppercase names
/// only, case-sensitive) and the LENIENT <see cref="Iec61131Converter.Beckhoff"/> tier
/// (case-insensitive + Beckhoff aliases). They also pin that the core REJECTS what the
/// lenient tier accepts, proving the split.
/// </summary>
public class Iec61131ConverterTests
{
    // ---- Constants ----

    [Fact]
    public void Constants_EqualTheirNames()
    {
        Assert.Equal("BOOL", Iec61131Converter.BOOL);
        Assert.Equal("BYTE", Iec61131Converter.BYTE);
        Assert.Equal("WORD", Iec61131Converter.WORD);
        Assert.Equal("DWORD", Iec61131Converter.DWORD);
        Assert.Equal("LWORD", Iec61131Converter.LWORD);
        Assert.Equal("SINT", Iec61131Converter.SINT);
        Assert.Equal("INT", Iec61131Converter.INT);
        Assert.Equal("DINT", Iec61131Converter.DINT);
        Assert.Equal("LINT", Iec61131Converter.LINT);
        Assert.Equal("USINT", Iec61131Converter.USINT);
        Assert.Equal("UINT", Iec61131Converter.UINT);
        Assert.Equal("UDINT", Iec61131Converter.UDINT);
        Assert.Equal("ULINT", Iec61131Converter.ULINT);
        Assert.Equal("REAL", Iec61131Converter.REAL);
        Assert.Equal("LREAL", Iec61131Converter.LREAL);
        Assert.Equal("TIME", Iec61131Converter.TIME);
        Assert.Equal("DT", Iec61131Converter.DT);
        Assert.Equal("STRING", Iec61131Converter.STRING);
        Assert.Equal("WSTRING", Iec61131Converter.WSTRING);
    }

    // ---- Forward map: IEC name -> .NET Type ----

    [Theory]
    [InlineData("BOOL", typeof(bool))]
    [InlineData("BYTE", typeof(byte))]
    [InlineData("WORD", typeof(ushort))]
    [InlineData("DWORD", typeof(uint))]
    [InlineData("LWORD", typeof(ulong))]
    [InlineData("SINT", typeof(sbyte))]
    [InlineData("INT", typeof(short))]
    [InlineData("DINT", typeof(int))]
    [InlineData("LINT", typeof(long))]
    [InlineData("USINT", typeof(byte))]
    [InlineData("UINT", typeof(ushort))]
    [InlineData("UDINT", typeof(uint))]
    [InlineData("ULINT", typeof(ulong))]
    [InlineData("REAL", typeof(float))]
    [InlineData("LREAL", typeof(double))]
    [InlineData("TIME", typeof(TimeSpan))]
    [InlineData("DT", typeof(DateTime))]
    [InlineData("STRING", typeof(string))]
    [InlineData("WSTRING", typeof(string))]
    public void GetDotNetType_MapsEveryName(string iecName, Type expected)
    {
        Assert.Equal(expected, Iec61131Converter.GetDotNetType(iecName));
    }

    [Theory]
    [InlineData("BOOL", typeof(bool))]
    [InlineData("DINT", typeof(int))]
    [InlineData("WSTRING", typeof(string))]
    public void TryGetDotNetType_Known_ReturnsTrue(string iecName, Type expected)
    {
        Assert.True(Iec61131Converter.TryGetDotNetType(iecName, out var t));
        Assert.Equal(expected, t);
    }

    [Fact]
    public void TryGetDotNetType_Unknown_ReturnsFalse()
    {
        Assert.False(Iec61131Converter.TryGetDotNetType("NOPE", out var t));
        Assert.Null(t);
    }

    [Fact]
    public void GetDotNetType_Unknown_ThrowsArgumentExceptionNamingTheName()
    {
        var ex = Assert.Throws<ArgumentException>(() => Iec61131Converter.GetDotNetType("NOPE"));
        Assert.Contains("NOPE", ex.Message);
    }

    // ---- Reverse map: .NET Type -> canonical IEC name ----

    [Theory]
    [InlineData(typeof(bool), "BOOL")]
    [InlineData(typeof(sbyte), "SINT")]
    [InlineData(typeof(byte), "USINT")]
    [InlineData(typeof(short), "INT")]
    [InlineData(typeof(ushort), "UINT")]
    [InlineData(typeof(int), "DINT")]
    [InlineData(typeof(uint), "UDINT")]
    [InlineData(typeof(long), "LINT")]
    [InlineData(typeof(ulong), "ULINT")]
    [InlineData(typeof(float), "REAL")]
    [InlineData(typeof(double), "LREAL")]
    [InlineData(typeof(DateTime), "DT")]
    [InlineData(typeof(TimeSpan), "TIME")]
    [InlineData(typeof(string), "STRING")]
    public void GetIecTypeName_ReverseMapsEverySupportedType(Type dotNet, string expected)
    {
        Assert.Equal(expected, Iec61131Converter.GetIecTypeName(dotNet));
    }

    [Fact]
    public void GetIecTypeName_Unsupported_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => Iec61131Converter.GetIecTypeName(typeof(Guid)));
        Assert.Contains(nameof(Guid), ex.Message);
    }

    [Theory]
    [InlineData(typeof(int), "DINT")]
    [InlineData(typeof(bool), "BOOL")]
    [InlineData(typeof(double), "LREAL")]
    [InlineData(typeof(string), "STRING")]
    public void RoundTrip_ReverseThenForward_IsStableForCanonicalTypes(Type dotNet, string canonical)
    {
        var name = Iec61131Converter.GetIecTypeName(dotNet);
        Assert.Equal(canonical, name);
        Assert.Equal(dotNet, Iec61131Converter.GetDotNetType(name));
    }

    [Fact]
    public void ReverseMap_UnsignedIntegers_PickIntegerNotBitStringTypes()
    {
        // byte/ushort/uint/ulong reverse to the unsigned INTEGER IEC types,
        // NOT the bit-string types (BYTE/WORD/DWORD/LWORD) that also map forward.
        Assert.Equal("USINT", Iec61131Converter.GetIecTypeName(typeof(byte)));
        Assert.Equal("UINT", Iec61131Converter.GetIecTypeName(typeof(ushort)));
        Assert.Equal("UDINT", Iec61131Converter.GetIecTypeName(typeof(uint)));
        Assert.Equal("ULINT", Iec61131Converter.GetIecTypeName(typeof(ulong)));
    }

    // ---- Default values ----

    [Theory]
    [InlineData("BOOL", false)]
    [InlineData("BYTE", (byte)0)]
    [InlineData("WORD", (ushort)0)]
    [InlineData("DWORD", (uint)0)]
    [InlineData("LWORD", (ulong)0)]
    [InlineData("SINT", (sbyte)0)]
    [InlineData("INT", (short)0)]
    [InlineData("DINT", 0)]
    [InlineData("LINT", 0L)]
    [InlineData("USINT", (byte)0)]
    [InlineData("UINT", (ushort)0)]
    [InlineData("UDINT", (uint)0)]
    [InlineData("ULINT", (ulong)0)]
    [InlineData("REAL", 0f)]
    [InlineData("LREAL", 0d)]
    [InlineData("STRING", "")]
    [InlineData("WSTRING", "")]
    public void GetDefaultValue_ReturnsTypedDefault(string iecName, object expected)
    {
        var actual = Iec61131Converter.GetDefaultValue(iecName);
        Assert.Equal(expected, actual);
        Assert.Equal(expected.GetType(), actual!.GetType());
    }

    [Fact]
    public void GetDefaultValue_Time_IsTimeSpanZero()
    {
        Assert.Equal(TimeSpan.Zero, Iec61131Converter.GetDefaultValue("TIME"));
    }

    [Fact]
    public void GetDefaultValue_Dt_IsDateTimeMinValue()
    {
        Assert.Equal(DateTime.MinValue, Iec61131Converter.GetDefaultValue("DT"));
    }

    [Fact]
    public void GetDefaultValue_String_IsEmptyStringNotNull()
    {
        var d = Iec61131Converter.GetDefaultValue("STRING");
        Assert.NotNull(d);
        Assert.Equal("", d);
    }

    [Fact]
    public void GetDefaultValue_Unknown_ThrowsArgumentExceptionNotNull()
    {
        var ex = Assert.Throws<ArgumentException>(() => Iec61131Converter.GetDefaultValue("NOPE"));
        Assert.Contains("NOPE", ex.Message);
    }

    // ---- ConvertValue ----

    [Fact]
    public void ConvertValue_StringToDint_GivesBoxedInt()
    {
        var result = Iec61131Converter.ConvertValue("DINT", "42");
        Assert.Equal(42, result);
        Assert.IsType<int>(result);
    }

    [Fact]
    public void ConvertValue_StringToLreal_UsesInvariantCulture()
    {
        var result = Iec61131Converter.ConvertValue("LREAL", "3.14");
        Assert.Equal(3.14d, result);
        Assert.IsType<double>(result);
    }

    [Fact]
    public void ConvertValue_StringToLreal_DoesNotDependOnCurrentCulture()
    {
        // A culture that uses comma as the decimal separator must not change the result;
        // ConvertValue delegates to the core which always uses InvariantCulture.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var result = Iec61131Converter.ConvertValue("LREAL", "3.14");
            Assert.Equal(3.14d, result);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ConvertValue_StringTrue_ToBool()
    {
        Assert.Equal(true, Iec61131Converter.ConvertValue("BOOL", "true"));
    }

    [Fact]
    public void ConvertValue_IntOne_ToBool()
    {
        // The shared core uses Convert.ChangeType, which converts the integer 1 to true.
        // (Note: the string "1" is NOT a valid bool literal for Convert.ChangeType — only
        // numeric 1 / "true" / "false" convert — so this asserts the numeric path.)
        Assert.Equal(true, Iec61131Converter.ConvertValue("BOOL", 1));
    }

    [Fact]
    public void ConvertValue_Unknown_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => Iec61131Converter.ConvertValue("NOPE", "1"));
        Assert.Contains("NOPE", ex.Message);
    }

    // ---- Beckhoff (lenient tier) ----

    [Theory]
    [InlineData("dtSystemTime", typeof(DateTime))]
    [InlineData("T_UD", typeof(TimeSpan))]
    [InlineData("BIT", typeof(bool))]
    [InlineData("BIT8", typeof(bool))]
    public void Beckhoff_Aliases_Resolve(string alias, Type expected)
    {
        Assert.Equal(expected, Iec61131Converter.Beckhoff.GetDotNetType(alias));
    }

    [Theory]
    [InlineData("dint", typeof(int))]
    [InlineData("Int", typeof(short))]
    [InlineData("bool", typeof(bool))]
    [InlineData("lreal", typeof(double))]
    public void Beckhoff_CaseInsensitive_Resolves(string typeName, Type expected)
    {
        Assert.True(Iec61131Converter.Beckhoff.TryGetDotNetType(typeName, out var t));
        Assert.Equal(expected, t);
    }

    [Theory]
    [InlineData("bool", "BOOL")]
    [InlineData("Int", "INT")]
    [InlineData("dtSystemTime", "DT")]
    [InlineData("T_UD", "TIME")]
    [InlineData("BIT", "BOOL")]
    [InlineData("BIT8", "BOOL")]
    public void Beckhoff_Normalize_MapsToCanonical(string input, string canonical)
    {
        Assert.Equal(canonical, Iec61131Converter.Beckhoff.Normalize(input));
    }

    [Fact]
    public void Beckhoff_Normalize_Unknown_PassesThroughUnchanged()
    {
        Assert.Equal("MyCustomStruct", Iec61131Converter.Beckhoff.Normalize("MyCustomStruct"));
    }

    [Fact]
    public void Beckhoff_ConvertValue_CaseInsensitive()
    {
        var result = Iec61131Converter.Beckhoff.ConvertValue("dint", "42");
        Assert.Equal(42, result);
    }

    [Fact]
    public void Beckhoff_GetDefaultValue_Alias()
    {
        Assert.Equal(TimeSpan.Zero, Iec61131Converter.Beckhoff.GetDefaultValue("T_UD"));
    }

    [Fact]
    public void Beckhoff_GetDotNetType_UnknownAfterNormalize_Throws()
    {
        // Unknown passes through Normalize unchanged, then the strict core rejects it.
        Assert.Throws<ArgumentException>(() => Iec61131Converter.Beckhoff.GetDotNetType("MyCustomStruct"));
    }

    // ---- Tier split: strict core REJECTS what the lenient tier accepts ----

    [Fact]
    public void StrictCore_RejectsLowercase()
    {
        Assert.Throws<ArgumentException>(() => Iec61131Converter.GetDotNetType("dint"));
    }

    [Fact]
    public void StrictCore_RejectsAlias()
    {
        Assert.Throws<ArgumentException>(() => Iec61131Converter.GetDotNetType("BIT"));
        Assert.Throws<ArgumentException>(() => Iec61131Converter.GetDotNetType("dtSystemTime"));
        Assert.Throws<ArgumentException>(() => Iec61131Converter.GetDotNetType("T_UD"));
    }

    [Fact]
    public void StrictCore_TryGet_RejectsLowercase()
    {
        Assert.False(Iec61131Converter.TryGetDotNetType("bool", out _));
    }
}
