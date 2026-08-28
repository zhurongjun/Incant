using System.Text;

namespace Incant.Base;

/// <summary>Builds text with configurable indentation and line-oriented helpers.</summary>
public class CodeBuilder
{
    /// <summary>Restores the previous indentation level when disposed.</summary>
    public readonly ref struct IndentScopeHolder : IDisposable
    {
        private readonly CodeBuilder _builder;

        /// <summary>Enters one indentation level for the supplied builder.</summary>
        /// <param name="builder">The builder whose indentation is increased.</param>
        public IndentScopeHolder(CodeBuilder builder)
        {
            _builder = builder;
            _builder.PushIndent();
        }

        /// <summary>Leaves the indentation level entered by this scope.</summary>
        public readonly void Dispose()
        {
            _builder.PopIndent();
        }
    }

    private readonly StringBuilder _content = new();
    private uint _currentIndent;

    /// <summary>Gets or sets the number of spaces written for each indentation level.</summary>
    public uint IndentUnit { get; set; } = 4;

    /// <summary>Gets all content written so far.</summary>
    public string Content => _content.ToString();

    /// <summary>Gets a value indicating whether no content has been written.</summary>
    public bool IsEmpty => _content.Length == 0;

    /// <summary>Writes an indented empty line.</summary>
    public void Line()
    {
        _content.Append(' ', (int)(_currentIndent * IndentUnit));
        _content.AppendLine();
    }

    /// <summary>Writes an indented line followed by the platform newline.</summary>
    /// <param name="line">The line content.</param>
    public void Line(string line)
    {
        if (_currentIndent == 0)
        {
            _content.AppendLine(line);
        }
        else
        {
            _content.Append(' ', (int)(_currentIndent * IndentUnit));
            _content.AppendLine(line);
        }
    }

    /// <summary>Writes a line with a temporary number of additional indentation levels.</summary>
    /// <param name="indentLevels">The temporary relative indentation.</param>
    /// <param name="line">The line content.</param>
    public void LineIndent(int indentLevels, string line)
    {
        _currentIndent += (uint)indentLevels;
        Line(line);
        _currentIndent -= (uint)indentLevels;
    }

    /// <summary>
    /// Writes text unchanged at the root level. At nested levels, each split line is indented
    /// and terminated with the platform newline.
    /// </summary>
    /// <param name="text">The text to write.</param>
    public void WriteKeepIndent(string text)
    {
        if (_currentIndent == 0)
        {
            _content.Append(text);
        }
        else
        {
            string[] lines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
            for (int lineIndex = 0; lineIndex < lines.Length; ++lineIndex)
            {
                _content.Append(' ', (int)(_currentIndent * IndentUnit));
                _content.Append(lines[lineIndex]);
                _content.AppendLine();
            }
        }
    }

    /// <summary>Writes a bordered single-line-comment block with left-aligned content.</summary>
    /// <param name="text">The annotation content. Any supported line break separates content lines.</param>
    /// <param name="options">The comment syntax and block spacing.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="OverflowException">The configured dimensions and longest content line exceed the supported width.</exception>
    public void WriteBlockAnnotation(string text, BlockAnnotationOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);

        string[] lines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        int contentWidth = 0;
        foreach (string line in lines)
        {
            contentWidth = Math.Max(contentWidth, line.Length);
        }

        int annotationWidth = checked(
            options.FillThickness
                + options.Padding
                + contentWidth
                + options.Padding
                + options.FillThickness);
        string border = options.LineCommentPrefix + new string(options.FillCharacter, annotationWidth);
        string leftDecoration = options.LineCommentPrefix
            + new string(options.FillCharacter, options.FillThickness)
            + new string(' ', options.Padding);
        string rightDecoration = new string(' ', options.Padding)
            + new string(options.FillCharacter, options.FillThickness);

