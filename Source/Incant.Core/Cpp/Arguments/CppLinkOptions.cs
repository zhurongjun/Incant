namespace Incant.Core.Cpp.Arguments;

/// <summary>Link output kind; absence selects an executable.</summary>
public enum CppOutputKind
{
    /// <summary>Links an executable program.</summary>
    Executable,
    /// <summary>SharedSearches for a library by logical name.</summary>
    SharedLibrary,
    /// <summary>Links an Apple loadable bundle.</summary>
    AppleBundle,
}

/// <summary>Whether arguments target a compiler driver or a directly invoked Windows linker.</summary>
public enum CppLinkerDialect
{
    /// <summary>Passes arguments to the compiler driver.</summary>
    Driver,
    /// <summary>Microsoft command syntax.</summary>
    Msvc,
    /// <summary>Direct LLVM lld-link command syntax.</summary>
    LldLink,
}

/// <summary>Semantics of an ordered link input expression.</summary>
public enum CppLinkInputKind
{
    /// <summary>Names an exact input file.</summary>
    File,
    /// <summary>Searches for a library by logical name.</summary>
    Library,
    /// <summary>Searches for a literal library filename.</summary>
    ExactLibrary,
    /// <summary>Repeats archive search across the enclosed ordered inputs.</summary>
    Group,
    /// <summary>Includes all members of the enclosed archives.</summary>
    WholeArchive,
}

/// <summary>An immutable ordered link expression. Children and duplicates retain declaration order.</summary>
public sealed class CppLinkInput
{
    private CppLinkInput(CppLinkInputKind kind, string? value, IEnumerable<CppLinkInput>? children)
    {
        Kind = kind;
        Value = value;
        CppLinkInput[] items = (children ?? []).ToArray();
        if (items.Any(item => item is null))
        {
            throw new ArgumentException("Link children cannot be null.", nameof(children));
        }

        Children = Array.AsReadOnly(items);
    }

    /// <summary>Gets the input semantics.</summary>
    public CppLinkInputKind Kind { get; }

    /// <summary>Gets the unsplit path or library name for a leaf.</summary>
    public string? Value { get; }

    /// <summary>Gets ordered group children.</summary>
    public IReadOnlyList<CppLinkInput> Children { get; }

    /// <summary>References an exact input file, without changing its extension.</summary>
    public static CppLinkInput File(string path) => Leaf(CppLinkInputKind.File, path);

    /// <summary>Requests the linker's normal library-name search.</summary>
    public static CppLinkInput Library(string name) => Leaf(CppLinkInputKind.Library, name);

    /// <summary>Requests an exact filename within library search directories.</summary>
    public static CppLinkInput ExactLibrary(string name) => Leaf(CppLinkInputKind.ExactLibrary, name);

    /// <summary>Requests repeated archive search; unsupported linker dialects report an error.</summary>
    public static CppLinkInput Group(params CppLinkInput[] inputs) => new(CppLinkInputKind.Group, null, inputs ?? throw new ArgumentNullException(nameof(inputs)));

    /// <summary>Requests all members of the enclosed archives, without changing surrounding inputs.</summary>
    public static CppLinkInput WholeArchive(params CppLinkInput[] inputs) => new(CppLinkInputKind.WholeArchive, null, inputs ?? throw new ArgumentNullException(nameof(inputs)));

    private static CppLinkInput Leaf(CppLinkInputKind kind, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new CppLinkInput(kind, value, null);
    }
}
