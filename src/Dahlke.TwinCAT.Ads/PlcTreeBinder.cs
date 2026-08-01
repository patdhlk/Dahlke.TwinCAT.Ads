using System.Collections.Concurrent;
using System.Reflection;

namespace Dahlke.TwinCAT.Ads;

/// <summary>
/// Binds a neutral PLC value tree — the <c>IReadOnlyDictionary&lt;string, object?&gt;</c> a struct,
/// function block or union decodes to, and the <c>object?[]</c> an array decodes to — onto a .NET
/// type, by MEMBER NAME.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Without it, the only values that survived a typed read were those a
/// direct cast or <see cref="IConvertible"/> could handle, so a PLC struct could be read as
/// <c>T</c> only when the store happened to hold that exact CLR type. A simulated target could
/// therefore not stand in for hardware on the single most common domain shape in a real project —
/// reading <c>ST_Motor</c> into a C# type — and the divergence stayed invisible until
/// commissioning.
/// </para>
/// <para>
/// <b>By name here, by LAYOUT on a real symbol read.</b> This is the one place the two paths are
/// not interchangeable, and it bounds what a simulated target can prove.
/// <see cref="IAdsConnection.ReadValueAsync{T}"/> against real hardware hands <c>T</c> to
/// Beckhoff's marshaller, which maps PLC memory onto the .NET type by declaration ORDER and
/// layout. This binder has no memory to map — a decoded tree is keyed by member name — so it
/// matches names (case-insensitively) and ignores order. The practical consequence: a target type
/// whose members are named correctly but DECLARED IN THE WRONG ORDER binds cleanly here and will
/// still be wrong against hardware. A simulated target catches a misspelled or mistyped member; it
/// cannot catch a mis-ordered one.
/// </para>
/// <para>
/// <b>Every member of the target must be present in the tree.</b> A member the tree does not
/// supply is a failure naming that member, not a silent default — the target type and the PLC's
/// type disagreeing is exactly what a consumer wants to hear about, and this library refuses
/// silently-wrong data elsewhere for the same reason (see <c>PlcAlarmShapeException</c>). EXTRA
/// keys in the tree are ignored, so the target type drives: reading a 20-member PLC struct into a
/// 3-member projection is not supported, and reading it as
/// <c>IReadOnlyDictionary&lt;string, object?&gt;</c> remains the way to take a subset.
/// </para>
/// <para>
/// <b>Construction.</b> A public constructor taking parameters wins when every parameter name
/// resolves in the tree — which is what binds a positional <see langword="record"/> or any
/// immutable type. Otherwise a public parameterless constructor is used and writable properties
/// and public fields are set. Read-only computed properties are skipped: they have no setter, so
/// nothing about them can disagree with the PLC.
/// </para>
/// </remarks>
internal static class PlcTreeBinder
{
    // Reflection plans are per closed type and never change, and the notification path can bind
    // at cycle rate. Built once, reused for the process.
    private static readonly ConcurrentDictionary<Type, BindingPlan?> Plans = new();

    /// <summary>
    /// Whether <paramref name="value"/> is a tree this binder knows how to bind — the shape check
    /// only, so a caller can fall through to its own error message when it is not.
    /// </summary>
    public static bool IsTree(object? value)
        => value is IReadOnlyDictionary<string, object?> or IDictionary<string, object?> or object?[];

    /// <summary>
    /// Binds <paramref name="value"/> onto <paramref name="targetType"/>, or throws an
    /// <see cref="InvalidCastException"/> naming what did not line up.
    /// </summary>
    /// <param name="value">A decoded tree — a member dictionary or an element array.</param>
    /// <param name="targetType">The .NET type to bind onto.</param>
    /// <param name="context">Symbol path or member path, used in failure messages.</param>
    public static object? Bind(object? value, Type targetType, string context)
    {
        if (value is object?[] elements)
            return BindArray(elements, targetType, context);

        var members = AsMemberMap(value)
            ?? throw new InvalidCastException(
                $"Symbol '{context}': expected a decoded PLC value tree to bind onto " +
                $"'{targetType.Name}' but got '{value?.GetType().Name ?? "null"}'.");

        return BindMembers(members, targetType, context);
    }

    private static IReadOnlyDictionary<string, object?>? AsMemberMap(object? value) => value switch
    {
        IReadOnlyDictionary<string, object?> map => map,
        IDictionary<string, object?> map => new Dictionary<string, object?>(map),
        _ => null,
    };

    private static object BindArray(object?[] elements, Type targetType, string context)
    {
        if (!targetType.IsArray || targetType.GetArrayRank() != 1)
            throw new InvalidCastException(
                $"Symbol '{context}': the PLC value is an array of {elements.Length} element(s), " +
                $"which binds onto a one-dimensional array type, not '{targetType.Name}'.");

