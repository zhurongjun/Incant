using System.Collections.ObjectModel;

namespace Incant.Core.Arguments;

/// <summary>Factories for scalar, sequence and map policies. Element copying remains explicit.</summary>
public static class ArgumentKeys
{
    /// <summary>Creates an immutable scalar key with an equality constraint, or an explicit reducer.</summary>
    /// <remarks>Only primitive, enum and known immutable BCL scalar types are accepted. Use the key constructor for custom values.</remarks>
    public static ArgumentKey<T> Scalar<T>(string id, Func<T, T, T>? merge = null)
    {
        Type type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (!type.IsPrimitive && !type.IsEnum && type != typeof(string) && type != typeof(decimal)
            && type != typeof(Guid) && type != typeof(Version) && type != typeof(DateTime)
            && type != typeof(DateTimeOffset) && type != typeof(TimeSpan))
        {
            throw new ArgumentException("Custom values require an explicit snapshot strategy.", nameof(T));
        }

        return new ArgumentKey<T>(id, value => value, merge ?? RequireEqual);
    }

    /// <summary>Returns the incoming scalar as an explicit merge policy.</summary>
    public static T Replace<T>(T left, T right) => right;

    /// <summary>Requires equal scalar contributions.</summary>
    public static T RequireEqual<T>(T left, T right) => EqualityComparer<T>.Default.Equals(left, right)
        ? left
        : throw new ArgumentException("Values must agree.");

    /// <summary>Creates a sequence key preserving order and duplicates unless an identity comparer is supplied.</summary>
    public static ArgumentKey<IReadOnlyList<T>> Sequence<T>(
        string id, Func<T, T> snapshotElement, IEqualityComparer<T>? distinct = null)
    {
        ArgumentNullException.ThrowIfNull(snapshotElement);
        return new ArgumentKey<IReadOnlyList<T>>(id,
            values => Array.AsReadOnly((distinct is null ? values : values.Distinct(distinct)).Select(snapshotElement).ToArray()),
            (left, right) => Array.AsReadOnly((distinct is null
                ? left.Concat(right)
                : left.Concat(right).Distinct(distinct)).ToArray()),
            values => values.Count);
    }

    /// <summary>Creates a map merged by name, rejecting incompatible values unless a reducer is supplied.</summary>
    public static ArgumentKey<IReadOnlyDictionary<string, T>> Map<T>(
        string id, Func<T, T> snapshotValue, Func<T, T, T>? mergeValue = null,
        IEqualityComparer<string>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(snapshotValue);
        comparer ??= StringComparer.Ordinal;
        return new ArgumentKey<IReadOnlyDictionary<string, T>>(id,
            values => new ReadOnlyDictionary<string, T>(values.ToDictionary(
                pair => pair.Key, pair => snapshotValue(pair.Value), comparer)),
            (left, right) =>
            {
                var merged = new Dictionary<string, T>(left, comparer);
                foreach (KeyValuePair<string, T> pair in right)
                {
                    merged[pair.Key] = merged.TryGetValue(pair.Key, out T? previous)
                        ? (mergeValue ?? RequireEqual)(previous, pair.Value)
                        : pair.Value;
                }

                return new ReadOnlyDictionary<string, T>(merged);
            });
    }
}

/// <summary>Constructs lexical paths at declaration time; never probes the filesystem.</summary>
public static class ArgumentPath
{
    /// <summary>Resolves a path against an explicitly supplied absolute base directory.</summary>
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
