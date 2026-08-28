using System.Text;

namespace Incant.Base.Cli;

/// <summary>Provides ANSI escape sequences used by CLI writers.</summary>
public static class StyleStrings
{
    /// <summary>Gets the ANSI sequence that resets all text styles.</summary>
    public const string Clear = "\u001b[0m";

    /// <summary>Gets the ANSI sequence that enables bold text.</summary>
    public const string Bold = "\u001b[1m";

    /// <summary>Gets the ANSI sequence that disables bold text.</summary>
    public const string NoBold = "\u001b[22m";

    /// <summary>Gets the ANSI sequence that enables underlined text.</summary>
    public const string Underline = "\u001b[4m";

    /// <summary>Gets the ANSI sequence that disables underlined text.</summary>
    public const string NoUnderline = "\u001b[24m";

    /// <summary>Gets the ANSI sequence that enables reversed foreground and background colors.</summary>
    public const string Reverse = "\u001b[7m";

    /// <summary>Gets the ANSI sequence that disables reversed foreground and background colors.</summary>
    public const string NoReverse = "\u001b[27m";

    /// <summary>Gets the ANSI SGR 30 foreground sequence exposed by this API as gray.</summary>
    public const string FrontGray = "\u001b[30m";

    /// <summary>Gets the ANSI sequence for a red foreground.</summary>
    public const string FrontRed = "\u001b[31m";

    /// <summary>Gets the ANSI sequence for a green foreground.</summary>
    public const string FrontGreen = "\u001b[32m";

    /// <summary>Gets the ANSI sequence for a yellow foreground.</summary>
    public const string FrontYellow = "\u001b[33m";

    /// <summary>Gets the ANSI sequence for a blue foreground.</summary>
    public const string FrontBlue = "\u001b[34m";

    /// <summary>Gets the ANSI sequence for a magenta foreground.</summary>
    public const string FrontMagenta = "\u001b[35m";

    /// <summary>Gets the ANSI sequence for a cyan foreground.</summary>
    public const string FrontCyan = "\u001b[36m";

    /// <summary>Gets the ANSI sequence for a white foreground.</summary>
    public const string FrontWhite = "\u001b[37m";

    /// <summary>Gets the ANSI SGR 40 background sequence exposed by this API as gray.</summary>
    public const string BackGray = "\u001b[40m";

    /// <summary>Gets the ANSI sequence for a red background.</summary>
    public const string BackRed = "\u001b[41m";

    /// <summary>Gets the ANSI sequence for a green background.</summary>
    public const string BackGreen = "\u001b[42m";

    /// <summary>Gets the ANSI sequence for a yellow background.</summary>
    public const string BackYellow = "\u001b[43m";

    /// <summary>Gets the ANSI sequence for a blue background.</summary>
    public const string BackBlue = "\u001b[44m";

    /// <summary>Gets the ANSI sequence for a magenta background.</summary>
    public const string BackMagenta = "\u001b[45m";

    /// <summary>Gets the ANSI sequence for a cyan background.</summary>
    public const string BackCyan = "\u001b[46m";

    /// <summary>Gets the ANSI sequence for a white background.</summary>
    public const string BackWhite = "\u001b[47m";
}

/// <summary>Builds styled console text while preserving multiline indentation.</summary>
public class Writer
{
    private readonly StringBuilder _content = new();
    private uint _currentIndent;

    /// <summary>Gets a value indicating whether no content has been written.</summary>
    public bool IsEmpty => _content.Length == 0;

    /// <summary>Gets all content written so far.</summary>
    public string Content => _content.ToString();

    #region Style
    /// <summary>Appends the ANSI sequence that resets all text styles.</summary>
    /// <returns>This writer for fluent calls.</returns>
    public Writer StyleClear()
    {
        _content.Append(StyleStrings.Clear);
        return this;
    }

    /// <summary>Appends the ANSI sequence that enables bold text.</summary>
    /// <returns>This writer for fluent calls.</returns>
    public Writer StyleBold()
    {
        _content.Append(StyleStrings.Bold);
        return this;
    }

    /// <summary>Appends the ANSI sequence that disables bold text.</summary>
    /// <returns>This writer for fluent calls.</returns>
    public Writer StyleNoBold()
    {
        _content.Append(StyleStrings.NoBold);
        return this;
    }

    /// <summary>Appends the ANSI sequence that enables underlined text.</summary>
    /// <returns>This writer for fluent calls.</returns>
    public Writer StyleUnderline()
    {
        _content.Append(StyleStrings.Underline);
        return this;
    }

    /// <summary>Appends the ANSI sequence that disables underlined text.</summary>
    /// <returns>This writer for fluent calls.</returns>
    public Writer StyleNoUnderline()
    {
        _content.Append(StyleStrings.NoUnderline);
        return this;
    }

    /// <summary>Appends the ANSI sequence that enables reversed colors.</summary>
    /// <returns>This writer for fluent calls.</returns>
    public Writer StyleReverse()
    {
        _content.Append(StyleStrings.Reverse);
        return this;
    }

    /// <summary>Appends the ANSI sequence that disables reversed colors.</summary>
    /// <returns>This writer for fluent calls.</returns>
    public Writer StyleNoReverse()
    {
        _content.Append(StyleStrings.NoReverse);
        return this;
    }

    /// <summary>Appends the ANSI SGR 30 foreground sequence exposed by this API as gray.</summary>
    /// <returns>This writer for fluent calls.</returns>
    public Writer StyleFrontGray()
    {
        _content.Append(StyleStrings.FrontGray);
        return this;
    }

