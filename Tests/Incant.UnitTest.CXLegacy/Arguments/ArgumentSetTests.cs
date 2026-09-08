using Incant.CXLegacy.Arguments;

namespace Incant.UnitTest.CXLegacy.Arguments;

public sealed class ArgumentSetTests
{
    [Fact]
    public void UnsetFalseEmptyAndRemovalRemainDistinct()
    {
        var empty = new ArgumentSet();
        ArgumentSet populated = empty.WithWarningsAsErrors(false).WithIncludes([])
            .WithDefines(new Dictionary<string, string?> { ["VALUELESS"] = null, ["EMPTY"] = "" });
        Assert.Null(empty.WarningsAsErrors);
        Assert.Null(empty.Includes);
        Assert.False(populated.WarningsAsErrors);
        Assert.Empty(populated.Includes!);
        Assert.True(populated.IsSet(ArgumentField.Includes));
        Assert.Null(populated.Defines!["VALUELESS"]);
        Assert.Equal("", populated.Defines["EMPTY"]);
        ArgumentSet removed = populated.WithoutIncludes();
        Assert.Null(removed.Includes);
        Assert.True(removed.IsRemoved(ArgumentField.Includes));
        Assert.False(removed.IsSet(ArgumentField.Includes));
        Assert.False(empty.IsRemoved(ArgumentField.Includes));
        Assert.NotEqual(populated.Id, removed.Id);
    }

    [Fact]
    public void DiamondMergesDeduplicateOriginsButExplicitAppendPreservesDuplicates()
    {
        ArgumentSet root = new ArgumentSet().AppendInputs("a.o", "a.o");
        ArgumentSet left = root.AppendInputs("left.o");
        ArgumentSet right = root.AppendInputs("right.o");
        ArgumentSet merged = left.Merge(right);
        Assert.Equal(["a.o", "a.o", "left.o", "right.o"], merged.Inputs);
        Assert.Equal(merged.Inputs, merged.Merge(root).Inputs);
        Assert.Equal(["a.o", "a.o", "left.o", "right.o", "a.o"], merged.AppendInputs("a.o").Inputs);
        Assert.Equal([0, 1], root.Origins(ArgumentField.Inputs).Select(origin => origin.ElementIndex));
    }