        var elementType = targetType.GetElementType()!;
        var bound = Array.CreateInstance(elementType, elements.Length);
        for (var i = 0; i < elements.Length; i++)
            bound.SetValue(ConvertMember(elements[i], elementType, $"{context}[{i}]"), i);

        return bound;
    }

    private static object BindMembers(
        IReadOnlyDictionary<string, object?> members, Type targetType, string context)
    {
        var plan = Plans.GetOrAdd(targetType, BuildPlan)
            ?? throw new InvalidCastException(
                $"Symbol '{context}': cannot bind a PLC value tree onto '{targetType.Name}' — it " +
                $"has no public constructor this binder can use. Bind onto a type with a " +
                $"parameterless constructor, a positional record, or read the symbol as " +
                $"IReadOnlyDictionary<string, object?>.");

        // Case-insensitive because PLC member names follow the PLC program's conventions
        // (nSpeed, bRunning), not C#'s, and the simulated store is already case-insensitive.
        var lookup = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in members) lookup[key] = value;

        var missing = plan.RequiredNames.Where(n => !lookup.ContainsKey(n)).ToList();
        if (missing.Count > 0)
            throw new InvalidCastException(
                $"Symbol '{context}': the PLC value has no member(s) " +
                $"{string.Join(", ", missing.Select(m => $"'{m}'"))} required by " +
                $"'{targetType.Name}'. The value supplies {Describe(lookup.Keys)}. Every member of " +
                $"the target type must be present — a missing one is a disagreement between the " +
                $"type and the PLC, not a default. To read a subset, read the symbol as " +
                $"IReadOnlyDictionary<string, object?> instead.");

        var arguments = plan.ConstructorParameters
            .Select(p => ConvertMember(lookup[p.Name], p.Type, $"{context}.{p.Name}"))
            .ToArray();

        var instance = plan.Create(arguments);

        foreach (var member in plan.WritableMembers)
            member.Set(instance, ConvertMember(lookup[member.Name], member.Type, $"{context}.{member.Name}"));

        return instance;
    }

    /// <summary>
    /// Converts one member value, recursing through this binder when the member is itself a tree
    /// so nested structs and arrays bind to arbitrary depth.
    /// </summary>
    private static object? ConvertMember(object? value, Type memberType, string context)
        => IsTree(value) && !memberType.IsInstanceOfType(value)
            ? Bind(value, memberType, context)
            : AdsValueConverter.ConvertForRead(value, memberType, context);

    private static string Describe(IEnumerable<string> keys)
    {
        var named = keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        return named.Count == 0 ? "no members" : string.Join(", ", named.Select(k => $"'{k}'"));
    }

    private static BindingPlan? BuildPlan(Type targetType)
    {
        if (targetType.IsAbstract || targetType.IsInterface) return null;

        var constructors = targetType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        // A parameterised constructor wins — that is what binds a positional record or any type
        // whose members are init-only. Widest first, so the richest constructor is preferred.
        var parameterised = constructors
            .Where(c => c.GetParameters().Length > 0)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        var parameterless = constructors.FirstOrDefault(c => c.GetParameters().Length == 0);

        // A struct's implicit parameterless constructor is not reported by GetConstructors, so a
        // value type with no explicit one is created through Activator instead of a ConstructorInfo.
        var chosen = parameterised ?? parameterless;
        if (chosen is null && !targetType.IsValueType) return null;

        Func<object?[], object> create = chosen is null
            ? _ => Activator.CreateInstance(targetType)!
            : chosen.Invoke;

        var constructorParameters = chosen?.GetParameters()
            .Select(p => new PlanParameter(p.Name!, p.ParameterType))
            .ToArray() ?? [];

        var covered = constructorParameters
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var writable = new List<PlanMember>();
        foreach (var property in targetType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || property.GetIndexParameters().Length > 0) continue;
            if (covered.Contains(property.Name)) continue;
            writable.Add(new PlanMember(property.Name, property.PropertyType, property.SetValue));
        }

        foreach (var field in targetType.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.IsInitOnly || covered.Contains(field.Name)) continue;
            writable.Add(new PlanMember(field.Name, field.FieldType, field.SetValue));
        }

        var required = constructorParameters.Select(p => p.Name)
            .Concat(writable.Select(m => m.Name))
            .ToArray();

        // Nothing to bind at all is not a struct-shaped type; refuse rather than hand back an
        // empty instance that silently ignored every member the PLC supplied.
        if (required.Length == 0) return null;

        return new BindingPlan(create, constructorParameters, writable.ToArray(), required);
    }

    private sealed record PlanParameter(string Name, Type Type);

    private sealed record PlanMember(string Name, Type Type, Action<object, object?> Set);

    private sealed record BindingPlan(
        Func<object?[], object> Create,
        PlanParameter[] ConstructorParameters,
        PlanMember[] WritableMembers,
        string[] RequiredNames);
}
