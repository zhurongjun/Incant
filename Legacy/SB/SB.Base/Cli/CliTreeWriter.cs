using System.Text;

namespace SB.Cli;

public class TreeWriter
{
    public enum EIndentNode : byte
    {
        Empty,      // means last node in this indent level
        IndentHint, // means not last node in this indent level
        NodeEntry,  // means entry a sub node
        NodeExit,   // mean exit a sub node
    };
    public class LineData
    {
        public StringBuilder Content;
        public required uint Indent;
        public EIndentNode[]? IndentNodes = null;

        public LineData()
        {
            Content = new StringBuilder();
            Indent = 0;
        }
    }
    public ref struct IndentScopeHolder : IDisposable
    {
        private TreeWriter _Writer;

        public IndentScopeHolder(TreeWriter writer)
        {
            _Writer = writer;
            ++_Writer._CurrentIndent;
        }

        public readonly void Dispose()
        {
            --_Writer._CurrentIndent;
        }
    }

    private List<LineData> _Lines = [];
    private uint _CurrentIndent = 0;
    private bool _IsLineStart = true;

    #region Build Content
    public TreeWriter Write(string text)
    {
        // new line
        if (_IsLineStart)
        {
            _Lines.Add(new LineData()
            {
                Indent = _CurrentIndent,
            });
            _IsLineStart = false;
        }

        // append content
        _Lines[^1].Content.Append(text);
        return this;
    }
    public TreeWriter WriteLine(string text)
    {
        // write content
        Write(text);

        // finish line
        EndLine();
        return this;
    }
    public TreeWriter WriteLine()
    {
        WriteLine(string.Empty);
        return this;
    }
    public TreeWriter EndLine()
    {
        _IsLineStart = true;
        return this;
    }
    public IndentScopeHolder IndentScope()
    {
        return new IndentScopeHolder(this);
    }
    #endregion

    #region Style
    public TreeWriter StyleClear()
    {
        Write(StyleStrings.Clear);
        return this;
    }

    // style
    public TreeWriter StyleBold()
    {
        Write(StyleStrings.Bold);
        return this;
    }
    public TreeWriter StyleNoBold()
    {
        Write(StyleStrings.NoBold);
        return this;
    }
    public TreeWriter StyleUnderline()
    {
        Write(StyleStrings.Underline);
        return this;
    }
    public TreeWriter StyleNoUnderline()
    {
        Write(StyleStrings.NoUnderline);
        return this;
    }
    public TreeWriter StyleReverse()
    {
        Write(StyleStrings.Reverse);
        return this;
    }
    public TreeWriter StyleNoReverse()
    {
        Write(StyleStrings.NoReverse);
        return this;
    }

    // front colors
    public TreeWriter StyleFrontGray()
    {
        Write(StyleStrings.FrontGray);
        return this;
    }
    public TreeWriter StyleFrontRed()
    {
        Write(StyleStrings.FrontRed);
        return this;
    }
    public TreeWriter StyleFrontGreen()
    {
        Write(StyleStrings.FrontGreen);
        return this;
    }
    public TreeWriter StyleFrontYellow()
    {
        Write(StyleStrings.FrontYellow);
        return this;
    }
    public TreeWriter StyleFrontBlue()
    {
        Write(StyleStrings.FrontBlue);
        return this;
    }
    public TreeWriter StyleFrontMagenta()
    {
        Write(StyleStrings.FrontMagenta);
        return this;
    }
    public TreeWriter StyleFrontCyan()
    {
        Write(StyleStrings.FrontCyan);
        return this;
    }
    public TreeWriter StyleFrontWhite()
    {
        Write(StyleStrings.FrontWhite);
        return this;
    }

    // back colors
    public TreeWriter StyleBackGray()
    {
        Write(StyleStrings.BackGray);
        return this;
    }
    public TreeWriter StyleBackRed()
    {
        Write(StyleStrings.BackRed);
        return this;
    }
    public TreeWriter StyleBackGreen()
    {
        Write(StyleStrings.BackGreen);
        return this;
    }
    public TreeWriter StyleBackYellow()
    {
        Write(StyleStrings.BackYellow);
        return this;
    }
    public TreeWriter StyleBackBlue()
    {
        Write(StyleStrings.BackBlue);
        return this;
    }
    public TreeWriter StyleBackMagenta()
    {
        Write(StyleStrings.BackMagenta);
        return this;
    }
    public TreeWriter StyleBackCyan()
    {
        Write(StyleStrings.BackCyan);
        return this;
    }
    public TreeWriter StyleBackWhite()
    {
        Write(StyleStrings.BackWhite);
        return this;
    }
    #endregion

