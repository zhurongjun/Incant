using System.Globalization;
using System.Text;

namespace Incant.Base.Log;

internal static class LogLevelText
{
    internal static string Format(LogLevel level)
    {
        return level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Info => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Fatal => "FTL",
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, "The log level cannot be written."),
        };
    }

    internal static bool TryParse(string text, out LogLevel level)
    {
        level = text switch
        {
            "TRC" => LogLevel.Trace,
            "DBG" => LogLevel.Debug,
            "INF" => LogLevel.Info,
            "WRN" => LogLevel.Warning,
            "ERR" => LogLevel.Error,
            "FTL" => LogLevel.Fatal,
            _ => LogLevel.None,
        };
        return level != LogLevel.None;
    }
}

internal static class LogFileEscaping
{
    internal static string EscapeContentLine(ReadOnlySpan<char> value)
    {
        var builder = new StringBuilder(value.Length);
        AppendEscaped(builder, value);
        return builder.ToString();
    }

    internal static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        AppendEscaped(builder, value.AsSpan());
        builder.Append('"');
        return builder.ToString();
    }

    internal static string UnescapeContentLine(string value, int lineNumber)
    {
        var builder = new StringBuilder(value.Length);
        for (int index = 0; index < value.Length; ++index)
        {
            char character = value[index];
            if (character == '\\')
            {
                builder.Append(ReadEscape(value, ref index, lineNumber));
            }
            else if (char.IsControl(character) || char.IsSurrogate(character))
            {
                throw Error(lineNumber, "A content line contains an unescaped control or surrogate character.");
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    internal static string ReadQuoted(string text, ref int index, int lineNumber)
    {
        if (index >= text.Length || text[index] != '"')
        {
            throw Error(lineNumber, "A quoted string was expected.");
        }

        ++index;
        var builder = new StringBuilder();
        while (index < text.Length)
        {
            char character = text[index];
            if (character == '"')
            {
                ++index;
                return builder.ToString();
            }

            if (character == '\\')
            {
                builder.Append(ReadEscape(text, ref index, lineNumber));
                ++index;
                continue;
            }

            if (char.IsControl(character) || char.IsSurrogate(character))
            {
                throw Error(lineNumber, "A quoted string contains an unescaped control or surrogate character.");
            }

            builder.Append(character);
            ++index;
        }

        throw Error(lineNumber, "A quoted string is not closed.");
    }

    internal static FormatException Error(int lineNumber, string message)
    {
        return new FormatException($"Line {lineNumber}: {message}");
    }

    private static void AppendEscaped(StringBuilder builder, ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            switch (character)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\0':
                    builder.Append("\\0");
                    break;
                default:
                    if (char.IsControl(character) || char.IsSurrogate(character))
                    {
                        builder.Append("\\u");
                        builder.Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }
    }

    private static char ReadEscape(string text, ref int slashIndex, int lineNumber)
    {
        int escapeIndex = slashIndex + 1;
        if (escapeIndex >= text.Length)
        {
            throw Error(lineNumber, "A backslash escape is incomplete.");
        }

        char escape = text[escapeIndex];
        slashIndex = escapeIndex;
        switch (escape)
        {
            case '\\':
                return '\\';
            case '"':
                return '"';
            case 'n':
                return '\n';
            case 'r':
                return '\r';
            case 't':
                return '\t';
            case '0':
                return '\0';
            case 'u':
                if (escapeIndex + 4 >= text.Length)
                {
                    throw Error(lineNumber, "A Unicode escape must contain four hexadecimal digits.");
                }

                ReadOnlySpan<char> digits = text.AsSpan(escapeIndex + 1, 4);
                if (!ushort.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort code))
                {
                    throw Error(lineNumber, "A Unicode escape contains an invalid hexadecimal digit.");
                }

                slashIndex = escapeIndex + 4;
                return (char)code;
            default:
                throw Error(lineNumber, $"The escape sequence '\\{escape}' is invalid.");
        }
    }
}

internal static class LogFileValueCodec
{
    internal static string Format(LogValue value)
    {
        var builder = new StringBuilder();
        Append(builder, value);
        return builder.ToString();
    }

    internal static LogValue Parse(string text, int lineNumber)
    {
        var parser = new ValueParser(text, lineNumber);
        LogValue value = parser.ParseValue();
        parser.RequireEnd();
        return value;
    }

    private static void Append(StringBuilder builder, LogValue value)
    {
        switch (value.Kind)
        {
            case LogValueKind.Null:
                builder.Append("null");
                break;
            case LogValueKind.Boolean:
                builder.Append((bool)value.Value! ? "true" : "false");
                break;
            case LogValueKind.SignedInteger:
                builder.Append(
                    Convert.ToInt64(value.Value, CultureInfo.InvariantCulture)
                        .ToString(CultureInfo.InvariantCulture));
                break;
            case LogValueKind.UnsignedInteger:
                builder.Append(
                    Convert.ToUInt64(value.Value, CultureInfo.InvariantCulture)
                        .ToString(CultureInfo.InvariantCulture));
                builder.Append('u');
                break;
            case LogValueKind.FloatingPoint:
                builder.Append(Convert.ToDouble(value.Value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
                builder.Append('d');
                break;
            case LogValueKind.Decimal:
                builder.Append(((decimal)value.Value!).ToString("G29", CultureInfo.InvariantCulture));
                builder.Append('m');
                break;
            case LogValueKind.String:
            case LogValueKind.CaptureError:
                builder.Append(LogFileEscaping.Quote(value.Value?.ToString() ?? string.Empty));
                break;
            case LogValueKind.Guid:
                AppendFunction(builder, "guid", ((Guid)value.Value!).ToString("D", CultureInfo.InvariantCulture));
                break;
            case LogValueKind.DateTime:
                string dateTime = value.Value switch
                {
                    DateTimeOffset offset => offset.ToString("O", CultureInfo.InvariantCulture),
                    DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
                    _ => throw new InvalidOperationException("A date-time log value has an invalid representation."),
                };
                AppendFunction(builder, "datetime", dateTime);
                break;
            case LogValueKind.Date:
                AppendFunction(builder, "date", ((DateOnly)value.Value!).ToString("O", CultureInfo.InvariantCulture));
                break;
            case LogValueKind.Time:
                AppendFunction(builder, "time", ((TimeOnly)value.Value!).ToString("O", CultureInfo.InvariantCulture));
                break;
            case LogValueKind.Duration:
                AppendFunction(builder, "duration", ((TimeSpan)value.Value!).ToString("c", CultureInfo.InvariantCulture));
                break;
            case LogValueKind.Uri:
                AppendFunction(builder, "uri", value.Value?.ToString() ?? string.Empty);
                break;
            case LogValueKind.Enum:
                builder.Append("enum(");
                builder.Append(LogFileEscaping.Quote(value.TypeName ?? string.Empty));
                builder.Append(", ");
                builder.Append(LogFileEscaping.Quote(value.Value?.ToString() ?? string.Empty));
                builder.Append(')');
                break;
            case LogValueKind.Sequence:
                builder.Append('[');
                IReadOnlyList<LogValue> sequence = (IReadOnlyList<LogValue>)value.Value!;
                for (int index = 0; index < sequence.Count; ++index)
                {
                    if (index != 0)
                    {
                        builder.Append(", ");
                    }

                    Append(builder, sequence[index]);
                }

                builder.Append(']');
                break;
            case LogValueKind.Structure:
                builder.Append('{');
                IReadOnlyList<LogStructureProperty> properties =
                    (IReadOnlyList<LogStructureProperty>)value.Value!;
                for (int index = 0; index < properties.Count; ++index)
                {
                    if (index != 0)
                    {
                        builder.Append(", ");
                    }

                    LogStructureProperty property = properties[index];
                    builder.Append(LogFileEscaping.Quote(property.Name));
                    builder.Append('=');
                    Append(builder, property.Value);
                }

                builder.Append('}');
                break;
            default:
                throw new InvalidOperationException($"Log value kind '{value.Kind}' cannot be written.");
        }
    }

    private static void AppendFunction(StringBuilder builder, string name, string value)
    {
        builder.Append(name);
        builder.Append('(');
        builder.Append(LogFileEscaping.Quote(value));
        builder.Append(')');
    }

    private ref struct ValueParser
    {
        private readonly int _lineNumber;
        private readonly string _text;
        private int _index;

        internal ValueParser(string text, int lineNumber)
        {
            _text = text;
            _lineNumber = lineNumber;
        }

        internal LogValue ParseValue()
        {
            if (_index >= _text.Length)
            {
                throw Error("A property value is missing.");
            }

            return _text[_index] switch
            {
                '"' => new LogValue(LogValueKind.String, ParseQuoted()),
                '[' => ParseSequence(),
                '{' => ParseStructure(),
                _ => ParseTokenOrFunction(),
            };
        }

        internal readonly void RequireEnd()
        {
            if (_index != _text.Length)
            {
                throw Error("Unexpected characters follow a property value.");
            }
        }

        private LogValue ParseTokenOrFunction()
        {
            if (TryConsume("null"))
            {
                return new LogValue(LogValueKind.Null, null);
            }

            if (TryConsume("true"))
            {
                return new LogValue(LogValueKind.Boolean, true);
            }

            if (TryConsume("false"))
            {
                return new LogValue(LogValueKind.Boolean, false);
            }

            if (TryConsume("guid("))
            {
                string text = ParseQuoted();
                Require(')');
                if (!Guid.TryParseExact(text, "D", out Guid value))
                {
                    throw Error("A guid value is invalid.");
                }

                return new LogValue(LogValueKind.Guid, value);
            }

            if (TryConsume("datetime("))
            {
                string text = ParseQuoted();
                Require(')');
                if (!DateTimeOffset.TryParseExact(
                        text,
                        "O",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out DateTimeOffset value))
                {
                    throw Error("A date-time value is invalid.");
                }

                return new LogValue(LogValueKind.DateTime, value);
            }

            if (TryConsume("date("))
            {
                string text = ParseQuoted();
                Require(')');
                if (!DateOnly.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly value))
                {
                    throw Error("A date value is invalid.");
                }

                return new LogValue(LogValueKind.Date, value);
            }

            if (TryConsume("time("))
            {
                string text = ParseQuoted();
                Require(')');
                if (!TimeOnly.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly value))
                {
                    throw Error("A time value is invalid.");
                }

                return new LogValue(LogValueKind.Time, value);
            }

            if (TryConsume("duration("))
            {
                string text = ParseQuoted();
                Require(')');
                if (!TimeSpan.TryParseExact(text, "c", CultureInfo.InvariantCulture, out TimeSpan value))
                {
                    throw Error("A duration value is invalid.");
                }

                return new LogValue(LogValueKind.Duration, value);
            }

            if (TryConsume("uri("))
            {
                string text = ParseQuoted();
                Require(')');
                return new LogValue(LogValueKind.Uri, text);
            }

            if (TryConsume("enum("))
            {
                string typeName = ParseQuoted();
                Require(", ");
                string text = ParseQuoted();
                Require(')');
                return new LogValue(LogValueKind.Enum, text, typeName);
            }

            int start = _index;
            while (_index < _text.Length && _text[_index] != ',' && _text[_index] != ']' && _text[_index] != '}')
            {
                ++_index;
            }

            string token = _text[start.._index];
            if (token.Length == 0 || token.Any(char.IsWhiteSpace))
            {
                throw Error("A numeric value is invalid.");
            }

            if (token.EndsWith('u'))
            {
                string digits = token[..^1];
                if (!IsUnsignedDigits(digits)
                    || !ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value))
                {
                    throw Error("An unsigned integer value is invalid.");
                }

                return new LogValue(LogValueKind.UnsignedInteger, value);
            }

            if (token.EndsWith('m'))
            {
                string number = token[..^1];
                if (!decimal.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value))
                {
                    throw Error("A decimal value is invalid.");
                }

                return new LogValue(LogValueKind.Decimal, value);
            }

            if (token.EndsWith('d'))
            {
                string number = token[..^1];
                if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                {
                    throw Error("A floating-point value is invalid.");
                }

                return new LogValue(LogValueKind.FloatingPoint, value);
            }

            if (!IsSignedDigits(token)
                || !long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long signed))
            {
                throw Error("A signed integer value is invalid.");
            }

            return new LogValue(LogValueKind.SignedInteger, signed);
        }

        private LogValue ParseSequence()
        {
            Require('[');
            var values = new List<LogValue>();
            if (TryConsume("]"))
            {
                return new LogValue(LogValueKind.Sequence, Array.AsReadOnly(values.ToArray()));
            }

            while (true)
            {
                values.Add(ParseValue());
                if (TryConsume("]"))
                {
                    return new LogValue(LogValueKind.Sequence, Array.AsReadOnly(values.ToArray()));
                }

                Require(", ");
            }
        }

        private LogValue ParseStructure()
        {
            Require('{');
            var properties = new List<LogStructureProperty>();
            if (TryConsume("}"))
            {
                return new LogValue(LogValueKind.Structure, Array.AsReadOnly(properties.ToArray()));
            }

            while (true)
            {
                string name = ParseQuoted();
                Require('=');
                properties.Add(new LogStructureProperty(name, ParseValue()));
                if (TryConsume("}"))
                {
                    return new LogValue(LogValueKind.Structure, Array.AsReadOnly(properties.ToArray()));
                }

                Require(", ");
            }
        }

        private string ParseQuoted()
        {
            return LogFileEscaping.ReadQuoted(_text, ref _index, _lineNumber);
        }

        private bool TryConsume(string expected)
        {
            if (!_text.AsSpan(_index).StartsWith(expected, StringComparison.Ordinal))
            {
                return false;
            }

            _index += expected.Length;
            return true;
        }

        private void Require(char expected)
        {
            if (_index >= _text.Length || _text[_index] != expected)
            {
                throw Error($"Expected '{expected}'.");
            }

            ++_index;
        }

        private void Require(string expected)
        {
            if (!TryConsume(expected))
            {
                throw Error($"Expected '{expected}'.");
            }
        }

        private readonly FormatException Error(string message)
        {
            return LogFileEscaping.Error(_lineNumber, message);
        }

        private static bool IsUnsignedDigits(string value)
        {
            return value.Length > 0 && value.All(character => character is >= '0' and <= '9');
        }

        private static bool IsSignedDigits(string value)
        {
            int start = value.Length > 0 && value[0] == '-' ? 1 : 0;
            return start < value.Length && IsUnsignedDigits(value[start..]);
        }
    }
}
