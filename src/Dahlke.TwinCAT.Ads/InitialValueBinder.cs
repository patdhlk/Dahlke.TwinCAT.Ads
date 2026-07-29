using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Re-binds <see cref="PlcTargetOptions.InitialValues"/> from raw configuration so that
/// config-seeded simulation values keep a PLC-faithful CLR type.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <see cref="IConfiguration"/> is string-typed all the way down: a JSON
/// <c>1500</c>, <c>21.5</c> and <c>true</c> all arrive at the binder as the strings
/// <c>"1500"</c>, <c>"21.5"</c> and <c>"true"</c>. Bound into a
/// <see cref="Dictionary{TKey,TValue}"/> of <see cref="object"/> they stay strings, so
/// <see cref="SimulatedAdsConnection.InferPlcType"/> — which reads the CLR type and nothing
/// else — reports <c>STRING</c> for every config-seeded symbol where a real PLC reports
/// <c>DINT</c>, <c>LREAL</c> or <c>BOOL</c>. That makes a file-configured simulation an
/// unfaithful stand-in for hardware, which is the main reason to use one.
/// </para>
/// <para>
/// <b>The fix.</b> An <c>InitialValues</c> entry may declare its PLC type explicitly:
/// <code>
/// "InitialValues": {
///   "MAIN.Speed":    { "value": 1500, "type": "DINT"  },
///   "MAIN.Setpoint": { "value": 21.5, "type": "LREAL" },
///   "MAIN.Running":  { "value": true, "type": "BOOL"  },
///   "MAIN.Station":  "Demo Station"
/// }
/// </code>
/// A typed entry is converted to the .NET type of its declared IEC 61131-3 type via
/// <see cref="Iec61131Converter.Beckhoff"/> (so type names are case-insensitive and Beckhoff
/// aliases resolve) and stored boxed as that type. A bare scalar entry keeps its historical
/// behaviour and is seeded as a <see cref="string"/> — for a genuine <c>STRING</c> symbol that
/// is already correct, and for anything else the declared-type form is the escape hatch.
/// The type is never guessed from the content: a <c>STRING</c> symbol whose value happens to
/// look numeric must not silently become a <c>DINT</c>.
/// </para>
/// <para>
/// <b>Why not the stock binder.</b> Binding a nested <c>{ "value": …, "type": … }</c> object
/// into a <c>Dictionary&lt;string, object?&gt;</c> yields a bare <see cref="object"/> instance
/// — the binder has no target type to bind the children onto and drops them. Reshaping
/// <see cref="PlcTargetOptions.InitialValues"/> into a dedicated entry type would bind
/// natively but is a source-breaking change for code-first callers who assign plain CLR
/// values. This pass therefore reads the section itself, immediately after <c>Bind</c>, and
/// overwrites the entries the binder could not type. Only keys present in configuration are
/// touched, so code-first values layered on afterwards survive.
/// </para>
/// <para>
/// <b>Errors are collected, not thrown.</b> Every problem is appended to
/// <see cref="PlcTargetOptions.InitialValueBindingErrors"/> and surfaced by
/// <see cref="TwinCatAdsOptionsValidator"/>, so an operator sees every bad entry in one
/// startup failure rather than fixing them one restart at a time.
/// </para>
/// </remarks>
internal static class InitialValueBinder
{
    private const string ValueKey = "value";
    private const string TypeKey = "type";

    /// <summary>
    /// The canonical IEC names accepted in a <c>type</c> declaration, listed in an
    /// unknown-type failure so the operator does not have to go looking for them.
    /// </summary>
    private const string SupportedTypeNames =
        "BOOL, BYTE, WORD, DWORD, LWORD, SINT, INT, DINT, LINT, USINT, UINT, UDINT, ULINT, " +
        "REAL, LREAL, TIME, DT, STRING, WSTRING";

    /// <summary>
    /// Re-binds the <c>InitialValues</c> of every target in <paramref name="targets"/> that has a
    /// matching child section under <paramref name="plcTargetsSection"/>. Targets that exist only
    /// in code are left untouched — their values already carry real CLR types.
    /// </summary>
    public static void Bind(
        IConfigurationSection plcTargetsSection,
        IDictionary<string, PlcTargetOptions> targets)
    {
        foreach (var targetSection in plcTargetsSection.GetChildren())
        {
            if (targets.TryGetValue(targetSection.Key, out var target))
                BindTarget(
                    targetSection.Key,
                    targetSection.GetSection(nameof(PlcTargetOptions.InitialValues)),
                    target);
        }
    }

