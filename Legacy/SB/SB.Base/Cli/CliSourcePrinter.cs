namespace SB.Cli;

public struct SourceLineData
{
    public required int Start;
    public required int End;
    public int Length => End - Start;
};

public class SourcePrinter
{
    private string _Text;
    private List<SourceLineData> _Lines;
    private int _MaxLineNumberWidth;
    public string Text => _Text;
    public IReadOnlyList<SourceLineData> Lines => _Lines;

    public SourcePrinter(string text)
    {
        _Text = text;

        // parse lines
        _Lines = new List<SourceLineData>();
        int lineStart = 0;
        while (lineStart < text.Length)
        {
            // find line end
            int lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = text.Length;

            // add line data
            _Lines.Add(new SourceLineData()
            {
                Start = lineStart,
                End = lineEnd
            });

            // update search start
            lineStart = lineEnd + 1;
        }

        // calculate max line number width
        _MaxLineNumberWidth = _Lines.Count.ToString().Length;
    }

    public ReadOnlySpan<char> LineAt(int lineIndex)
    {
        var lineData = _Lines[lineIndex];
        return _Text.AsSpan(lineData.Start, lineData.Length);
    }
    public int SourcePosToLineIndex(int sourcePos)
    {
        for (int i = 0; i < _Lines.Count; i++)
        {
            var lineData = _Lines[i];
            if (sourcePos >= lineData.Start && sourcePos <= lineData.End)
                return i;
        }
        return -1;
    }

    public void PrintLine(Writer writer, int lineIndex, bool withLineNumber = true)
    {
        if (withLineNumber) _PrintLineNumber(writer, lineIndex + 1);
        writer.WriteLine(LineAt(lineIndex).ToString());
    }
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
    public void PrintIndicatedSourceSpan(
        Writer writer,
        int start,
        int end,
        int previewLineCount = 2,
        bool withLineNumber = true
    )
    {
        int startLine = SourcePosToLineIndex(start);
        int endLine = SourcePosToLineIndex(end);
        if (startLine < 0 || endLine < 0)
            return;

        // print preview lines before
        int previewStartLine = Math.Max(0, startLine - previewLineCount);
        PrintLines(writer, previewStartLine, startLine - previewStartLine, withLineNumber);

        // print indicated lines
        for (int lineIndex = startLine; lineIndex <= endLine; lineIndex++)
        {
            PrintLine(writer, lineIndex, withLineNumber);

            // print indicator
            var lineData = _Lines[lineIndex];
            int indicatorStartCol = int.Max(0, start - lineData.Start);
            int indicatorEndCol = int.Min(lineData.Length, end - lineData.Start);
            int indicatorLength = indicatorEndCol - indicatorStartCol;
            _PrintIndicator(writer, indicatorStartCol, indicatorLength, withLineNumber);
        }

        // print preview lines after
        int previewEndLine = Math.Min(_Lines.Count - 1, endLine + previewLineCount);
        PrintLines(writer, endLine + 1, previewEndLine - endLine, withLineNumber);
    }

    private void _PrintLineNumber(Writer writer, int lineNum)
    {
        writer.Write(lineNum.ToString().PadRight(_MaxLineNumberWidth));
        writer.Write("|");
    }
    private void _PrintLineNumberPlaceholder(Writer writer)
    {
        for (int i = 0; i < _MaxLineNumberWidth; i++)
            writer.Write(" ");
        writer.Write("|");
    }
    private void _PrintIndicator(Writer writer, int startCol, int length, bool withLineNumber = true)
    {
        if (withLineNumber) _PrintLineNumberPlaceholder(writer);
        writer.StyleFrontGreen();
        for (int i = 0; i < startCol; i++)
            writer.Write(" ");
        for (int i = 0; i < length; i++)
            writer.Write("^");
        writer.StyleClear();
        writer.NextLine();
    }
}