        Line(border);
        foreach (string line in lines)
        {
            Line(leftDecoration + line.PadRight(contentWidth) + rightDecoration);
        }
        Line(border);
    }

    /// <summary>Writes the generated-file warning using the supplied annotation style.</summary>
    /// <param name="options">The comment syntax and block spacing.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="OverflowException">The configured dimensions exceed the supported width.</exception>
    public void WriteGenerateNote(BlockAnnotationOptions options)
    {
        WriteBlockAnnotation("THIS FILE IS GENERATED, ANY CHANGES WILL BE LOST", options);
        Line(string.Empty);
    }

    /// <summary>Enters a nested indentation level until the returned scope is disposed.</summary>
    /// <returns>A scope that restores the previous indentation level.</returns>
    public IndentScopeHolder IndentScope()
    {
        return new IndentScopeHolder(this);
    }

    /// <summary>Enters one indentation level.</summary>
    /// <param name="levels">
    /// Retained from the legacy API; the legacy behavior enters exactly one level regardless of
    /// the supplied value.
    /// </param>
    public void PushIndent(uint levels = 1)
    {
        _currentIndent++;
    }

    /// <summary>Leaves one indentation level.</summary>
    /// <exception cref="InvalidOperationException">The builder is already at the root level.</exception>
    public void PopIndent()
    {
        if (_currentIndent == 0)
        {
            throw new InvalidOperationException("No indent level to pop.");
        }

        --_currentIndent;
    }

    /// <summary>Returns all content written so far.</summary>
    /// <returns>The accumulated content.</returns>
    public override string ToString()
    {
        return _content.ToString();
    }

    /// <summary>Clears all content and returns indentation to the root level.</summary>
    public void Reset()
    {
        _content.Clear();
        _currentIndent = 0;
    }
}

/// <summary>Defines the syntax and spacing used to write a block annotation.</summary>
public sealed class BlockAnnotationOptions
{
    /// <summary>Options for C source files.</summary>
    public static readonly BlockAnnotationOptions C = CreateDefault("//");

    /// <summary>Options for C++ source files.</summary>
    public static readonly BlockAnnotationOptions Cpp = CreateDefault("//");

    /// <summary>Options for C# source files.</summary>
    public static readonly BlockAnnotationOptions CSharp = CreateDefault("//");

    /// <summary>Options for Java source files.</summary>
    public static readonly BlockAnnotationOptions Java = CreateDefault("//");

    /// <summary>Options for JavaScript source files.</summary>
    public static readonly BlockAnnotationOptions JavaScript = CreateDefault("//");

    /// <summary>Options for TypeScript source files.</summary>
    public static readonly BlockAnnotationOptions TypeScript = CreateDefault("//");

    /// <summary>Options for Python source files.</summary>
    public static readonly BlockAnnotationOptions Python = CreateDefault("#");

    /// <summary>Options for POSIX shell scripts.</summary>
    public static readonly BlockAnnotationOptions Shell = CreateDefault("#");

    /// <summary>Options for PowerShell scripts.</summary>
    public static readonly BlockAnnotationOptions PowerShell = CreateDefault("#");

    /// <summary>Options for Lua source files.</summary>
    public static readonly BlockAnnotationOptions Lua = CreateDefault("--");

    /// <summary>Options for SQL source files.</summary>
    public static readonly BlockAnnotationOptions Sql = CreateDefault("--");

    /// <summary>Initializes block annotation syntax and spacing.</summary>
    /// <param name="lineCommentPrefix">The token that begins a single-line comment.</param>
    /// <param name="fillCharacter">The character used for borders and side fills.</param>
    /// <param name="fillThickness">The number of fill characters on each side of a content line.</param>
    /// <param name="padding">The number of spaces between the content and the fill on each side.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="lineCommentPrefix"/> is empty or contains a line break, or
    /// <paramref name="fillCharacter"/> is a line-break character.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="lineCommentPrefix"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A thickness or padding value is negative.</exception>
    public BlockAnnotationOptions(
        string lineCommentPrefix,
        char fillCharacter,
        int fillThickness,
        int padding)
    {
        ArgumentException.ThrowIfNullOrEmpty(lineCommentPrefix);
        ArgumentOutOfRangeException.ThrowIfNegative(fillThickness);
        ArgumentOutOfRangeException.ThrowIfNegative(padding);

        if (lineCommentPrefix.Contains('\r') || lineCommentPrefix.Contains('\n'))
        {
            throw new ArgumentException("The line comment prefix cannot contain a line break.", nameof(lineCommentPrefix));
        }

        if (fillCharacter is '\r' or '\n')
        {
            throw new ArgumentException("The fill character cannot be a line break.", nameof(fillCharacter));
        }

        LineCommentPrefix = lineCommentPrefix;
        FillCharacter = fillCharacter;
        FillThickness = fillThickness;
        Padding = padding;
    }

    /// <summary>Gets the token that begins each annotation line.</summary>
    public string LineCommentPrefix { get; }

    /// <summary>Gets the character used for borders and side fills.</summary>
    public char FillCharacter { get; }

    /// <summary>Gets the number of fill characters on each side of a content line.</summary>
    public int FillThickness { get; }

    /// <summary>Gets the number of spaces between the content and the fill on each side.</summary>
    public int Padding { get; }

    private static BlockAnnotationOptions CreateDefault(string lineCommentPrefix)
    {
        return new BlockAnnotationOptions(lineCommentPrefix, '!', 2, 1);
    }
}
