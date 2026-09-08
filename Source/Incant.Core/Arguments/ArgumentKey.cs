namespace Incant.Core.Arguments;

/// <summary>Identity and value policy for an entry. Definitions are compared by reference, not identifier alone.</summary>
public abstract class ArgumentKey
{
    private protected ArgumentKey(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
    }

    /// <summary>Gets the unique ordinal identifier.</summary>
    public string Id { get; }

    internal abstract object? Snapshot(object? value);

    internal abstract object? Merge(object? left, object? right, ArgumentOrigin leftOrigin, ArgumentOrigin rightOrigin);

    internal virtual int? Count(object? value) => null;
}

/// <summary>A typed key with explicit copying and merge policies. Policies must be pure.</summary>
/// <typeparam name="T">Value exposed by this key.</typeparam>
public sealed class ArgumentKey<T> : ArgumentKey
{
    private readonly Func<T, T> _snapshot;
    private readonly Func<T, T, T> _merge;
    private readonly Func<T, int?>? _count;

    /// <summary>Defines a key. The snapshot must detach every mutable part of the value, including on reads.</summary>
    public ArgumentKey(string id, Func<T, T> snapshot, Func<T, T, T> merge)
        : this(id, snapshot, merge, null)
    {
    }

    internal ArgumentKey(string id, Func<T, T> snapshot, Func<T, T, T> merge, Func<T, int?>? count)
        : base(id)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(merge);
        _snapshot = snapshot;
        _merge = merge;
        _count = count;
    }

    internal override object? Snapshot(object? value) => _snapshot((T)value!);

    internal override int? Count(object? value) => _count?.Invoke((T)value!);

    internal override object? Merge(object? left, object? right, ArgumentOrigin leftOrigin, ArgumentOrigin rightOrigin)
    {
        try
        {
            return _snapshot(_merge(_snapshot((T)left!), _snapshot((T)right!)));
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentConflictException(Id, leftOrigin, rightOrigin, exception.Message);
        }
    }
}

/// <summary>Requests typed ArgumentSet extensions for a static readonly key field or get-only property.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class GenerateArgumentAttribute : Attribute
{
    /// <summary>Creates a request using the member's name, or an explicit method suffix.</summary>
    public GenerateArgumentAttribute(string? name = null) => Name = name;

    /// <summary>Gets the optional generated method suffix.</summary>
    public string? Name { get; }
}
