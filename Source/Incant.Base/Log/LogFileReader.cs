using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Incant.Base.Log;

/// <summary>Represents one ordered property read from an Incant log file.</summary>
public sealed class LogFileProperty
{
    internal LogFileProperty(string name, LogValue value)
    {
        Name = name;
        Value = value;
    }

    /// <summary>Gets the property name.</summary>
    public string Name { get; }

    /// <summary>Gets the decoded immutable value.</summary>
    public LogValue Value { get; }
}

/// <summary>Represents one complete record read from the Incant v1 log protocol.</summary>
public sealed class LogFileRecord
{
    private readonly ReadOnlyCollection<LogFileProperty> _properties;

    internal LogFileRecord(
        DateTimeOffset timestamp,
        int processId,
        int threadId,
        LogLevel level,
        LogCategory category,
        string message,
        IReadOnlyList<LogFileProperty> properties,
        string? exceptionText,
        string? templateError)
    {
        Timestamp = timestamp;
        ProcessId = processId;
        ThreadId = threadId;
        Level = level;
        Category = category;
        Message = message;
        _properties = Array.AsReadOnly(properties.ToArray());
        ExceptionText = exceptionText;
        TemplateError = templateError;
    }

    /// <summary>Gets the stable UTC timestamp.</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>Gets the operating-system process identifier.</summary>
    public int ProcessId { get; }

    /// <summary>Gets the managed thread identifier.</summary>
    public int ThreadId { get; }

    /// <summary>Gets the event level.</summary>
    public LogLevel Level { get; }

    /// <summary>Gets the event category.</summary>
    public LogCategory Category { get; }

    /// <summary>Gets the formatted message body with logical line separators normalized to <c>\n</c>.</summary>
    public string Message { get; }

    /// <summary>Gets properties in their original occurrence order.</summary>
    public IReadOnlyList<LogFileProperty> Properties => _properties;

    /// <summary>Gets the captured exception text, when present.</summary>
    public string? ExceptionText { get; }

    /// <summary>Gets the template fallback diagnostic, when present.</summary>
    public string? TemplateError { get; }
}

/// <summary>Incrementally reads the Incant v1 streaming text protocol.</summary>
public static class LogFileReader
{
    /// <summary>Reads complete records from a caller-owned text reader.</summary>
    /// <param name="reader">The source reader. This method does not dispose it.</param>
    /// <returns>A lazy sequence of complete records.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
    /// <exception cref="FormatException">The input violates the Incant v1 log protocol.</exception>
    public static IEnumerable<LogFileRecord> Read(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var parser = new Parser();
        while (reader.ReadLine() is string line)
        {
            if (parser.ProcessLine(line, out LogFileRecord? record))
            {
                yield return record!;
            }
        }

        if (parser.Complete(out LogFileRecord? finalRecord))
        {
            yield return finalRecord!;
        }
    }

    /// <summary>Asynchronously reads complete records from a caller-owned text reader.</summary>
    /// <param name="reader">The source reader. This method does not dispose it.</param>
    /// <param name="cancellationToken">A token that cancels pending line reads.</param>
    /// <returns>An asynchronous sequence of complete records.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
    /// <exception cref="FormatException">The input violates the Incant v1 log protocol.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public static async IAsyncEnumerable<LogFileRecord> ReadAsync(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var parser = new Parser();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is string line)
        {
            if (parser.ProcessLine(line, out LogFileRecord? record))
            {
                yield return record!;
            }
        }

