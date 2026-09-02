namespace Incant.Core.Toolchains;

/// <summary>Represents an exact or bounded version requirement.</summary>
public sealed class VersionConstraint
{
    /// <summary>Initializes a version constraint.</summary>
    /// <param name="exact">The exact required version.</param>
    /// <param name="minimumInclusive">The inclusive lower bound.</param>
    /// <param name="maximumExclusive">The exclusive upper bound.</param>
    /// <exception cref="ArgumentException">
    /// An exact version is combined with a range, or the range is empty.
    /// </exception>
    public VersionConstraint(
        Version? exact = null,
        Version? minimumInclusive = null,
        Version? maximumExclusive = null)
    {
        if (exact is null && minimumInclusive is null && maximumExclusive is null)
        {
            throw new ArgumentException("At least one exact or range version must be supplied.");
        }

        if (exact is not null && (minimumInclusive is not null || maximumExclusive is not null))
        {
            throw new ArgumentException("An exact version cannot be combined with a range.");
        }

        if (minimumInclusive is not null
            && maximumExclusive is not null
            && minimumInclusive >= maximumExclusive)
        {
            throw new ArgumentException("The minimum version must be lower than the exclusive maximum.");
        }

        Exact = exact;
        MinimumInclusive = minimumInclusive;
        MaximumExclusive = maximumExclusive;
    }

    /// <summary>Gets the required exact version.</summary>
    public Version? Exact { get; }

    /// <summary>Gets the inclusive lower bound.</summary>
    public Version? MinimumInclusive { get; }

    /// <summary>Gets the exclusive upper bound.</summary>
    public Version? MaximumExclusive { get; }

    /// <summary>Determines whether a version satisfies this constraint.</summary>
    /// <param name="version">The version to inspect.</param>
    /// <returns>True when the version satisfies this constraint; otherwise, false.</returns>
    public bool Matches(Version? version)
    {
        if (version is null)
        {
            return false;
        }

        if (Exact is not null)
        {
            return version == Exact;
        }

        return (MinimumInclusive is null || version >= MinimumInclusive)
            && (MaximumExclusive is null || version < MaximumExclusive);
    }
}
