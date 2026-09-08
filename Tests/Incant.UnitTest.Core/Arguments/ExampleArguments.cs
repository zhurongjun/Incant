using Incant.Core.Arguments;

namespace Incant.UnitTest.Core.Arguments;

/// <summary>A standalone, non-toolchain use of configuration keys and generated extensions.</summary>
public static class ExampleArguments
{
    /// <summary>Gets an explicitly set verbosity switch.</summary>
    [GenerateArgument]
    public static ArgumentKey<bool> Verbose { get; } = ArgumentKeys.Scalar<bool>("example.verbose");

    /// <summary>Gets an ordered message list.</summary>
    [GenerateArgument]
    public static ArgumentKey<IReadOnlyList<string>> Messages { get; } = ArgumentKeys.Sequence<string>("example.messages", value => value);

    /// <summary>Gets named message annotations.</summary>
    [GenerateArgument]
    public static ArgumentKey<IReadOnlyDictionary<string, string?>> Annotations { get; } =
        ArgumentKeys.Map<string?>("example.annotations", value => value);
}