        if (parser.Complete(out LogFileRecord? finalRecord))
        {
            yield return finalRecord!;
        }
    }

    private sealed class Parser
    {
        private RecordBuilder? _current;
        private int _lineNumber;

        internal bool ProcessLine(string line, out LogFileRecord? completed)
        {
            ++_lineNumber;
            completed = null;
            if (line.StartsWith('@'))
            {
                if (_current is not null)
                {
                    completed = _current.Build(_lineNumber - 1);
                }

                _current = ParseHeader(line, _lineNumber);
                return completed is not null;
            }

            if (line.Length == 0)
            {
                if (_current is null)
                {
                    return false;
                }

                completed = _current.Build(_lineNumber);
                _current = null;
                return true;
            }

            if (_current is null)
            {
                throw LogFileEscaping.Error(_lineNumber, "A content or detail line appears before a record header.");
            }

            if (line == ">>")
            {
                _current.AddContentLine(string.Empty, _lineNumber);
                return false;
            }

            if (line.StartsWith(">> ", StringComparison.Ordinal))
            {
                if (line.Length == 3)
                {
                    throw LogFileEscaping.Error(_lineNumber, "An empty content line must contain only '>>'.");
                }

                _current.AddContentLine(
                    LogFileEscaping.UnescapeContentLine(line[3..], _lineNumber),
                    _lineNumber);
                return false;
            }

            if (line.StartsWith(">>", StringComparison.Ordinal))
            {
                throw LogFileEscaping.Error(_lineNumber, "The content marker '>>' must be followed by one space.");
            }

            if (line.StartsWith(":: ", StringComparison.Ordinal))
            {
                _current.ParseDetail(line[3..], _lineNumber);
                return false;
            }

            if (line.StartsWith("::", StringComparison.Ordinal))
            {
                throw LogFileEscaping.Error(_lineNumber, "The detail marker '::' must be followed by one space.");
            }

            throw LogFileEscaping.Error(_lineNumber, "A record line must begin with '>>' or '::'.");
        }

        internal bool Complete(out LogFileRecord? completed)
        {
            if (_current is null)
            {
                completed = null;
                return false;
            }

            completed = _current.Build(_lineNumber);
            _current = null;
            return true;
        }

        private static RecordBuilder ParseHeader(string line, int lineNumber)
        {
            int index = 1;
            string version = ReadUntil(line, ref index, ' ', lineNumber, "version");
            if (version != "1")
            {
                throw LogFileEscaping.Error(lineNumber, $"Log file version '{version}' is not supported.");
            }

            string timestampText = ReadUntil(line, ref index, ' ', lineNumber, "timestamp");
            if (!DateTimeOffset.TryParseExact(
                    timestampText,
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset timestamp))
            {
                throw LogFileEscaping.Error(lineNumber, "The record timestamp is invalid.");
            }

            timestamp = timestamp.ToUniversalTime();

            Require(line, ref index, "[P", lineNumber);
            string processText = ReadUntil(line, ref index, ':', lineNumber, "process identifier");
            Require(line, ref index, "T", lineNumber);
            string threadText = ReadUntil(line, ref index, ']', lineNumber, "thread identifier");
            if (!TryParseNonNegativeInt32(processText, out int processId)
                || !TryParseNonNegativeInt32(threadText, out int threadId))
            {
                throw LogFileEscaping.Error(lineNumber, "The process or thread identifier is invalid.");
            }

            Require(line, ref index, " [", lineNumber);
            string levelText = ReadUntil(line, ref index, ']', lineNumber, "level");
            if (!LogLevelText.TryParse(levelText, out LogLevel level))
            {
                throw LogFileEscaping.Error(lineNumber, $"Log level '{levelText}' is invalid.");
            }

            Require(line, ref index, " [", lineNumber);
            string categoryText = ReadUntil(line, ref index, ']', lineNumber, "category");
            LogCategory category;
            try
            {
                category = new LogCategory(categoryText);
            }
            catch (ArgumentException exception)
            {
                throw LogFileEscaping.Error(lineNumber, $"The category is invalid: {exception.Message}");
            }

            if (index != line.Length)
            {
                throw LogFileEscaping.Error(lineNumber, "Unexpected characters follow the record header.");
            }

            return new RecordBuilder(
                timestamp,
                processId,
                threadId,
                level,
                category,
                lineNumber);
        }

        private static string ReadUntil(
            string text,
            ref int index,
            char terminator,
            int lineNumber,
            string role)
        {
            int end = text.IndexOf(terminator, index);
            if (end < 0 || end == index)
            {
                throw LogFileEscaping.Error(lineNumber, $"The record {role} is missing or malformed.");
            }

            string result = text[index..end];
            index = end + 1;
            return result;
        }

        private static void Require(string text, ref int index, string expected, int lineNumber)
        {
            if (!text.AsSpan(index).StartsWith(expected, StringComparison.Ordinal))
            {
                throw LogFileEscaping.Error(lineNumber, $"Expected '{expected}' in the record header.");
            }

            index += expected.Length;
        }

        private static bool TryParseNonNegativeInt32(string value, out int result)
        {
            result = 0;
            return value.Length > 0
                && value.All(character => character is >= '0' and <= '9')
                && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);
        }
    }

    private sealed class RecordBuilder
    {
        private readonly LogCategory _category;
        private readonly List<string> _contentLines = [];
        private readonly List<LogFileProperty> _properties = [];
        private readonly int _processId;
        private readonly int _startLine;
        private readonly int _threadId;
        private readonly DateTimeOffset _timestamp;
        private readonly LogLevel _level;
        private string? _exceptionText;
        private bool _hasDetails;
        private bool _hasTemplateError;
        private string? _templateError;
        private bool _hasException;

        internal RecordBuilder(
            DateTimeOffset timestamp,
            int processId,
            int threadId,
            LogLevel level,
            LogCategory category,
            int startLine)
        {
            _timestamp = timestamp;
            _processId = processId;
            _threadId = threadId;
            _level = level;
            _category = category;
            _startLine = startLine;
        }

        internal void AddContentLine(string content, int lineNumber)
        {
            if (_hasDetails)
            {
                throw LogFileEscaping.Error(lineNumber, "A content line cannot appear after a detail line.");
            }

            _contentLines.Add(content);
        }

        internal void ParseDetail(string detail, int lineNumber)
        {
            if (_contentLines.Count == 0)
            {
                throw LogFileEscaping.Error(lineNumber, "A detail line cannot appear before the record content.");
            }

            _hasDetails = true;
            if (detail.StartsWith(' '))
            {
                throw LogFileEscaping.Error(lineNumber, "A detail field cannot contain extra leading whitespace.");
            }

            if (detail.StartsWith("property ", StringComparison.Ordinal))
            {
                int separator = detail.IndexOf(": ", "property ".Length, StringComparison.Ordinal);
                if (separator < 0)
                {
                    throw LogFileEscaping.Error(lineNumber, "A property detail is missing its ': ' separator.");
                }

                string name = detail["property ".Length..separator];
                if (!IsPropertyName(name))
                {
                    throw LogFileEscaping.Error(lineNumber, "A property name is invalid.");
                }

                string valueText = detail[(separator + 2)..];
                _properties.Add(new LogFileProperty(name, LogFileValueCodec.Parse(valueText, lineNumber)));
                return;
            }

            if (detail.StartsWith("exception: ", StringComparison.Ordinal))
            {
                EnsureSingle(!_hasException, "exception", lineNumber);
                _exceptionText = ParseWholeQuoted(detail["exception: ".Length..], lineNumber);
                _hasException = true;
                return;
            }

            if (detail.StartsWith("template-error: ", StringComparison.Ordinal))
            {
                EnsureSingle(!_hasTemplateError, "template-error", lineNumber);
                _templateError = ParseWholeQuoted(detail["template-error: ".Length..], lineNumber);
                _hasTemplateError = true;
                return;
            }

            throw LogFileEscaping.Error(lineNumber, "The detail field is unknown or malformed.");
        }

        internal LogFileRecord Build(int endLine)
        {
            if (_contentLines.Count == 0)
            {
                throw LogFileEscaping.Error(
                    endLine,
                    $"The record beginning on line {_startLine} is incomplete; missing content.");
            }

            return new LogFileRecord(
                _timestamp,
                _processId,
                _threadId,
                _level,
                _category,
                string.Join("\n", _contentLines),
                _properties,
                _exceptionText,
                _templateError);
        }

        private static void EnsureSingle(bool isAvailable, string name, int lineNumber)
        {
            if (!isAvailable)
            {
                throw LogFileEscaping.Error(lineNumber, $"The single-value detail '{name}' is duplicated.");
            }
        }

        private static string ParseWholeQuoted(string text, int lineNumber)
        {
            int index = 0;
            string value = LogFileEscaping.ReadQuoted(text, ref index, lineNumber);
            if (index != text.Length)
            {
                throw LogFileEscaping.Error(lineNumber, "Unexpected characters follow a quoted string.");
            }

            return value;
        }

        private static bool IsPropertyName(string name)
        {
            if (name.Length == 0)
            {
                return false;
            }

            foreach (string segment in name.Split('.'))
            {
                if (segment.Length == 0 || (!char.IsLetter(segment[0]) && segment[0] != '_'))
                {
                    return false;
                }

                foreach (char character in segment.AsSpan(1))
                {
                    if (!char.IsLetterOrDigit(character) && character != '_')
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
