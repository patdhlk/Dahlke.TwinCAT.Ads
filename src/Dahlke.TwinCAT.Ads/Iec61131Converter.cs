namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Table-driven mapping between IEC 61131-3 elementary type names and their .NET
/// representations, plus default-value lookup and value conversion. This is the
/// <b>strict, standard</b> tier: the public members here recognise <em>only</em> the
/// canonical uppercase IEC names (<c>BOOL</c>, <c>DINT</c>, <c>LREAL</c>, …) and match
/// them case-sensitively. Anything else — lowercase, mixed case, or a Beckhoff-specific
/// alias — is rejected. Callers that need lenient, alias-aware, case-insensitive
/// resolution use the nested <see cref="Beckhoff"/> tier, which normalises its input
/// and then delegates to this core.
/// </summary>
/// <remarks>
/// <para>
/// <b>Forward vs. reverse ambiguity.</b> The IEC bit-string types and the unsigned
/// integer types share the same .NET representation: <c>BYTE</c> and <c>USINT</c> both
/// map forward to <see cref="byte"/>, <c>WORD</c> and <c>UINT</c> both to
/// <see cref="ushort"/>, <c>DWORD</c> and <c>UDINT</c> both to <see cref="uint"/>, and
/// <c>LWORD</c> and <c>ULINT</c> both to <see cref="ulong"/>. The forward map
/// (<see cref="GetDotNetType"/>) accepts all of them. The reverse map
/// (<see cref="GetIecTypeName"/>) is deterministic: for an unsigned .NET integer it picks
/// the unsigned <em>integer</em> IEC type (<c>USINT</c>/<c>UINT</c>/<c>UDINT</c>/<c>ULINT</c>),
/// never the bit-string type. Likewise <c>STRING</c> and <c>WSTRING</c> both map forward
/// to <see cref="string"/>; the reverse map picks <c>STRING</c>.
/// </para>
/// <para>
/// Conversion is delegated to <see cref="AdsValueConverter.ConvertForRead(object?, System.Type, string)"/>,
/// the single shared conversion core, so an IEC-typed conversion interprets a stored value
/// exactly as a typed read does (invariant-culture <see cref="System.Convert.ChangeType(object, System.Type, System.IFormatProvider)"/>
/// after a direct-assignable fast path).
/// </para>
/// </remarks>
public static class Iec61131Converter
{
    /// <summary>Boolean. Maps to <see cref="bool"/>.</summary>
    public const string BOOL = "BOOL";

    /// <summary>8-bit bit string. Maps forward to <see cref="byte"/> (reverse picks <see cref="USINT"/>).</summary>
    public const string BYTE = "BYTE";

    /// <summary>16-bit bit string. Maps forward to <see cref="ushort"/> (reverse picks <see cref="UINT"/>).</summary>
    public const string WORD = "WORD";

    /// <summary>32-bit bit string. Maps forward to <see cref="uint"/> (reverse picks <see cref="UDINT"/>).</summary>
    public const string DWORD = "DWORD";

    /// <summary>64-bit bit string. Maps forward to <see cref="ulong"/> (reverse picks <see cref="ULINT"/>).</summary>
    public const string LWORD = "LWORD";

    /// <summary>Signed 8-bit integer. Maps to <see cref="sbyte"/>.</summary>
    public const string SINT = "SINT";

    /// <summary>Signed 16-bit integer. Maps to <see cref="short"/>.</summary>
    public const string INT = "INT";

    /// <summary>Signed 32-bit integer. Maps to <see cref="int"/>.</summary>
    public const string DINT = "DINT";

    /// <summary>Signed 64-bit integer. Maps to <see cref="long"/>.</summary>
    public const string LINT = "LINT";

    /// <summary>Unsigned 8-bit integer. Maps to <see cref="byte"/>.</summary>
    public const string USINT = "USINT";

    /// <summary>Unsigned 16-bit integer. Maps to <see cref="ushort"/>.</summary>
    public const string UINT = "UINT";

    /// <summary>Unsigned 32-bit integer. Maps to <see cref="uint"/>.</summary>
    public const string UDINT = "UDINT";

    /// <summary>Unsigned 64-bit integer. Maps to <see cref="ulong"/>.</summary>
    public const string ULINT = "ULINT";

    /// <summary>32-bit IEEE-754 floating point. Maps to <see cref="float"/>.</summary>
    public const string REAL = "REAL";