    /// <summary>Re-binds one target's <c>InitialValues</c> from <paramref name="section"/>.</summary>
    private static void BindTarget(string targetId, IConfigurationSection section, PlcTargetOptions target)
    {
        // Configure delegates run once per options instance, but clearing keeps a repeated
        // bind (e.g. a test that rebuilds the provider over the same options object) from
        // accumulating duplicate failures.
        target.InitialValueBindingErrors.Clear();

        foreach (var entry in section.GetChildren())
        {
            // Scalar leaf — "MAIN.Station": "Demo Station". Seeded as the string it bound as;
            // this is the historical shape and stays exactly as it was.
            if (entry.Value is not null)
            {
                target.InitialValues[entry.Key] = entry.Value;
                continue;
            }

            var children = entry.GetChildren().ToList();

            // Neither a value nor children: an explicit null (JSON `null`). Seeding null is
            // legal — the store holds it, and a typed read rejects it with an actionable cast
            // error rather than inventing a value.
            if (children.Count == 0)
            {
                target.InitialValues[entry.Key] = null;
                continue;
            }

            if (TryConvertTypedEntry(targetId, entry, children, target.InitialValueBindingErrors, out var converted))
                target.InitialValues[entry.Key] = converted;
        }
    }

    /// <summary>
    /// Converts one <c>{ "value": …, "type": … }</c> entry to its declared CLR type, appending an
    /// actionable message to <paramref name="errors"/> and returning <see langword="false"/> when
    /// the entry cannot be seeded.
    /// </summary>
    private static bool TryConvertTypedEntry(
        string targetId,
        IConfigurationSection entry,
        List<IConfigurationSection> children,
        List<string> errors,
        out object? converted)
    {
        converted = null;

        var unknownKeys = children
            .Where(c => !c.Key.Equals(ValueKey, StringComparison.OrdinalIgnoreCase)
                     && !c.Key.Equals(TypeKey, StringComparison.OrdinalIgnoreCase))
            .Select(c => $"'{c.Key}'")
            .ToList();

        if (unknownKeys.Count > 0)
        {
            errors.Add(
                $"Target '{targetId}': InitialValues entry '{entry.Path}' has unrecognised " +
                $"{(unknownKeys.Count == 1 ? "key" : "keys")} {string.Join(", ", unknownKeys)}. " +
                $"A typed seed entry accepts only 'value' and 'type', " +
                $"e.g. {{ \"value\": 1500, \"type\": \"DINT\" }}.");
            return false;
        }

        // A `value` that is itself an object cannot be seeded — the simulated store holds
        // scalars. Checked before the missing-type check so the message names the real problem.
        var valueSection = children.FirstOrDefault(c => c.Key.Equals(ValueKey, StringComparison.OrdinalIgnoreCase));
        if (valueSection is { Value: null } && valueSection.GetChildren().Any())
        {
            errors.Add(
                $"Target '{targetId}': InitialValues entry '{entry.Path}' declares a non-scalar 'value'. " +
                $"Seed values must be scalars; seed each leaf under its own symbol path instead.");
            return false;
        }

        var declaredType = entry[TypeKey];
        if (string.IsNullOrWhiteSpace(declaredType))
        {
            errors.Add(
                $"Target '{targetId}': InitialValues entry '{entry.Path}' supplies a 'value' without a 'type'. " +
                $"Configuration is string-typed, so an untyped entry would be seeded — and reported back by a " +
                $"metadata read — as STRING. Declare the PLC type (e.g. {{ \"value\": 1500, \"type\": \"DINT\" }}), " +
                $"or write the value directly as '{entry.Path}' to seed it as a string.");
            return false;
        }

        if (!Iec61131Converter.Beckhoff.TryGetDotNetType(declaredType, out var clrType) || clrType is null)
        {
            errors.Add(
                $"Target '{targetId}': InitialValues entry '{entry.Path}' declares type '{declaredType}', which is " +
                $"not a recognised IEC 61131-3 elementary type. Fix '{entry.Path}:{TypeKey}'. " +
                $"Supported: {SupportedTypeNames}.");
            return false;
        }

        var rawValue = entry[ValueKey];

        // No value (key absent, or present but null): seed the declared type's default, so
        // `{ "type": "DINT" }` declares a symbol that reads back as DINT 0 rather than being
        // absent from the store entirely.
        if (rawValue is null)
        {
            converted = Iec61131Converter.Beckhoff.GetDefaultValue(declaredType);
            return true;
        }

        // TIME maps to TimeSpan, which is not IConvertible — Convert.ChangeType cannot reach it
        // from a string, so parse it here rather than let the shared converter fail with a
        // message about IConvertible that says nothing useful about the config entry.
        if (clrType == typeof(TimeSpan))
        {
            if (TimeSpan.TryParse(rawValue, CultureInfo.InvariantCulture, out var parsed))
            {
                converted = parsed;
                return true;
            }

            errors.Add(
                $"Target '{targetId}': InitialValues entry '{entry.Path}' cannot seed value '{rawValue}' as " +
                $"'{declaredType}'. Expected a duration such as '00:00:05' or '1.02:03:04'.");
            return false;
        }

        try
        {
            converted = Iec61131Converter.Beckhoff.ConvertValue(declaredType, rawValue);
            return true;
        }
        catch (InvalidCastException ex)
        {
            errors.Add(
                $"Target '{targetId}': InitialValues entry '{entry.Path}' cannot seed value '{rawValue}' as " +
                $"'{declaredType}'. {ex.Message}");
            return false;
        }
    }
}
