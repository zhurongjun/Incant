using System.Collections.ObjectModel;

namespace Incant.CX.Arguments;

/// <summary>An immutable C-family configuration with fixed fields and original contribution tracking.</summary>
public sealed partial class ArgumentSet
{
    private readonly IReadOnlyDictionary<ArgumentField, Entry> _entries;

    /// <summary>Creates an empty configuration with opaque tracing metadata.</summary>
    public ArgumentSet(IReadOnlyDictionary<string, string>? metadata = null)
        : this(new ArgumentSetId(Guid.NewGuid()), new Dictionary<ArgumentField, Entry>(), metadata)
    {
    }

    private ArgumentSet(ArgumentSetId id, IReadOnlyDictionary<ArgumentField, Entry> entries,
        IReadOnlyDictionary<string, string>? metadata)
    {
        Id = id;
        _entries = entries;
        Metadata = new ReadOnlyDictionary<string, string>(metadata is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(metadata, StringComparer.Ordinal));
    }

    /// <summary>Gets this snapshot's identity.</summary>
    public ArgumentSetId Id { get; }

    /// <summary>Gets metadata that has no parameter or merge semantics.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }

    /// <summary>Gets present fields in configuration insertion order, excluding removal markers.</summary>
    public IReadOnlyList<ArgumentField> Fields => Array.AsReadOnly(_entries
        .Where(pair => !pair.Value.Removed).Select(pair => pair.Key).ToArray());

    /// <summary>Reports whether a field is explicitly set, including false and empty collections.</summary>
    public bool IsSet(ArgumentField field)
    {
        ValidateField(field);
        return _entries.TryGetValue(field, out Entry? entry) && !entry.Removed;
    }

    /// <summary>Reports an explicit removal, distinct from a field that was never set.</summary>
    public bool IsRemoved(ArgumentField field)
    {
        ValidateField(field);
        return _entries.TryGetValue(field, out Entry? entry) && entry.Removed;
    }

    /// <summary>Gets original contributions and their positions, including empty and removed fields.</summary>
    public IReadOnlyList<ArgumentOrigin> Origins(ArgumentField field)
    {
        ValidateField(field);
        if (!_entries.TryGetValue(field, out Entry? entry))
        {
            return Array.Empty<ArgumentOrigin>();
        }

        var origins = new List<ArgumentOrigin>();
        foreach (Contribution contribution in entry.Contributions)
        {
            int count = entry.Removed ? 0 : ElementCount(field, contribution.Value!);
            if (count == 0)
            {
                origins.Add(contribution.Origin);
            }
            else
            {
                for (int index = 0; index < count; index++)
                {
                    origins.Add(new ArgumentOrigin(contribution.Origin.SetId, field, index, contribution.Origin.Metadata));
                }
            }
        }

        return origins.AsReadOnly();
    }