    /// <summary>64-bit IEEE-754 floating point. Maps to <see cref="double"/>.</summary>
    public const string LREAL = "LREAL";

    /// <summary>Duration. Maps to <see cref="System.TimeSpan"/>.</summary>
    public const string TIME = "TIME";

    /// <summary>Date and time of day. Maps to <see cref="System.DateTime"/>.</summary>
    public const string DT = "DT";

    /// <summary>Single-byte character string. Maps to <see cref="string"/>.</summary>
    public const string STRING = "STRING";

    /// <summary>Wide (Unicode) character string. Maps forward to <see cref="string"/> (reverse picks <see cref="STRING"/>).</summary>
    public const string WSTRING = "WSTRING";

    /// <summary>
    /// Forward map: canonical IEC name (case-sensitive, uppercase) to .NET <see cref="System.Type"/>.
    /// Includes both the bit-string types and the unsigned-integer types that share a .NET type.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Type> ForwardMap = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        [BOOL] = typeof(bool),
        [BYTE] = typeof(byte),
        [WORD] = typeof(ushort),
        [DWORD] = typeof(uint),
        [LWORD] = typeof(ulong),
        [SINT] = typeof(sbyte),
        [INT] = typeof(short),
        [DINT] = typeof(int),
        [LINT] = typeof(long),
        [USINT] = typeof(byte),
        [UINT] = typeof(ushort),
        [UDINT] = typeof(uint),
        [ULINT] = typeof(ulong),
        [REAL] = typeof(float),
        [LREAL] = typeof(double),
        [TIME] = typeof(TimeSpan),
        [DT] = typeof(DateTime),
        [STRING] = typeof(string),
        [WSTRING] = typeof(string),
    };

    /// <summary>
    /// Reverse map: .NET <see cref="System.Type"/> to a single deterministic canonical IEC name.
    /// Unsigned integers resolve to the unsigned-integer IEC types (not the bit-string types);
    /// <see cref="string"/> resolves to <see cref="STRING"/> (not <see cref="WSTRING"/>).
    /// </summary>
    private static readonly IReadOnlyDictionary<Type, string> ReverseMap = new Dictionary<Type, string>
    {
        [typeof(bool)] = BOOL,
        [typeof(sbyte)] = SINT,
        [typeof(byte)] = USINT,
        [typeof(short)] = INT,
        [typeof(ushort)] = UINT,
        [typeof(int)] = DINT,
        [typeof(uint)] = UDINT,
        [typeof(long)] = LINT,
        [typeof(ulong)] = ULINT,
        [typeof(float)] = REAL,
        [typeof(double)] = LREAL,
        [typeof(DateTime)] = DT,
        [typeof(TimeSpan)] = TIME,
        [typeof(string)] = STRING,
    };

    /// <summary>
    /// Default boxed value per canonical IEC name. String types default to the empty
    /// string (never <see langword="null"/>), <see cref="DT"/> to <see cref="System.DateTime.MinValue"/>,
    /// and <see cref="TIME"/> to <see cref="System.TimeSpan.Zero"/>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, object?> DefaultValueMap = new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        [BOOL] = false,
        [BYTE] = (byte)0,
        [WORD] = (ushort)0,
        [DWORD] = 0u,
        [LWORD] = 0ul,
        [SINT] = (sbyte)0,
        [INT] = (short)0,
        [DINT] = 0,
        [LINT] = 0L,
        [USINT] = (byte)0,
        [UINT] = (ushort)0,
        [UDINT] = 0u,
        [ULINT] = 0ul,
        [REAL] = 0f,
        [LREAL] = 0d,
        [TIME] = TimeSpan.Zero,
        [DT] = DateTime.MinValue,
        [STRING] = "",
        [WSTRING] = "",
    };

    /// <summary>
    /// Attempts to resolve a canonical IEC type name to its .NET <see cref="System.Type"/>.
    /// Strict: the name must be a canonical uppercase IEC name and is matched case-sensitively.
    /// </summary>
    /// <param name="iecTypeName">The canonical IEC 61131-3 type name (e.g. <c>"DINT"</c>).</param>
    /// <param name="dotNetType">When this method returns <see langword="true"/>, the mapped .NET type; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the name is a recognised canonical IEC type; otherwise <see langword="false"/>.</returns>
    public static bool TryGetDotNetType(string iecTypeName, out Type? dotNetType)
        => ForwardMap.TryGetValue(iecTypeName, out dotNetType);

    /// <summary>
    /// Resolves a canonical IEC type name to its .NET <see cref="System.Type"/>.
    /// Strict: the name must be a canonical uppercase IEC name and is matched case-sensitively.
    /// </summary>
    /// <param name="iecTypeName">The canonical IEC 61131-3 type name (e.g. <c>"DINT"</c>).</param>
    /// <returns>The mapped .NET <see cref="System.Type"/>.</returns>
    /// <exception cref="System.ArgumentException">The name is not a recognised canonical IEC type.</exception>
    public static Type GetDotNetType(string iecTypeName)
        => ForwardMap.TryGetValue(iecTypeName, out var t)
            ? t
            : throw new ArgumentException(
                $"Unknown IEC 61131-3 type name '{iecTypeName}'. Expected a canonical uppercase " +
                $"elementary type name such as 'BOOL', 'DINT', or 'LREAL'.",
                nameof(iecTypeName));

    /// <summary>
    /// Reverse-resolves a .NET <see cref="System.Type"/> to its canonical IEC type name.
    /// Deterministic for the ambiguous cases: unsigned integers pick the unsigned-integer
    /// IEC types (not the bit-string types) and <see cref="string"/> picks <see cref="STRING"/>.
    /// </summary>
    /// <param name="dotNetType">The .NET type to reverse-map.</param>
    /// <returns>The canonical IEC 61131-3 type name.</returns>
    /// <exception cref="System.ArgumentException">The .NET type has no IEC mapping.</exception>
    public static string GetIecTypeName(Type dotNetType)
        => ReverseMap.TryGetValue(dotNetType, out var name)
            ? name
            : throw new ArgumentException(
                $"Unsupported .NET type '{dotNetType.Name}'. No IEC 61131-3 elementary type maps to it.",
                nameof(dotNetType));

    /// <summary>
    /// Returns the default boxed value for a canonical IEC type name. String types return
    /// the empty string (never <see langword="null"/>).
    /// </summary>
    /// <param name="iecTypeName">The canonical IEC 61131-3 type name.</param>
    /// <returns>The default boxed value for the type.</returns>
    /// <exception cref="System.ArgumentException">
    /// The name is not a recognised canonical IEC type. An unknown name throws rather than
    /// returning <see langword="null"/>, because a <see langword="null"/> result would look
    /// like a legitimate default and hide the error.
    /// </exception>
    public static object? GetDefaultValue(string iecTypeName)
        => DefaultValueMap.TryGetValue(iecTypeName, out var value)
            ? value
            : throw new ArgumentException(
                $"Unknown IEC 61131-3 type name '{iecTypeName}'. Cannot produce a default value.",
                nameof(iecTypeName));

    /// <summary>
    /// Converts <paramref name="rawValue"/> to the .NET type of the given canonical IEC type
    /// name, delegating to <see cref="AdsValueConverter.ConvertForRead(object?, System.Type, string)"/>
    /// — the single shared conversion core (direct-assignable fast path then
    /// invariant-culture <see cref="System.Convert.ChangeType(object, System.Type, System.IFormatProvider)"/>).
    /// </summary>
    /// <param name="iecTypeName">The canonical IEC 61131-3 type name.</param>
    /// <param name="rawValue">The boxed value to convert.</param>
    /// <returns>The converted boxed value.</returns>
    /// <exception cref="System.ArgumentException">The name is not a recognised canonical IEC type.</exception>
    /// <exception cref="System.InvalidCastException">The value cannot be converted to the resolved .NET type.</exception>
    public static object? ConvertValue(string iecTypeName, object? rawValue)
        => AdsValueConverter.ConvertForRead(rawValue, GetDotNetType(iecTypeName), $"IEC type '{iecTypeName}'");

    /// <summary>
    /// The <b>lenient, Beckhoff-aware</b> tier layered over the strict
    /// <see cref="Iec61131Converter"/> core. Unlike the core, every method here first
    /// <see cref="Normalize"/>s its input — matching standard IEC names case-insensitively
    /// (so <c>"bool"</c> and <c>"Int"</c> resolve) and translating Beckhoff/non-standard
    /// aliases (<c>dtSystemTime</c>, <c>T_UD</c>, <c>BIT</c>, <c>BIT8</c>) to canonical
    /// names — and then delegates to the corresponding core method. Use this tier when
    /// consuming type names from TwinCAT/Beckhoff metadata; use the core directly when you
    /// require strict standard names.
    /// </summary>
    public static class Beckhoff
    {
        /// <summary>
        /// Maps an input type name to a canonical IEC name. Standard names are matched
        /// case-insensitively (<c>"bool"</c>, <c>"Int"</c> → <c>"BOOL"</c>, <c>"INT"</c>);
        /// the Beckhoff/non-standard aliases <c>dtSystemTime</c> → <see cref="DT"/>,
        /// <c>T_UD</c> → <see cref="TIME"/>, <c>BIT</c> → <see cref="BOOL"/>, and
        /// <c>BIT8</c> → <see cref="BOOL"/> are translated. An unrecognised input is
        /// returned unchanged so the downstream strict core produces the actionable error.
        /// </summary>
        /// <param name="typeName">The input type name (any case, possibly an alias).</param>
        /// <returns>The canonical IEC name, or the original input when unrecognised.</returns>
        public static string Normalize(string typeName)
        {
            if (AliasMap.TryGetValue(typeName, out var canonical))
                return canonical;

            // Case-insensitive match against the canonical standard names.
            if (CaseInsensitiveStandardMap.TryGetValue(typeName, out var standard))
                return standard;

            // Unrecognised: pass through unchanged; the strict core will reject it.
            return typeName;
        }

        /// <summary>
        /// Beckhoff/non-standard aliases (case-insensitive) to canonical IEC names.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, string> AliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["dtSystemTime"] = DT,
            ["T_UD"] = TIME,
            ["BIT"] = BOOL,
            ["BIT8"] = BOOL,
        };

        /// <summary>
        /// Case-insensitive lookup from any casing of a canonical name back to its canonical form.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, string> CaseInsensitiveStandardMap = BuildCaseInsensitiveStandardMap();

        private static IReadOnlyDictionary<string, string> BuildCaseInsensitiveStandardMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var canonical in ForwardMap.Keys)
                map[canonical] = canonical;
            return map;
        }

        /// <summary>
        /// <see cref="Normalize"/>s <paramref name="typeName"/>, then attempts the strict
        /// <see cref="Iec61131Converter.TryGetDotNetType"/> lookup.
        /// </summary>
        /// <param name="typeName">The input type name (any case, possibly an alias).</param>
        /// <param name="dotNetType">When this method returns <see langword="true"/>, the mapped .NET type; otherwise <see langword="null"/>.</param>
        /// <returns><see langword="true"/> if the normalised name resolves; otherwise <see langword="false"/>.</returns>
        public static bool TryGetDotNetType(string typeName, out Type? dotNetType)
            => Iec61131Converter.TryGetDotNetType(Normalize(typeName), out dotNetType);

        /// <summary>
        /// <see cref="Normalize"/>s <paramref name="typeName"/>, then delegates to the strict
        /// <see cref="Iec61131Converter.GetDotNetType"/>.
        /// </summary>
        /// <param name="typeName">The input type name (any case, possibly an alias).</param>
        /// <returns>The mapped .NET <see cref="System.Type"/>.</returns>
        /// <exception cref="System.ArgumentException">The normalised name is not a recognised IEC type.</exception>
        public static Type GetDotNetType(string typeName)
            => Iec61131Converter.GetDotNetType(Normalize(typeName));

        /// <summary>
        /// <see cref="Normalize"/>s <paramref name="typeName"/>, then delegates to the strict
        /// <see cref="Iec61131Converter.GetDefaultValue"/>.
        /// </summary>
        /// <param name="typeName">The input type name (any case, possibly an alias).</param>
        /// <returns>The default boxed value for the resolved type.</returns>
        /// <exception cref="System.ArgumentException">The normalised name is not a recognised IEC type.</exception>
        public static object? GetDefaultValue(string typeName)
            => Iec61131Converter.GetDefaultValue(Normalize(typeName));

        /// <summary>
        /// <see cref="Normalize"/>s <paramref name="typeName"/>, then delegates to the strict
        /// <see cref="Iec61131Converter.ConvertValue"/>.
        /// </summary>
        /// <param name="typeName">The input type name (any case, possibly an alias).</param>
        /// <param name="rawValue">The boxed value to convert.</param>
        /// <returns>The converted boxed value.</returns>
        /// <exception cref="System.ArgumentException">The normalised name is not a recognised IEC type.</exception>
        /// <exception cref="System.InvalidCastException">The value cannot be converted to the resolved .NET type.</exception>
        public static object? ConvertValue(string typeName, object? rawValue)
            => Iec61131Converter.ConvertValue(Normalize(typeName), rawValue);
    }
}
