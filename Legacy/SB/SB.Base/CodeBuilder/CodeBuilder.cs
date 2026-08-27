using System.Text;
namespace SB;

public class CodeBuilder
{
    public uint IndentUnit { get; set; } = 4;
    private uint _CurIndent = 0;
    private StringBuilder _Content = new();
    public string Content => _Content.ToString();

    // empty
    public bool IsEmpty => _Content.Length == 0;

    public ref struct IndentScopeHolder : IDisposable
    {
        private CodeBuilder _Builder;
        public IndentScopeHolder(CodeBuilder builder)
        {
            _Builder = builder;
            _Builder.PushIndent();
        }
        public readonly void Dispose()
        {
            _Builder.PopIndent();
        }
    }

    // write
    public void Line()
    {
        _Content.Append(' ', (int)(_CurIndent * IndentUnit));
        _Content.AppendLine();
    }
    public void Line(string line)
    {
        if (_CurIndent == 0)
        {
            _Content.AppendLine(line);
        }
        else
        {
            _Content.Append(' ', (int)(_CurIndent * IndentUnit));
            _Content.AppendLine(line);
        }
    }
    public void LineIndent(int indentLevels, string line)
    {
        _CurIndent += (uint)indentLevels;
        Line(line);
        _CurIndent -= (uint)indentLevels;
    }
    public void WriteKeepIndent(string text)
    {
        if (_CurIndent == 0)
        {
            _Content.Append(text);
        }
        else
        {
            // split lines
            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                _Content.Append(' ', (int)(_CurIndent * IndentUnit));
                _Content.Append(lines[i]);
                _Content.AppendLine();
            }
        }
    }
    public void WriteGenerateNote()
    {
        Line("//!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
        Line("//!! THIS FILE IS GENERATED, ANY CHANGES WILL BE LOST !!");
        Line("//!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
        Line("");
    }
    public IndentScopeHolder IndentScope()
    {
        return new IndentScopeHolder(this);
    }

    // indent helper
    public void PushIndent(uint levels = 1)
    {
        _CurIndent++;
    }
    public void PopIndent()
    {
        if (_CurIndent == 0)
            throw new InvalidOperationException("No indent level to pop.");

        --_CurIndent;
    }

    // get content
    public override string ToString()
    {
        return _Content.ToString();
    }

    // reset
    public void Reset()
    {
        _Content.Clear();
        _CurIndent = 0;
    }
}
