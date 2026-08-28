using System.Text;

namespace Incant.Base.Cli;

/// <summary>Builds a text tree from lines written at nested indentation levels.</summary>
public class TreeWriter
{
    /// <summary>Identifies the tree marker rendered at an indentation level.</summary>
    public enum IndentNodeKind : byte
    {
        /// <summary>No branch continues through this indentation level.</summary>
        Empty,

        /// <summary>A sibling branch continues through this indentation level.</summary>
        IndentHint,

        /// <summary>A non-final node starts at this indentation level.</summary>
        NodeEntry,

        /// <summary>The final node starts at this indentation level.</summary>
        NodeExit,
    }

    /// <summary>Contains the content and computed indentation markers for one output line.</summary>
    public class LineData
    {
        /// <summary>Gets or sets the line content.</summary>
        public StringBuilder Content { get; set; } = new();

        /// <summary>Gets or sets the zero-based indentation level.</summary>
        public required uint Indent { get; set; }

        /// <summary>Gets or sets the computed markers for each indentation level.</summary>
        public IndentNodeKind[]? IndentNodes { get; set; }
    }

    /// <summary>Restores the previous indentation level when disposed.</summary>
    public ref struct IndentScopeHolder : IDisposable
    {
        private readonly TreeWriter _writer;

        /// <summary>Enters one indentation level for the supplied writer.</summary>
        /// <param name="writer">The writer whose indentation is increased.</param>
        public IndentScopeHolder(TreeWriter writer)
        {
            _writer = writer;
            ++_writer._currentIndent;
        }

        /// <summary>Leaves the indentation level entered by this scope.</summary>
        public readonly void Dispose()
        {
            --_writer._currentIndent;
        }
    }

    private readonly List<LineData> _lines = [];
    private uint _currentIndent;
    private bool _isLineStart = true;

    #region Build Content
    /// <summary>Appends text to the current tree line.</summary>
    /// <param name="text">The text to append.</param>
    /// <returns>This writer for fluent calls.</returns>
    public TreeWriter Write(string text)
    {
        if (_isLineStart)
        {
            _lines.Add(new LineData
            {
                Indent = _currentIndent,
            });
            _isLineStart = false;
        }

        _lines[^1].Content.Append(text);
        return this;
    }

    /// <summary>Appends text and ends the current tree line.</summary>
    /// <param name="text">The text to append.</param>
    /// <returns>This writer for fluent calls.</returns>
    public TreeWriter WriteLine(string text)
    {
        Write(text);
        EndLine();
        return this;
    }

    /// <summary>Writes an empty tree line.</summary>
    /// <returns>This writer for fluent calls.</returns>
    public TreeWriter WriteLine()
    {
        WriteLine(string.Empty);
        return this;
    }

    /// <summary>Ends the current tree line.</summary>
    /// <returns>This writer for fluent calls.</returns>
    public TreeWriter EndLine()
    {
        _isLineStart = true;
        return this;
    }

    /// <summary>Enters a nested indentation level until the returned scope is disposed.</summary>
    /// <returns>A scope that restores the previous indentation level.</returns>
    public IndentScopeHolder IndentScope()
    {
        return new IndentScopeHolder(this);
    }
    #endregion

    #region Style
    /// <inheritdoc cref="Writer.StyleClear"/>
    public TreeWriter StyleClear()
    {
        Write(StyleStrings.Clear);
        return this;
    }

    /// <inheritdoc cref="Writer.StyleBold"/>
    public TreeWriter StyleBold()
    {
        Write(StyleStrings.Bold);
        return this;
    }

    /// <inheritdoc cref="Writer.StyleNoBold"/>
    public TreeWriter StyleNoBold()
    {
        Write(StyleStrings.NoBold);
        return this;
    }

    /// <inheritdoc cref="Writer.StyleUnderline"/>
    public TreeWriter StyleUnderline()
    {
        Write(StyleStrings.Underline);
        return this;
    }

    /// <inheritdoc cref="Writer.StyleNoUnderline"/>
    public TreeWriter StyleNoUnderline()
    {
        Write(StyleStrings.NoUnderline);
        return this;
    }

    /// <inheritdoc cref="Writer.StyleReverse"/>
    public TreeWriter StyleReverse()
    {
        Write(StyleStrings.Reverse);
        return this;
    }

    /// <inheritdoc cref="Writer.StyleNoReverse"/>
    public TreeWriter StyleNoReverse()
    {
        Write(StyleStrings.NoReverse);
        return this;
    }