    /// <summary>Retains selected fields, including removal markers, and preserves their origins.</summary>
    public ArgumentSet Filter(Func<ArgumentField, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new ArgumentSet(new ArgumentSetId(Guid.NewGuid()),
            _entries.Where(pair => predicate(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value), Metadata);
    }

    /// <summary>Creates a metadata-only snapshot. Existing origins keep their original metadata.</summary>
    public ArgumentSet WithMetadata(IReadOnlyDictionary<string, string> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return new ArgumentSet(new ArgumentSetId(Guid.NewGuid()), _entries, metadata);
    }

    /// <summary>Merges unique contributions using fixed field semantics. Incoming removal markers remove fields.</summary>
    /// <exception cref="ArgumentConflictException">Scalar or named values disagree.</exception>
    public ArgumentSet Merge(ArgumentSet other) => Combine(other, replace: false);

    /// <summary>Replaces incoming fields explicitly while retaining their original sources.</summary>
    public ArgumentSet Override(ArgumentSet other) => Combine(other, replace: true);

    private ArgumentSet Set(ArgumentField field, object? value, bool removed = false)
    {
        var id = new ArgumentSetId(Guid.NewGuid());
        var origin = new ArgumentOrigin(id, field, null, Metadata);
        var entries = new Dictionary<ArgumentField, Entry>(_entries)
        {
            [field] = new Entry(value, [new Contribution(origin, value)], removed),
        };
        return new ArgumentSet(id, entries, Metadata);
    }

    private ArgumentSet AppendValue(ArgumentField field, object value) =>
        Merge(new ArgumentSet(Metadata).Set(field, value));

    private ArgumentSet Combine(ArgumentSet other, bool replace)
    {
        ArgumentNullException.ThrowIfNull(other);
        var entries = new Dictionary<ArgumentField, Entry>(_entries);
        foreach ((ArgumentField field, Entry incoming) in other._entries)
        {
            if (!entries.TryGetValue(field, out Entry? previous) || replace || incoming.Removed || previous.Removed)
            {
                entries[field] = incoming;
                continue;
            }

            var contributions = new List<Contribution>(previous.Contributions);
            var seen = new HashSet<ArgumentSetId>(contributions.Select(item => item.Origin.SetId));
            object value = previous.Value!;
            foreach (Contribution contribution in incoming.Contributions)
            {
                if (!seen.Add(contribution.Origin.SetId))
                {
                    continue;
                }

                try
                {
                    value = MergeValue(field, value, contribution.Value!);
                }
                catch (ArgumentException exception)
                {
                    throw new ArgumentConflictException(field, FindConflict(field, contributions, contribution),
                        contribution.Origin, exception.Message);
                }

                contributions.Add(contribution);
            }

            entries[field] = new Entry(value, contributions.ToArray(), false);
        }

        return new ArgumentSet(new ArgumentSetId(Guid.NewGuid()), entries, Metadata);
    }

    private static ArgumentOrigin FindConflict(ArgumentField field, List<Contribution> previous, Contribution incoming)
    {
        foreach (Contribution contribution in previous)
        {
            try
            {
                MergeValue(field, contribution.Value!, incoming.Value!);
            }
            catch (ArgumentException)
            {
                return contribution.Origin;
            }
        }

        return previous[^1].Origin;
    }

    internal object? ReadValue(ArgumentField field) =>
        _entries.TryGetValue(field, out Entry? entry) && !entry.Removed ? entry.Value : null;

    private static void ValidateField(ArgumentField field)
    {
        if (!Enum.IsDefined(field))
        {
            throw new ArgumentOutOfRangeException(nameof(field));
        }
    }

    private static IReadOnlyList<T> SnapshotList<T>(IReadOnlyList<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Array.AsReadOnly(values.ToArray());
    }

    private static IReadOnlyDictionary<string, T> SnapshotMap<T>(IReadOnlyDictionary<string, T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new ReadOnlyDictionary<string, T>(new Dictionary<string, T>(values, StringComparer.Ordinal));
    }

    private static object MergeMap<T>(IReadOnlyDictionary<string, T> left, IReadOnlyDictionary<string, T> right)
    {
        var merged = new Dictionary<string, T>(left, StringComparer.Ordinal);
        foreach ((string name, T value) in right)
        {
            if (merged.TryGetValue(name, out T? previous) && !EqualityComparer<T>.Default.Equals(previous, value))
            {
                throw new ArgumentException($"Values for '{name}' must agree.");
            }

            merged[name] = value;
        }

        return new ReadOnlyDictionary<string, T>(merged);
    }

    private sealed record Contribution(ArgumentOrigin Origin, object? Value);

    private sealed record Entry(object? Value, IReadOnlyList<Contribution> Contributions, bool Removed);
}

/// <summary>Marks a fixed, optional configuration property for the CX-specific generator.</summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class ArgumentAttribute : Attribute
{
}

/// <summary>Resolves parameter paths lexically against an explicit base, without filesystem discovery.</summary>
public static class ArgumentPath
{
    /// <summary>Resolves a path against an absolute base directory without using the current directory.</summary>
    public static string Resolve(string path, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        if (!Path.IsPathFullyQualified(baseDirectory))
        {
            throw new ArgumentException("The base directory must be absolute.", nameof(baseDirectory));
        }

        return Path.GetFullPath(path, baseDirectory);
    }
}