    [Fact]
    public void SnapshotsDetachCollectionsAndMetadataAndKeepOriginalSources()
    {
        var includes = new List<string> { "include" };
        var defines = new Dictionary<string, string?> { ["NAME"] = "original" };
        var metadata = new Dictionary<string, string> { ["name"] = "original" };
        ArgumentSet snapshot = new ArgumentSet(metadata).WithIncludes(includes).WithDefines(defines);
        includes.Add("changed");
        defines["NAME"] = "changed";
        metadata["name"] = "changed";
        Assert.Equal(["include"], snapshot.Includes);
        Assert.Equal("original", snapshot.Defines!["NAME"]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)snapshot.Includes!).Add("leaked"));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string?>)snapshot.Defines).Add("leaked", null));
        ArgumentSet renamed = snapshot.WithMetadata(new Dictionary<string, string> { ["name"] = "renamed" });
        Assert.Equal("original", Assert.Single(renamed.Origins(ArgumentField.Defines)).Metadata["name"]);
        Assert.Equal("renamed", renamed.Metadata["name"]);
        Assert.NotEqual(snapshot.Id, renamed.Id);
    }

    [Fact]
    public void ConflictsExposeBothOriginsAndOverrideRetainsIncomingOrigin()
    {
        ArgumentSet left = new ArgumentSet().WithStandard("c++17");
        ArgumentSet right = new ArgumentSet().WithStandard("c++20");
        ArgumentConflictException conflict = Assert.Throws<ArgumentConflictException>(() => left.Merge(right));
        Assert.Equal(ArgumentField.Standard, conflict.Field);
        Assert.Equal(left.Id, conflict.Left.SetId);
        Assert.Equal(right.Id, conflict.Right.SetId);
        ArgumentSet replaced = left.Override(right);
        Assert.Equal("c++20", replaced.Standard);
        Assert.Equal(right.Id, Assert.Single(replaced.Origins(ArgumentField.Standard)).SetId);
        Assert.Equal("c++17", left.Standard);
        Assert.Equal("c++17", left.Merge(new ArgumentSet().WithStandard("c++17")).Standard);
    }

    [Fact]
    public void NamedContributionsConflictAtTheirOriginalSourceAndCanBeRemoved()
    {
        ArgumentSet original = new ArgumentSet().WithDefines(new Dictionary<string, string?> { ["FIRST"] = null });
        ArgumentSet extended = original.AppendDefines(new Dictionary<string, string?> { ["SECOND"] = "2" });
        ArgumentSet conflicting = new ArgumentSet().WithDefines(new Dictionary<string, string?> { ["FIRST"] = "" });
        ArgumentConflictException conflict = Assert.Throws<ArgumentConflictException>(() => extended.Merge(conflicting));
        Assert.Equal(original.Id, conflict.Left.SetId);
        Assert.Equal(conflicting.Id, conflict.Right.SetId);
        ArgumentSet removed = extended.RemoveDefines(name => name == "FIRST");
        Assert.Equal("2", Assert.Single(removed.Defines!).Value);
        Assert.Equal(2, extended.Defines!.Count);
        Assert.Null(extended.Filter(field => field == ArgumentField.Inputs).Defines);
    }

    [Fact]
    public void RemovalMarkersSurviveMergingAndExplicitReplacementRestoresAField()
    {
        ArgumentSet original = new ArgumentSet().WithIncludes(["a", "a", "b"]);
        ArgumentSet marker = new ArgumentSet().WithoutIncludes();
        ArgumentSet removed = original.Merge(marker);
        Assert.True(removed.IsRemoved(ArgumentField.Includes));
        Assert.Equal(marker.Id, Assert.Single(removed.Origins(ArgumentField.Includes)).SetId);
        Assert.Empty(removed.Fields);
        ArgumentSet restored = removed.WithIncludes([]);
        Assert.True(restored.IsSet(ArgumentField.Includes));
        Assert.False(restored.IsRemoved(ArgumentField.Includes));
        Assert.Equal(["a", "a"], original.RemoveIncludes(value => value == "b").Includes);
        Assert.True(marker.Filter(_ => true).IsRemoved(ArgumentField.Includes));
    }

    [Fact]
    public void GeneratedMapAndListOperationsApplyFixedRules()
    {
        ArgumentSet settings = new ArgumentSet().WithWarningControls(new Dictionary<string, bool> { ["unused"] = false })
            .AppendWarningControls(new Dictionary<string, bool> { ["conversion"] = true })
            .AppendInputs("a.o", "b.o").RemoveInputs(value => value == "a.o");
        Assert.Equal(["b.o"], settings.Inputs);
        Assert.True(settings.WarningControls!["conversion"]);
        Assert.Equal("unused", Assert.Single(settings.RemoveWarningControls(name => name == "conversion").WarningControls!).Key);
        Assert.Throws<ArgumentConflictException>(() => settings.AppendWarningControls(new Dictionary<string, bool> { ["unused"] = true }));
    }

    [Fact]
    public void InvalidApiInputsAreRejectedWithoutCreatingDynamicFields()
    {
        var settings = new ArgumentSet();
        Assert.Throws<ArgumentNullException>(() => settings.WithStandard(null!));
        Assert.Throws<ArgumentNullException>(() => settings.WithIncludes(null!));
        Assert.Throws<ArgumentNullException>(() => settings.AppendDefines(null!));
        Assert.Throws<ArgumentNullException>(() => settings.RemoveIncludes(null!));
        Assert.Throws<ArgumentNullException>(() => settings.Merge(null!));
        Assert.Throws<ArgumentNullException>(() => settings.Override(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.IsSet((ArgumentField)(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Origins((ArgumentField)(-1)));
        foreach (ArgumentField field in Enum.GetValues<ArgumentField>())
        {
            Assert.False(settings.IsSet(field));
            Assert.False(settings.IsRemoved(field));
        }
    }
}
