using Incant.Core.Arguments;

namespace Incant.UnitTest.Core.Arguments;

public sealed class ArgumentSetTests
{
    [Fact]
    public void UnsetFalseEmptyNullAndRemovalRemainDistinct()
    {
        ArgumentKey<bool> flag = ArgumentKeys.Scalar<bool>("flag");
        ArgumentKey<string?> text = ArgumentKeys.Scalar<string?>("text");
        ArgumentKey<IReadOnlyList<string>> items = ArgumentKeys.Sequence<string>("items", value => value);
        var empty = new ArgumentSet();
        ArgumentSet populated = empty.With(flag, false).With(text, null).With(items, []);
        Assert.False(empty.TryGet(flag, out _));
        Assert.False(populated.Get(flag));
        Assert.True(populated.TryGet(text, out string? value));
        Assert.Null(value);
        Assert.Empty(populated.Get(items));
        ArgumentSet removed = populated.Remove(items);
        Assert.True(removed.IsRemoved(items));
        Assert.False(removed.TryGet(items, out _));
        Assert.False(empty.IsRemoved(items));
        Assert.Throws<KeyNotFoundException>(() => removed.Get(items));
        Assert.NotEqual(populated.Id, removed.Id);
    }

    [Fact]
    public void DiamondMergesDeduplicateOriginsButExplicitAppendPreservesDuplicates()
    {
        ArgumentKey<IReadOnlyList<string>> key = ArgumentKeys.Sequence<string>("link", value => value);
        ArgumentSet root = new ArgumentSet().Append(key, "a", "a");
        ArgumentSet left = root.Append(key, "left");
        ArgumentSet right = root.Append(key, "right");
        ArgumentSet merged = left.Merge(right);
        Assert.Equal(["a", "a", "left", "right"], merged.Get(key));
        Assert.Equal(merged.Get(key), merged.Merge(root).Get(key));
        Assert.Equal(["a", "a", "left", "right", "a"], merged.Append(key, "a").Get(key));
        Assert.Equal([0, 1], root.Origins(key).Select(origin => origin.ElementIndex));
    }

    [Fact]
    public void SnapshotsDetachMutableCustomValuesAndMetadataOnBothReadAndWrite()
    {
        var key = new ArgumentKey<List<int>>("numbers", value => new List<int>(value),
            (left, right) => left.Concat(right).ToList());
        var numbers = new List<int> { 1 };
        var metadata = new Dictionary<string, string> { ["name"] = "original" };
        ArgumentSet snapshot = new ArgumentSet(metadata).With(key, numbers);
        numbers.Add(2);
        metadata["name"] = "changed";
        snapshot.Get(key).Add(3);
        Assert.Equal([1], snapshot.Get(key));
        Assert.Equal("original", snapshot.Metadata["name"]);
        ArgumentSet renamed = snapshot.WithMetadata(new Dictionary<string, string> { ["name"] = "renamed" });
        Assert.Equal("original", Assert.Single(renamed.Origins(key)).Metadata["name"]);
        Assert.Equal("renamed", renamed.Metadata["name"]);
        Assert.NotEqual(snapshot.Id, renamed.Id);
    }

    [Fact]
    public void ConflictsExposeBothOriginsAndOverrideRetainsIncomingOrigin()
    {
        ArgumentKey<int> key = ArgumentKeys.Scalar<int>("level");
        ArgumentSet left = new ArgumentSet().With(key, 1);
        ArgumentSet right = new ArgumentSet().With(key, 2);
        ArgumentConflictException conflict = Assert.Throws<ArgumentConflictException>(() => left.Merge(right));
        Assert.Equal("level", conflict.Key);
        Assert.Equal(left.Id, conflict.Left.SetId);
        Assert.Equal(right.Id, conflict.Right.SetId);
        ArgumentSet replaced = left.Override(right);
        Assert.Equal(2, replaced.Get(key));
        Assert.Equal(right.Id, Assert.Single(replaced.Origins(key)).SetId);
        Assert.Equal(1, left.Get(key));
    }

    [Fact]
    public void DistinctAndNamedMergePoliciesPreserveCallerIdentityRules()
    {
        ArgumentKey<IReadOnlyList<string>> paths = ArgumentKeys.Sequence<string>("paths", value => value,
            StringComparer.OrdinalIgnoreCase);
        ArgumentKey<IReadOnlyDictionary<string, string?>> macros = ArgumentKeys.Map<string?>("macros", value => value);
        ArgumentSet left = new ArgumentSet().With(paths, ["A", "B"])
            .With(macros, new Dictionary<string, string?> { ["NAME"] = null });
        ArgumentSet right = new ArgumentSet().With(paths, ["b", "C"])
            .With(macros, new Dictionary<string, string?> { ["OTHER"] = "2" });
        ArgumentSet merged = left.Merge(right);
        Assert.Equal(["A", "B", "C"], merged.Get(paths));
        Assert.Equal(2, merged.Get(macros).Count);
        Assert.Throws<ArgumentConflictException>(() => merged.Merge(new ArgumentSet()
            .With(macros, new Dictionary<string, string?> { ["NAME"] = "" })));
        Assert.Equal(["A", "C"], merged.RemoveItems(paths, value => value == "B").Get(paths));
        Assert.False(merged.Filter(key => ReferenceEquals(key, paths)).TryGet(macros, out _));
    }

    [Fact]
    public void DefinitionsCannotReuseIdentifiersEvenWhenTypesMatch()
    {
        ArgumentKey<bool> first = ArgumentKeys.Scalar<bool>("same");
        ArgumentKey<bool> second = ArgumentKeys.Scalar<bool>("same");
        ArgumentSet snapshot = new ArgumentSet().With(first, true);
        Assert.Throws<ArgumentException>(() => snapshot.With(second, true));
        Assert.Throws<ArgumentException>(() => snapshot.Merge(new ArgumentSet().With(second, true)));
        Assert.Throws<ArgumentException>(() => snapshot.TryGet(second, out _));
        Assert.Throws<ArgumentException>(() => snapshot.Remove(first).With(second, true));
        Assert.Throws<ArgumentNullException>(() => snapshot.With<bool>(null!, true));
        Assert.Throws<ArgumentException>(() => ArgumentKeys.Scalar<List<int>>("mutable"));
    }

    [Fact]
    public void TypedReductionRunsOncePerContributionAndCopiesItsInputs()
    {
        ArgumentKey<int> sum = ArgumentKeys.Scalar<int>("sum", (left, right) => left + right);
        ArgumentSet root = new ArgumentSet().With(sum, 1);
        ArgumentSet left = root.Merge(new ArgumentSet().With(sum, 2));
        ArgumentSet right = root.Merge(new ArgumentSet().With(sum, 3));
        Assert.Equal(6, left.Merge(right).Get(sum));
        Assert.Equal(3, left.Get(sum));
    }
}
