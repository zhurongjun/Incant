using System.Collections.ObjectModel;

namespace Incant.CX.Arguments;

/// <summary>Opaque identity of one immutable configuration snapshot.</summary>
public readonly record struct ArgumentSetId
{
    private readonly Guid _value;

    internal ArgumentSetId(Guid value) => _value = value;

    /// <summary>Returns a display identifier, without implying ordering or dependency.</summary>
    public override string ToString() => _value.ToString("N");
}

/// <summary>Identifies an original contribution and snapshots its opaque metadata.</summary>
public sealed record ArgumentOrigin
{
    /// <summary>Creates a source reference, detaching metadata from its owner.</summary>
    public ArgumentOrigin(ArgumentSetId setId, ArgumentField field, int? elementIndex,
        IReadOnlyDictionary<string, string> metadata)
    {
        if (!Enum.IsDefined(field))
        {
            throw new ArgumentOutOfRangeException(nameof(field));
        }

        ArgumentNullException.ThrowIfNull(metadata);
        if (elementIndex is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(elementIndex));
        }

        SetId = setId;
        Field = field;
        ElementIndex = elementIndex;
        Metadata = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(metadata, StringComparer.Ordinal));
    }

    /// <summary>Gets the original snapshot identity.</summary>
    public ArgumentSetId SetId { get; }

    /// <summary>Gets the configuration identifier.</summary>
    public ArgumentField Field { get; }

    /// <summary>Gets the element position within its original contribution.</summary>
    public int? ElementIndex { get; }

    /// <summary>Gets opaque original metadata, never interpreted as business configuration.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }

    /// <summary>Compares the source identity and element, independently of display metadata.</summary>
    public bool Equals(ArgumentOrigin? other) => other is not null && SetId == other.SetId
        && Field == other.Field && ElementIndex == other.ElementIndex;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(SetId, Field, ElementIndex);
}

/// <summary>A configuration merge conflict, retaining both original contributions.</summary>
public sealed class ArgumentConflictException : InvalidOperationException
{
    /// <summary>Creates a conflict for two incompatible contributions.</summary>
    public ArgumentConflictException(ArgumentField field, ArgumentOrigin left, ArgumentOrigin right, string reason)
        : base(FormatMessage(field, left, right, reason))
    {
        Field = field;
        Left = left;
        Right = right;
    }

    /// <summary>Gets the affected configuration identifier.</summary>
    public ArgumentField Field { get; }

    /// <summary>Gets the earlier contribution.</summary>
    public ArgumentOrigin Left { get; }

    /// <summary>Gets the incoming contribution.</summary>
    public ArgumentOrigin Right { get; }

    private static string FormatMessage(ArgumentField field, ArgumentOrigin left, ArgumentOrigin right, string reason)
    {
        if (!Enum.IsDefined(field))
        {
            throw new ArgumentOutOfRangeException(nameof(field));
        }

        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return $"Argument '{field}' conflicts between {left.SetId} and {right.SetId}: {reason}";
    }
}
