using System.Diagnostics.CodeAnalysis;
using TwinCAT.TypeSystem;

namespace Dahlke.TwinCAT.Ads.Tests;

/// <summary>
/// Minimal <see cref="IStructValue"/> stub standing in for Beckhoff's <c>DynamicValue</c>: the
/// value a container read through its symbol's value accessor — or a notification payload —
/// decodes to. <see cref="PlcValueDecoder"/> reads only
/// <see cref="IStructValue.TryGetMemberValue"/>; everything else throws, so a new dependency fails
/// loudly instead of passing on a default.
/// </summary>
internal sealed class StubStructValue(IReadOnlyDictionary<string, object?> members) : IStructValue
{
    public StubStructValue(params (string Name, object? Value)[] members)
        : this(members.ToDictionary(m => m.Name, m => m.Value))
    {
    }

    /// <summary>How often a member was asked for — lets a test prove the value was consulted.</summary>
    public int MemberLookups { get; private set; }

    public bool TryGetMemberValue(string name, [NotNullWhen(true)] out object? value)
    {
        MemberLookups++;
        return members.TryGetValue(name, out value) && value is not null;
    }

    public bool TrySetMemberValue(string name, object value) => throw new NotSupportedException();

    public TimeSpan Age => throw new NotSupportedException();

    public ReadOnlyMemory<byte> CachedRaw => throw new NotSupportedException();

    public IDataType? DataType => throw new NotSupportedException();

    public bool IsPrimitive => throw new NotSupportedException();

    public ISymbol Symbol => throw new NotSupportedException();

    public DateTimeOffset TimeStamp => throw new NotSupportedException();
}
