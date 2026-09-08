using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace Incant.Core.Arguments;

/// <summary>An immutable configuration table. Transformations create a new identity and preserve untouched origins.</summary>
public sealed class ArgumentSet
{
    private readonly IReadOnlyDictionary<string, Entry> _entries;

    /// <summary>Creates an empty snapshot with optional opaque metadata.</summary>
    public ArgumentSet(IReadOnlyDictionary<string, string>? metadata = null)
        : this(new ArgumentSetId(Guid.NewGuid()), new Dictionary<string, Entry>(StringComparer.Ordinal), metadata)
    {
    }

    private ArgumentSet(ArgumentSetId id, IReadOnlyDictionary<string, Entry> entries,
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

    /// <summary>Gets opaque metadata, which has no merge or command semantics.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }

    /// <summary>Gets the present keys in declaration order.</summary>
    public IReadOnlyList<ArgumentKey> Keys => Array.AsReadOnly(_entries.Values
        .Where(entry => !entry.Removed).Select(entry => entry.Key).ToArray());

    /// <summary>Reads a detached value. Explicit null and empty values count as present.</summary>
    public bool TryGet<T>(ArgumentKey<T> key, [MaybeNullWhen(false)] out T value)
    {
        ArgumentNullException.ThrowIfNull(key);
        CheckDefinition(key);
        if (_entries.TryGetValue(key.Id, out Entry? entry) && !entry.Removed)
        {
            value = (T)key.Snapshot(entry.Value)!;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Reads a required value, throwing <see cref="KeyNotFoundException"/> when absent.</summary>
    public T Get<T>(ArgumentKey<T> key) => TryGet(key, out T? value)
        ? value
        : throw new KeyNotFoundException($"Argument '{key.Id}' is not set in {Id}.");

    /// <summary>Gets all original contributions, including element positions, without exposing their values.</summary>
    public IReadOnlyList<ArgumentOrigin> Origins(ArgumentKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        CheckDefinition(key);
        if (!_entries.TryGetValue(key.Id, out Entry? entry))
        {
            return Array.Empty<ArgumentOrigin>();
        }

        var origins = new List<ArgumentOrigin>();
        foreach (Contribution contribution in entry.Contributions)
        {
            int? count = entry.Removed ? null : key.Count(contribution.Value);
            if (count is > 0)
            {
                for (int index = 0; index < count; index++)
                {
                    origins.Add(new ArgumentOrigin(contribution.Origin.SetId, key.Id, index, contribution.Origin.Metadata));
                }
            }
            else
            {
                origins.Add(contribution.Origin);
            }
        }

        return origins.AsReadOnly();
    }

    /// <summary>Replaces one field explicitly, recording a new original contribution.</summary>
    public ArgumentSet With<T>(ArgumentKey<T> key, T value)
    {
        ArgumentNullException.ThrowIfNull(key);
        CheckDefinition(key);
        return Set(key, key.Snapshot(value), removed: false);
    }

    /// <summary>Removes a field and records a tombstone, distinct from an unset or empty field.</summary>
    public ArgumentSet Remove(ArgumentKey key) => Set(key, null, removed: true);

    /// <summary>Reports an explicit removal, even though required reads will treat the field as absent.</summary>
    public bool IsRemoved(ArgumentKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        CheckDefinition(key);
        return _entries.TryGetValue(key.Id, out Entry? entry) && entry.Removed;
    }

    /// <summary>Adds a fresh sequence contribution using the key's sequence policy.</summary>
    public ArgumentSet Append<T>(ArgumentKey<IReadOnlyList<T>> key, params T[] values)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(values);
        return Merge(new ArgumentSet(Metadata).With(key, values));
    }

    /// <summary>Adds a fresh named contribution using the map key's merge policy.</summary>
    public ArgumentSet Append<T>(ArgumentKey<IReadOnlyDictionary<string, T>> key, IReadOnlyDictionary<string, T> values)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(values);
        return Merge(new ArgumentSet(Metadata).With(key, values));
    }

    /// <summary>Removes matching names as an explicit map replacement. An absent field remains absent.</summary>
    public ArgumentSet RemoveItems<T>(ArgumentKey<IReadOnlyDictionary<string, T>> key, Predicate<string> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return TryGet(key, out IReadOnlyDictionary<string, T>? values)
            ? With(key, values.Where(pair => !predicate(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value))
            : Filter(_ => true);
    }
    /// <summary>Removes matching elements as an explicit replacement. An absent field remains absent.</summary>
    public ArgumentSet RemoveItems<T>(ArgumentKey<IReadOnlyList<T>> key, Predicate<T> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return TryGet(key, out IReadOnlyList<T>? values)
            ? With(key, Array.AsReadOnly(values.Where(value => !predicate(value)).ToArray()))
            : Filter(_ => true);
    }

    /// <summary>Retains selected fields and their original contributions, including removal markers.</summary>
    public ArgumentSet Filter(Func<ArgumentKey, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new ArgumentSet(new ArgumentSetId(Guid.NewGuid()),
            _entries.Where(pair => predicate(pair.Value.Key)).ToDictionary(pair => pair.Key, pair => pair.Value,
                StringComparer.Ordinal), Metadata);
    }

    /// <summary>Creates a metadata-only snapshot; existing origins retain their original metadata.</summary>
    public ArgumentSet WithMetadata(IReadOnlyDictionary<string, string> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return new ArgumentSet(new ArgumentSetId(Guid.NewGuid()), _entries, metadata);
    }

    /// <summary>Merges unique original contributions using each key's policy. Incoming removals remove a field.</summary>
    /// <exception cref="ArgumentConflictException">A key's values cannot be merged.</exception>
    /// <exception cref="ArgumentException">Two definitions reuse an identifier.</exception>
    public ArgumentSet Merge(ArgumentSet other) => Combine(other, replace: false);

    /// <summary>Replaces only the incoming fields, preserving the incoming origins.</summary>
    public ArgumentSet Override(ArgumentSet other) => Combine(other, replace: true);

    private ArgumentSet Set(ArgumentKey key, object? value, bool removed)
    {
        ArgumentNullException.ThrowIfNull(key);
        CheckDefinition(key);
        var id = new ArgumentSetId(Guid.NewGuid());
        var origin = new ArgumentOrigin(id, key.Id, null, Metadata);
        var entries = new Dictionary<string, Entry>(_entries, StringComparer.Ordinal)
        {
            [key.Id] = new Entry(key, value, [new Contribution(origin, value)], removed),
        };
        return new ArgumentSet(id, entries, Metadata);
    }

    private ArgumentSet Combine(ArgumentSet other, bool replace)
    {
        ArgumentNullException.ThrowIfNull(other);
        var entries = new Dictionary<string, Entry>(_entries, StringComparer.Ordinal);
        foreach (KeyValuePair<string, Entry> pair in other._entries)
        {
            CheckDefinition(pair.Value.Key);
            Entry incoming = pair.Value;
            if (!entries.TryGetValue(pair.Key, out Entry? previous) || replace || incoming.Removed || previous.Removed)
            {
                entries[pair.Key] = incoming;
                continue;
            }

            var contributions = new List<Contribution>(previous.Contributions);
            var seen = new HashSet<ArgumentSetId>(contributions.Select(item => item.Origin.SetId));
            object? value = previous.Value;
            foreach (Contribution contribution in incoming.Contributions)
            {
                if (seen.Add(contribution.Origin.SetId))
                {
                    value = incoming.Key.Merge(value, contribution.Value, contributions[^1].Origin, contribution.Origin);
                    contributions.Add(contribution);
                }
            }

            entries[pair.Key] = new Entry(incoming.Key, value, contributions.ToArray(), false);
        }

        return new ArgumentSet(new ArgumentSetId(Guid.NewGuid()), entries, Metadata);
    }

    internal object? ReadSnapshot(ArgumentKey key)
    {
        CheckDefinition(key);
        return _entries.TryGetValue(key.Id, out Entry? entry) && !entry.Removed ? key.Snapshot(entry.Value) : null;
    }

    private void CheckDefinition(ArgumentKey key)
    {
        if (_entries.TryGetValue(key.Id, out Entry? existing) && !ReferenceEquals(key, existing.Key))
        {
            throw new ArgumentException($"Argument identifier '{key.Id}' has another definition.", nameof(key));
        }
    }

    private sealed record Contribution(ArgumentOrigin Origin, object? Value);

    private sealed record Entry(ArgumentKey Key, object? Value, IReadOnlyList<Contribution> Contributions, bool Removed);
}
