namespace Incant.Base.Cli;

/// <summary>Describes a line range in the original source text.</summary>
public struct SourceLineData
{
    /// <summary>Gets or sets the inclusive start position of the line.</summary>
    public required int Start { get; set; }

    /// <summary>Gets or sets the exclusive end position of the line.</summary>
    public required int End { get; set; }

    /// <summary>Gets the number of characters in the line.</summary>
    public int Length => End - Start;
}

/// <summary>Formats source lines and highlighted source spans for console output.</summary>
public class SourcePrinter
{
    private readonly string _text;
    private readonly List<SourceLineData> _lines;
    private readonly int _maxLineNumberWidth;

    /// <summary>Gets the original source text.</summary>
    public string Text => _text;

    /// <summary>Gets the indexed source-line ranges.</summary>
    public IReadOnlyList<SourceLineData> Lines => _lines;

    /// <summary>Initializes a source printer and indexes its lines.</summary>
    /// <param name="text">The source text to format.</param>
    public SourcePrinter(string text)
    {
        _text = text;

        _lines = [];
        int lineStart = 0;
        while (lineStart < text.Length)
        {
            int lineFeedIndex = text.IndexOf('\n', lineStart);
            int lineEnd = lineFeedIndex < 0 ? text.Length : lineFeedIndex;
            if (lineFeedIndex >= 0
                && lineEnd > lineStart
                && text[lineEnd - 1] == '\r')
            {
                --lineEnd;
            }

            _lines.Add(new SourceLineData
            {
                Start = lineStart,
                End = lineEnd
            });

            if (lineFeedIndex < 0)
            {
                break;
            }

            lineStart = lineFeedIndex + 1;
        }

        _maxLineNumberWidth = _lines.Count.ToString().Length;
    }

    /// <summary>Gets the text of a source line without its newline character.</summary>
    /// <param name="lineIndex">The zero-based source-line index.</param>
    /// <returns>A span over the requested line.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The line index is outside the indexed source.</exception>
    public ReadOnlySpan<char> LineAt(int lineIndex)
    {
        SourceLineData lineData = _lines[lineIndex];
        return _text.AsSpan(lineData.Start, lineData.Length);
    }

    /// <summary>Finds the source line that contains a character position or line-end boundary.</summary>
    /// <param name="sourcePosition">The zero-based source position.</param>
    /// <returns>The zero-based line index, or <c>-1</c> when the position is outside the source.</returns>
    public int SourcePositionToLineIndex(int sourcePosition)
    {
        for (int i = 0; i < _lines.Count; i++)
        {
            SourceLineData lineData = _lines[i];
            int lineBoundaryEnd = GetLineBoundaryEnd(i);
            if (sourcePosition >= lineData.Start && sourcePosition <= lineBoundaryEnd)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Writes one source line.</summary>
    /// <param name="writer">The destination writer.</param>
    /// <param name="lineIndex">The zero-based line index.</param>
    /// <param name="withLineNumber">Whether to prefix the line with its one-based line number.</param>
    public void PrintLine(Writer writer, int lineIndex, bool withLineNumber = true)
    {
        if (withLineNumber)
        {
            PrintLineNumber(writer, lineIndex + 1);
        }

        writer.WriteLine(LineAt(lineIndex).ToString());
    }
    /// <summary>Writes a contiguous range of source lines.</summary>
    /// <param name="writer">The destination writer.</param>
    /// <param name="startLine">The zero-based first line index.</param>
    /// <param name="lineCount">The number of lines to write.</param>
    /// <param name="withLineNumber">Whether to prefix lines with one-based line numbers.</param>
    public void PrintLines(
        Writer writer,
        int startLine,
        int lineCount,
        bool withLineNumber = true
    )
    {
        for (int i = 0; i < lineCount; i++)
        {
            PrintLine(writer, startLine + i, withLineNumber);
        }
    }
    /// <summary>Writes a highlighted source span with optional context lines.</summary>
    /// <param name="writer">The destination writer.</param>
    /// <param name="start">The inclusive source-span start position.</param>
    /// <param name="end">The exclusive source-span end position.</param>
    /// <param name="previewLineCount">The number of context lines before and after the span.</param>
    /// <param name="withLineNumber">Whether to prefix lines with one-based line numbers.</param>
    public void PrintIndicatedSourceSpan(
        Writer writer,
        int start,
        int end,
        int previewLineCount = 2,
        bool withLineNumber = true
    )
    {
        int startLine = SourcePositionToLineIndex(start);
        int endLine = SourcePositionToLineIndex(end);
        if (startLine < 0 || endLine < 0)
        {
            return;
        }

        int previewStartLine = Math.Max(0, startLine - previewLineCount);
        PrintLines(writer, previewStartLine, startLine - previewStartLine, withLineNumber);

        for (int lineIndex = startLine; lineIndex <= endLine; lineIndex++)
        {
            PrintLine(writer, lineIndex, withLineNumber);

            SourceLineData lineData = _lines[lineIndex];
            int indicatorStartColumn = int.Clamp(start - lineData.Start, 0, lineData.Length);
            int indicatorEndColumn = int.Clamp(end - lineData.Start, 0, lineData.Length);
            int indicatorLength = int.Max(0, indicatorEndColumn - indicatorStartColumn);
            PrintIndicator(writer, indicatorStartColumn, indicatorLength, withLineNumber);
        }

        int previewEndLine = Math.Min(_lines.Count - 1, endLine + previewLineCount);
        PrintLines(writer, endLine + 1, previewEndLine - endLine, withLineNumber);
    }

    private int GetLineBoundaryEnd(int lineIndex)
    {
        if (lineIndex + 1 < _lines.Count)
        {
            return _lines[lineIndex + 1].Start - 1;
        }

        bool hasTrailingLineFeed = _text.Length != 0 && _text[^1] == '\n';
        return hasTrailingLineFeed ? _text.Length - 1 : _text.Length;
    }

    private void PrintLineNumber(Writer writer, int lineNumber)
    {
        writer.Write(lineNumber.ToString().PadRight(_maxLineNumberWidth));
        writer.Write("|");
    }
    private void PrintLineNumberPlaceholder(Writer writer)
    {
        for (int i = 0; i < _maxLineNumberWidth; i++)
        {
            writer.Write(" ");
        }
        writer.Write("|");
    }
    private void PrintIndicator(Writer writer, int startColumn, int length, bool withLineNumber = true)
    {
        if (withLineNumber)
        {
            PrintLineNumberPlaceholder(writer);
        }

        writer.StyleFrontGreen();
        for (int i = 0; i < startColumn; i++)
        {
            writer.Write(" ");
        }

        for (int i = 0; i < length; i++)
        {
            writer.Write("^");
        }

        writer.StyleClear();
        writer.NextLine();
    }
}