    /// <inheritdoc cref="Writer.StyleFrontGray"/>
    public TreeWriter StyleFrontGray()
    {
        Write(StyleStrings.FrontGray);
        return this;
    }

    /// <inheritdoc cref="Writer.StyleFrontRed"/>
    public TreeWriter StyleFrontRed()
    {
        Write(StyleStrings.FrontRed);
        return this;
    }

    /// <inheritdoc cref="Writer.StyleFrontGreen"/>
    public TreeWriter StyleFrontGreen()
    {
        Write(StyleStrings.FrontGreen);
        return this;
    }

    /// <inheritdoc cref="Writer.StyleFrontYellow"/>
    public TreeWriter StyleFrontYellow()
    {
        Write(StyleStrings.FrontYellow);
        return this;
    }

    /// <inheritdoc cref="Writer.StyleFrontBlue"/>
    public TreeWriter StyleFrontBlue()
    {
        Write(StyleStrings.FrontBlue);
        return this;
    }

    /// <inheritdoc cref="Writer.StyleFrontMagenta"/>
    public TreeWriter StyleFrontMagenta()
    {
        Write(StyleStrings.FrontMagenta);
        return this;
    }

    /// <inheritdoc cref="Writer.StyleFrontCyan"/>
    public TreeWriter StyleFrontCyan()
    {
        Write(StyleStrings.FrontCyan);
        return this;
    }

    /// <inheritdoc cref="Writer.StyleFrontWhite"/>
    public TreeWriter StyleFrontWhite()
    {
        Write(StyleStrings.FrontWhite);
        return this;
    }

    /// <inheritdoc cref="Writer.StyleBackGray"/>
    public TreeWriter StyleBackGray()
    {
        Write(StyleStrings.BackGray);
        return this;
    }

    /// <inheritdoc cref="Writer.StyleBackRed"/>
    public TreeWriter StyleBackRed()
    {
        Write(StyleStrings.BackRed);
        return this;
    }

    /// <inheritdoc cref="Writer.StyleBackGreen"/>
    public TreeWriter StyleBackGreen()
    {
        Write(StyleStrings.BackGreen);
        return this;
    }

    /// <inheritdoc cref="Writer.StyleBackYellow"/>
    public TreeWriter StyleBackYellow()
    {
        Write(StyleStrings.BackYellow);
        return this;
    }

    /// <inheritdoc cref="Writer.StyleBackBlue"/>
    public TreeWriter StyleBackBlue()
    {
        Write(StyleStrings.BackBlue);
        return this;
    }

    /// <inheritdoc cref="Writer.StyleBackMagenta"/>
    public TreeWriter StyleBackMagenta()
    {
        Write(StyleStrings.BackMagenta);
        return this;
    }

    /// <inheritdoc cref="Writer.StyleBackCyan"/>
    public TreeWriter StyleBackCyan()
    {
        Write(StyleStrings.BackCyan);
        return this;
    }

    /// <inheritdoc cref="Writer.StyleBackWhite"/>
    public TreeWriter StyleBackWhite()
    {
        Write(StyleStrings.BackWhite);
        return this;
    }
    #endregion

    #region Build Output
    /// <summary>Builds the tree and writes it to standard output.</summary>
    /// <param name="buildLine">An optional callback that renders each solved line.</param>
    public void Dump(Action<LineData, StringBuilder>? buildLine = null)
    {
        Console.Write(Build(buildLine));
    }

    /// <summary>Solves indentation markers and builds the complete tree text.</summary>
    /// <param name="buildLine">An optional callback that renders each solved line.</param>
    /// <returns>The rendered tree text.</returns>
    public string Build(Action<LineData, StringBuilder>? buildLine = null)
    {
        if (_lines.Count == 0)
        {
            return string.Empty;
        }

        SolveIndent();
        return BuildWithoutSolvingIndent(buildLine);
    }

    /// <summary>Builds tree text from previously solved indentation markers.</summary>
    /// <param name="buildLine">An optional callback that renders each solved line.</param>
    /// <returns>The rendered tree text.</returns>
    /// <exception cref="InvalidOperationException">Indentation markers have not been solved.</exception>
    public string BuildWithoutSolvingIndent(Action<LineData, StringBuilder>? buildLine = null)
    {
        if (_lines.Count == 0)
        {
            return string.Empty;
        }

        buildLine ??= DefaultBuildLine;
        StringBuilder builder = new();
        for (int lineIndex = 0; lineIndex < _lines.Count; ++lineIndex)
        {
            if (_lines[lineIndex].IndentNodes == null)
            {
                throw new InvalidOperationException("Indent nodes is null, please call SolveIndent first or use Build method.");
            }

            buildLine(_lines[lineIndex], builder);
            builder.AppendLine();
        }
        return builder.ToString();
    }

