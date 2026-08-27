using System.Text;

namespace SB.Cli;

public static class StyleStrings
{
    public const string Clear = "\u001b[0m";

    // style
    public const string Bold = "\u001b[1m";
    public const string NoBold = "\u001b[22m";
    public const string Underline = "\u001b[4m";
    public const string NoUnderline = "\u001b[24m";
    public const string Reverse = "\u001b[7m";
    public const string NoReverse = "\u001b[27m";

    // front colors
    public const string FrontGray = "\u001b[30m";
    public const string FrontRed = "\u001b[31m";
    public const string FrontGreen = "\u001b[32m";
    public const string FrontYellow = "\u001b[33m";
    public const string FrontBlue = "\u001b[34m";
    public const string FrontMagenta = "\u001b[35m";
    public const string FrontCyan = "\u001b[36m";
    public const string FrontWhite = "\u001b[37m";

    // back colors
    public const string BackGray = "\u001b[40m";
    public const string BackRed = "\u001b[41m";
    public const string BackGreen = "\u001b[42m";
    public const string BackYellow = "\u001b[43m";
    public const string BackBlue = "\u001b[44m";
    public const string BackMagenta = "\u001b[45m";
    public const string BackCyan = "\u001b[46m";
    public const string BackWhite = "\u001b[47m";
}
public class Writer
{
    private StringBuilder _Content = new StringBuilder();
    private uint _IndentCache = 0;

    public bool IsEmpty => _Content.Length == 0;
    public string Content => _Content.ToString();

    #region Style
    public Writer StyleClear()
    {
        _Content.Append(StyleStrings.Clear);
        return this;
    }

    // style
    public Writer StyleBold()
    {
        _Content.Append(StyleStrings.Bold);
        return this;
    }
    public Writer StyleNoBold()
    {
        _Content.Append(StyleStrings.NoBold);
        return this;
    }
    public Writer StyleUnderline()
    {
        _Content.Append(StyleStrings.Underline);
        return this;
    }
    public Writer StyleNoUnderline()
    {
        _Content.Append(StyleStrings.NoUnderline);
        return this;
    }
    public Writer StyleReverse()
    {
        _Content.Append(StyleStrings.Reverse);
        return this;
    }
    public Writer StyleNoReverse()
    {
        _Content.Append(StyleStrings.NoReverse);
        return this;
    }

    // front colors
    public Writer StyleFrontGray()
    {
        _Content.Append(StyleStrings.FrontGray);
        return this;
    }
    public Writer StyleFrontRed()
    {
        _Content.Append(StyleStrings.FrontRed);
        return this;
    }
    public Writer StyleFrontGreen()
    {
        _Content.Append(StyleStrings.FrontGreen);
        return this;
    }
    public Writer StyleFrontYellow()
    {
        _Content.Append(StyleStrings.FrontYellow);
        return this;
    }
    public Writer StyleFrontBlue()
    {
        _Content.Append(StyleStrings.FrontBlue);
        return this;
    }
    public Writer StyleFrontMagenta()
    {
        _Content.Append(StyleStrings.FrontMagenta);
        return this;
    }
    public Writer StyleFrontCyan()
    {
        _Content.Append(StyleStrings.FrontCyan);
        return this;
    }
    public Writer StyleFrontWhite()
    {
        _Content.Append(StyleStrings.FrontWhite);
        return this;
    }

    // back colors
    public Writer StyleBackGray()
    {
        _Content.Append(StyleStrings.BackGray);
        return this;
    }
    public Writer StyleBackRed()
    {
        _Content.Append(StyleStrings.BackRed);
        return this;
    }
    public Writer StyleBackGreen()
    {
        _Content.Append(StyleStrings.BackGreen);
        return this;
    }
    public Writer StyleBackYellow()
    {
        _Content.Append(StyleStrings.BackYellow);
        return this;
    }
    public Writer StyleBackBlue()
    {
        _Content.Append(StyleStrings.BackBlue);
        return this;
    }
    public Writer StyleBackMagenta()
    {
        _Content.Append(StyleStrings.BackMagenta);
        return this;
    }
    public Writer StyleBackCyan()
    {
        _Content.Append(StyleStrings.BackCyan);
        return this;
    }
    public Writer StyleBackWhite()
    {
        _Content.Append(StyleStrings.BackWhite);
        return this;
    }
    #endregion

    #region Build Content
    public Writer Write(string content)
    {
        _Content.Append(content);
        _SolveIndent(content, ref _IndentCache);
        return this;
    }
    public Writer WriteLine(string content)
    {
        _Content.AppendLine(content);
        _IndentCache = 0;
        return this;
    }
    public Writer NextLine()
    {
        _Content.AppendLine();
        _IndentCache = 0;
        return this;
    }
    public Writer WriteKeepIndent(string content)
    {
        // solve indent
        uint newIndent = 0;
        _SolveIndent(content, ref newIndent);

        // apply indent
        if (_IndentCache > 0)
        {
            var indentStr = "\n" + new string(' ', (int)_IndentCache);
            content = content.Replace("\n", indentStr);
        }

        // update state
        _Content.Append(content);
        _IndentCache += newIndent;
        return this;
    }
    #endregion

    #region Output
    public void Dump()
    {
        Console.Write(_Content.ToString());
    }
    #endregion

    #region Helpers
    private void _SolveIndent(string str, ref uint indent)
    {
        var lastNewLine = str.LastIndexOf('\n');
        if (lastNewLine >= 0)
        {
            indent = (uint)(str.Length - lastNewLine - 1);
        }
        else
        {
            indent += (uint)str.Length;
        }
    }
    #endregion
};
