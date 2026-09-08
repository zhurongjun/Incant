namespace Incant.CXLegacy.Arguments;

/// <summary>Link output kind; absence selects an executable.</summary>
public enum OutputKind
{
    /// <summary>Links an executable program.</summary>
    Executable,
    /// <summary>Links a shared library.</summary>
    SharedLibrary,
    /// <summary>Links an Apple loadable bundle.</summary>
    AppleBundle,
}

/// <summary>Whether arguments target a compiler driver or a directly invoked Windows linker.</summary>
public enum LinkerDialect
{
    /// <summary>Passes arguments to the compiler driver.</summary>
    Driver,
    /// <summary>Microsoft command syntax.</summary>
    Msvc,
    /// <summary>Direct LLVM lld-link command syntax.</summary>
    LldLink,
}

/// <summary>Semantics of an ordered link input expression.</summary>
public enum LinkInputKind
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
public sealed class LinkInput
{
    private LinkInput(LinkInputKind kind, string? value, IEnumerable<LinkInput>? children)
    {
        Kind = kind;
        Value = value;
        LinkInput[] items = (children ?? []).ToArray();
        if (items.Any(item => item is null))
        {
            throw new ArgumentException("Link children cannot be null.", nameof(children));
        }

        Children = Array.AsReadOnly(items);
    }

    /// <summary>Gets the input semantics.</summary>
    public LinkInputKind Kind { get; }

    /// <summary>Gets the unsplit path or library name for a leaf.</summary>
    public string? Value { get; }

    /// <summary>Gets ordered group children.</summary>
    public IReadOnlyList<LinkInput> Children { get; }

    /// <summary>References an exact input file, without changing its extension.</summary>
    public static LinkInput File(string path) => Leaf(LinkInputKind.File, path);

    /// <summary>Requests the linker's normal library-name search.</summary>
    public static LinkInput Library(string name) => Leaf(LinkInputKind.Library, name);

    /// <summary>Requests an exact filename within library search directories.</summary>
    public static LinkInput ExactLibrary(string name) => Leaf(LinkInputKind.ExactLibrary, name);

    /// <summary>Requests repeated archive search; unsupported linker dialects report an error.</summary>
    public static LinkInput Group(params LinkInput[] inputs) => new(LinkInputKind.Group, null, inputs ?? throw new ArgumentNullException(nameof(inputs)));

    /// <summary>Requests all members of the enclosed archives, without changing surrounding inputs.</summary>
    public static LinkInput WholeArchive(params LinkInput[] inputs) => new(LinkInputKind.WholeArchive, null, inputs ?? throw new ArgumentNullException(nameof(inputs)));

    private static LinkInput Leaf(LinkInputKind kind, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new LinkInput(kind, value, null);
    }
}