    /// <summary>Computes the tree marker for every indentation level of every line.</summary>
    public void SolveIndent()
    {
        uint maxIndent = 0;
        foreach (LineData lineData in _lines)
        {
            maxIndent = Math.Max(maxIndent, lineData.Indent);
            lineData.IndentNodes = new IndentNodeKind[lineData.Indent + 1];
            for (uint i = 0; i <= lineData.Indent; ++i)
            {
                lineData.IndentNodes[i] = IndentNodeKind.Empty;
            }
        }

        List<uint> lastEnterLine = new();
        for (uint i = 0; i <= maxIndent; ++i)
        {
            lastEnterLine.Add(uint.MaxValue);
        }

        uint currentIndent;
        {
            LineData lineData = _lines[0];
            currentIndent = lineData.Indent;

            for (uint i = 0; i <= currentIndent; ++i)
            {
                lastEnterLine[(int)i] = 0;
                lineData.IndentNodes![i] = IndentNodeKind.NodeEntry;
            }
        }

        for (int lineIndex = 1; lineIndex < _lines.Count; ++lineIndex)
        {
            LineData lineData = _lines[lineIndex];

            if (lineData.Indent > currentIndent)
            {
                for (uint i = 0; i <= lineData.Indent; ++i)
                {
                    lineData.IndentNodes![i] = IndentNodeKind.IndentHint;
                }

                for (uint i = currentIndent + 1; i <= lineData.Indent; ++i)
                {
                    lastEnterLine[(int)i] = (uint)lineIndex;
                    lineData.IndentNodes![i] = IndentNodeKind.NodeEntry;
                }

                currentIndent = lineData.Indent;
            }
            else if (lineData.Indent < currentIndent)
            {
                for (uint indentIndex = lineData.Indent + 1; indentIndex <= currentIndent; ++indentIndex)
                {
                    uint lastEntryLineIndex = lastEnterLine[(int)indentIndex];

                    _lines[(int)lastEntryLineIndex].IndentNodes![indentIndex] = IndentNodeKind.NodeExit;

                    for (uint i = lastEntryLineIndex + 1; i < lineIndex; ++i)
                    {
                        _lines[(int)i].IndentNodes![indentIndex] = IndentNodeKind.Empty;
                    }
                }

                currentIndent = lineData.Indent;

                for (uint i = 0; i <= lineData.Indent; ++i)
                {
                    lineData.IndentNodes![(int)i] = i == currentIndent ? IndentNodeKind.NodeEntry : IndentNodeKind.IndentHint;
                }
            }
            else
            {
                for (uint i = 0; i <= currentIndent; ++i)
                {
                    lineData.IndentNodes![i] = i == currentIndent ? IndentNodeKind.NodeEntry : IndentNodeKind.IndentHint;
                }
            }

            lastEnterLine[(int)lineData.Indent] = (uint)lineIndex;
        }

        for (uint indentIndex = 0; indentIndex <= currentIndent; ++indentIndex)
        {
            uint lastEntryLineIndex = lastEnterLine[(int)indentIndex];

            _lines[(int)lastEntryLineIndex].IndentNodes![indentIndex] = IndentNodeKind.NodeExit;

            for (uint i = lastEntryLineIndex + 1; i < _lines.Count; ++i)
            {
                _lines[(int)i].IndentNodes![indentIndex] = IndentNodeKind.Empty;
            }
        }
    }
    #endregion

    private static void DefaultBuildLine(LineData lineData, StringBuilder builder)
    {
        foreach (IndentNodeKind indentNode in lineData.IndentNodes!)
        {
            switch (indentNode)
            {
                case IndentNodeKind.Empty:
                    builder.Append("  ");
                    break;
                case IndentNodeKind.IndentHint:
                    builder.Append("| ");
                    break;
                case IndentNodeKind.NodeEntry:
                    builder.Append("|-");
                    break;
                case IndentNodeKind.NodeExit:
                    builder.Append("`-");
                    break;
            }
        }
        builder.Append(lineData.Content);
    }
}