    #region Build Output
    public void Dump(Action<LineData, StringBuilder>? buildLineFunc = null)
    {
        Console.Write(Build(buildLineFunc));
    }
    public string Build(Action<LineData, StringBuilder>? buildLineFunc = null)
    {
        if (_Lines.Count == 0)
        {
            return "";
        }

        SolveIndent();
        return BuildWithOutSolveIndent(buildLineFunc);
    }
    public string BuildWithOutSolveIndent(Action<LineData, StringBuilder>? buildLineFunc = null)
    {
        if (_Lines.Count == 0)
        {
            return "";
        }

        buildLineFunc ??= _DefaultBuildLine;
        StringBuilder builder = new StringBuilder();
        for (int line_idx = 0; line_idx < _Lines.Count; ++line_idx)
        {
            if (_Lines[line_idx].IndentNodes == null) throw new InvalidOperationException("Indent nodes is null, please call SolveIndent first or use Build method.");
            buildLineFunc(_Lines[line_idx], builder);
            builder.AppendLine();
        }
        return builder.ToString();
    }
    public void SolveIndent()
    {
        // get max indent and resize indent nodes
        uint maxIndent = 0;
        foreach (var lineData in _Lines)
        {
            maxIndent = Math.Max(maxIndent, lineData.Indent);
            lineData.IndentNodes = new EIndentNode[lineData.Indent + 1];
            for (uint i = 0; i <= lineData.Indent; ++i)
            {
                lineData.IndentNodes[i] = EIndentNode.Empty;
            }
        }

        // last enter cache for detect last node
        List<uint> lastEnterLine = new();
        for (uint i = 0; i <= maxIndent; ++i)
        {
            lastEnterLine.Add(uint.MaxValue);
        }

        // process first line
        uint curIndent;
        {
            var lineData = _Lines[0];
            curIndent = lineData.Indent;

            // record entry node
            for (uint i = 0; i <= curIndent; ++i)
            {
                lastEnterLine[(int)i] = 0;
                lineData.IndentNodes![i] = EIndentNode.NodeEntry;
            }
        }

        // process other lines
        for (int lineIdx = 1; lineIdx < _Lines.Count; ++lineIdx)
        {
            var lineData = _Lines[lineIdx];

            if (lineData.Indent > curIndent)
            {
                // add indent hint
                for (uint i = 0; i <= lineData.Indent; ++i)
                {
                    lineData.IndentNodes![i] = EIndentNode.IndentHint;
                }

                // setup new indent entry node
                for (uint i = curIndent + 1; i <= lineData.Indent; ++i)
                {
                    lastEnterLine[(int)i] = (uint)lineIdx;
                    lineData.IndentNodes![i] = EIndentNode.NodeEntry;
                }

                // update cur indent
                curIndent = lineData.Indent;
            }
            else if (lineData.Indent < curIndent)
            {
                // take back overflow indent hint
                for (uint indentIdx = lineData.Indent + 1; indentIdx <= curIndent; ++indentIdx)
                {
                    uint lastEntryLineIdx = lastEnterLine[(int)indentIdx];

                    // setup last entry line to exit node
                    _Lines[(int)lastEntryLineIdx].IndentNodes![indentIdx] = EIndentNode.NodeExit;

                    // take back indent hint
                    for (uint i = lastEntryLineIdx + 1; i < lineIdx; ++i)
                    {
                        _Lines[(int)i].IndentNodes![indentIdx] = EIndentNode.Empty;
                    }
                }

                // update cur indent
                curIndent = lineData.Indent;

                // add indent hint
                for (uint i = 0; i <= lineData.Indent; ++i)
                {
                    lineData.IndentNodes![(int)i] = i == curIndent ? EIndentNode.NodeEntry : EIndentNode.IndentHint;
                }
            }
            else
            {
                // just add indent hint
                for (uint i = 0; i <= curIndent; ++i)
                {
                    lineData.IndentNodes![i] = i == curIndent ? EIndentNode.NodeEntry : EIndentNode.IndentHint;
                }
            }

            // update enter line
            lastEnterLine[(int)lineData.Indent] = (uint)lineIdx;
        }

        // process end of node
        for (uint indentIdx = 0; indentIdx <= curIndent; ++indentIdx)
        {
            uint lastEntryLineIdx = lastEnterLine[(int)indentIdx];

            // setup last entry line to exit node
            _Lines[(int)lastEntryLineIdx].IndentNodes![indentIdx] = EIndentNode.NodeExit;

            // take back indent hint
            for (uint i = lastEntryLineIdx + 1; i < _Lines.Count; ++i)
            {
                _Lines[(int)i].IndentNodes![indentIdx] = EIndentNode.Empty;
            }
        }
    }
    #endregion

    private static void _DefaultBuildLine(LineData lineData, StringBuilder builder)
    {
        // append nodes
        foreach (var indentNode in lineData.IndentNodes!)
        {
            switch (indentNode)
            {
                case EIndentNode.Empty:
                    builder.Append("  ");
                    break;
                case EIndentNode.IndentHint:
                    builder.Append("| ");
                    break;
                case EIndentNode.NodeEntry:
                    builder.Append("|-");
                    break;
                case EIndentNode.NodeExit:
                    builder.Append("`-");
                    break;
            }
        }

        // append content
        builder.Append(lineData.Content);
    }
}