    /// <summary>Appends the ANSI sequence for a red foreground.</summary>
    /// <returns>This writer for fluent calls.</returns>
    public Writer StyleFrontRed()
    {
        _content.Append(StyleStrings.FrontRed);
        return this;
    }

    /// <summary>Appends the ANSI sequence for a green foreground.</summary>
    /// <returns>This writer for fluent calls.</returns>
    public Writer StyleFrontGreen()
    {
        _content.Append(StyleStrings.FrontGreen);
        return this;
    }

    /// <summary>Appends the ANSI sequence for a yellow foreground.</summary>
    /// <returns>This writer for fluent calls.</returns>
    public Writer StyleFrontYellow()
    {
        _content.Append(StyleStrings.FrontYellow);
        return this;
    }

    /// <summary>Appends the ANSI sequence for a blue foreground.</summary>
    /// <returns>This writer for fluent calls.</returns>
    public Writer StyleFrontBlue()
    {
        _content.Append(StyleStrings.FrontBlue);
        return this;
    }

    /// <summary>Appends the ANSI sequence for a magenta foreground.</summary>
    /// <returns>This writer for fluent calls.</returns>
    public Writer StyleFrontMagenta()
    {
        _content.Append(StyleStrings.FrontMagenta);
        return this;
    }

    /// <summary>Appends the ANSI sequence for a cyan foreground.</summary>
    /// <returns>This writer for fluent calls.</returns>
    public Writer StyleFrontCyan()
    {
        _content.Append(StyleStrings.FrontCyan);
        return this;
    }

    /// <summary>Appends the ANSI sequence for a white foreground.</summary>
    /// <returns>This writer for fluent calls.</returns>
    public Writer StyleFrontWhite()
    {
        _content.Append(StyleStrings.FrontWhite);
        return this;
    }

    /// <summary>Appends the ANSI SGR 40 background sequence exposed by this API as gray.</summary>
    /// <returns>This writer for fluent calls.</returns>
    public Writer StyleBackGray()
    {
        _content.Append(StyleStrings.BackGray);
        return this;
    }

    /// <summary>Appends the ANSI sequence for a red background.</summary>
    /// <returns>This writer for fluent calls.</returns>
    public Writer StyleBackRed()
    {
        _content.Append(StyleStrings.BackRed);
        return this;
    }

    /// <summary>Appends the ANSI sequence for a green background.</summary>
    /// <returns>This writer for fluent calls.</returns>
    public Writer StyleBackGreen()
    {
        _content.Append(StyleStrings.BackGreen);
        return this;
    }

    /// <summary>Appends the ANSI sequence for a yellow background.</summary>
    /// <returns>This writer for fluent calls.</returns>
    public Writer StyleBackYellow()
    {
        _content.Append(StyleStrings.BackYellow);
        return this;
    }

    /// <summary>Appends the ANSI sequence for a blue background.</summary>
    /// <returns>This writer for fluent calls.</returns>
    public Writer StyleBackBlue()
    {
        _content.Append(StyleStrings.BackBlue);
        return this;
    }

    /// <summary>Appends the ANSI sequence for a magenta background.</summary>
    /// <returns>This writer for fluent calls.</returns>
    public Writer StyleBackMagenta()
    {
        _content.Append(StyleStrings.BackMagenta);
        return this;
    }

    /// <summary>Appends the ANSI sequence for a cyan background.</summary>
    /// <returns>This writer for fluent calls.</returns>
    public Writer StyleBackCyan()
    {
        _content.Append(StyleStrings.BackCyan);
        return this;
    }

    /// <summary>Appends the ANSI sequence for a white background.</summary>
    /// <returns>This writer for fluent calls.</returns>
    public Writer StyleBackWhite()
    {
        _content.Append(StyleStrings.BackWhite);
        return this;
    }
    #endregion

    #region Build Content
    /// <summary>Appends text and updates the current multiline indentation.</summary>
    /// <param name="content">The text to append.</param>
    /// <returns>This writer for fluent calls.</returns>
    public Writer Write(string content)
    {
        _content.Append(content);
        UpdateIndent(content, ref _currentIndent);
        return this;
    }

    /// <summary>Appends text followed by the platform newline.</summary>
    /// <param name="content">The text to append.</param>
    /// <returns>This writer for fluent calls.</returns>
    public Writer WriteLine(string content)
    {
        _content.AppendLine(content);
        _currentIndent = 0;
        return this;
    }

    /// <summary>Appends the platform newline.</summary>
    /// <returns>This writer for fluent calls.</returns>
    public Writer NextLine()
    {
        _content.AppendLine();
        _currentIndent = 0;
        return this;
    }

    /// <summary>Appends multiline text and aligns continuation lines to the current column.</summary>
    /// <param name="content">The text to append.</param>
    /// <returns>This writer for fluent calls.</returns>
    public Writer WriteKeepIndent(string content)
    {
        uint appendedIndent = 0;
        UpdateIndent(content, ref appendedIndent);

        if (_currentIndent > 0)
        {
            string indentation = "\n" + new string(' ', (int)_currentIndent);
            content = content.Replace("\n", indentation);
        }

        _content.Append(content);
        _currentIndent += appendedIndent;
        return this;
    }
    #endregion

    #region Output
    /// <summary>Writes the accumulated content to standard output.</summary>
    public void Dump()
    {
        Console.Write(_content.ToString());
    }
    #endregion

    #region Helpers
    private static void UpdateIndent(string text, ref uint indent)
    {
        int lastNewLine = text.LastIndexOf('\n');
        if (lastNewLine >= 0)
        {
            indent = (uint)(text.Length - lastNewLine - 1);
        }
        else
        {
            indent += (uint)text.Length;
        }
    }
    #endregion
}
