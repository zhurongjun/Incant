using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace Incant.Base.Trace;

internal sealed class TracePayload
{
    private TracePayload(JsonElement arguments)
    {
        Arguments = arguments;
    }

    internal JsonElement Arguments { get; }

    internal static TracePayload Create(string messageTemplate, object?[]? propertyValues)
    {
        ArgumentNullException.ThrowIfNull(messageTemplate);

        TemplateProperty[] properties = MessageTemplateParser.Parse(messageTemplate);
        // Keep a covariant array passed as one params argument from being reinterpreted as multiple properties.
        object?[] values = propertyValues switch
        {
            null => [null],
            _ when propertyValues.GetType() != typeof(object[]) => [propertyValues],
            _ => propertyValues,
        };
        ValidatePropertyValueCount(properties, values.Length, nameof(propertyValues));

        var arguments = new Dictionary<string, JsonElement>(properties.Length, StringComparer.Ordinal);
        if (AreAllPropertiesPositional(properties))
        {
            foreach (TemplateProperty property in properties)
            {
                arguments[property.Name] = CaptureValue(values[property.Position!.Value], property.CaptureKind);
            }
        }
        else
        {
            for (int index = 0; index < properties.Length; ++index)
            {
                TemplateProperty property = properties[index];
                arguments[property.Name] = CaptureValue(values[index], property.CaptureKind);
            }
        }

        return new TracePayload(JsonSerializer.SerializeToElement(arguments, JsonSerializerOptions.Default));
    }

    private static void ValidatePropertyValueCount(
        TemplateProperty[] properties,
        int valueCount,
        string parameterName)
    {
        if (!AreAllPropertiesPositional(properties))
        {
            if (properties.Length != valueCount)
            {
                ThrowPropertyValueCountMismatch(properties.Length, valueCount, parameterName);
            }

            return;
        }

        var positions = new HashSet<int>();
        bool hasOutOfRangePosition = false;
        foreach (TemplateProperty property in properties)
        {
            int position = property.Position!.Value;
            if (position >= valueCount)
            {
                hasOutOfRangePosition = true;
            }

            positions.Add(position);
        }

        if (hasOutOfRangePosition)
        {
            throw new ArgumentException(
                "The supplied property values do not cover every positional property in the message template.",
                parameterName);
        }

        if (positions.Count != valueCount)
        {
            ThrowPropertyValueCountMismatch(positions.Count, valueCount, parameterName);
        }
    }

    private static bool AreAllPropertiesPositional(TemplateProperty[] properties)
    {
        if (properties.Length == 0)
        {
            return false;
        }

        foreach (TemplateProperty property in properties)
        {
            if (property.Position is null)
            {
                return false;
            }
        }

        return true;
    }

    private static JsonElement CaptureValue(object? value, PropertyCaptureKind captureKind)
    {
        object? capturedValue = captureKind switch
        {
            PropertyCaptureKind.Destructure => value,
            PropertyCaptureKind.Stringify => value is null ? null : value.ToString() ?? string.Empty,
            _ => CaptureDefaultValue(value),
        };

        return JsonSerializer.SerializeToElement(
            capturedValue,
            capturedValue?.GetType() ?? typeof(object),
            JsonSerializerOptions.Default);
    }

    private static object? CaptureDefaultValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        Type type = value.GetType();
        if (type.IsPrimitive
            || type.IsEnum
            || value is string
            || value is decimal
            || value is DateTime
            || value is DateTimeOffset
            || value is DateOnly
            || value is TimeOnly
            || value is TimeSpan
            || value is Guid
            || value is Uri
            || value is JsonElement
            || value is JsonDocument
            || value is IEnumerable)
        {
            return value;
        }

        return value.ToString() ?? string.Empty;
    }

    private static void ThrowPropertyValueCountMismatch(
        int propertyCount,
        int valueCount,
        string parameterName)
    {
        throw new ArgumentException(
            $"The message template requires {propertyCount} property values, but {valueCount} were supplied.",
            parameterName);
    }

    private enum PropertyCaptureKind
    {
        Default,
        Destructure,
        Stringify,
    }

    private readonly record struct TemplateProperty(
        string Name,
        int? Position,
        PropertyCaptureKind CaptureKind);

    private static class MessageTemplateParser
    {
        internal static TemplateProperty[] Parse(string messageTemplate)
        {
            var properties = new List<TemplateProperty>();
            int index = 0;
            while (index < messageTemplate.Length)
            {
                switch (messageTemplate[index])
                {
                    case '{':
                        if (IsEscapedBrace(messageTemplate, index, '{'))
                        {
                            index += 2;
                            break;
                        }

                        int endIndex = messageTemplate.IndexOf('}', index + 1);
                        if (endIndex < 0)
                        {
                            ThrowInvalidTemplate(messageTemplate);
                        }

                        string token = messageTemplate[(index + 1)..endIndex];
                        properties.Add(ParseProperty(token, messageTemplate));
                        index = endIndex + 1;
                        break;

                    case '}':
                        if (!IsEscapedBrace(messageTemplate, index, '}'))
                        {
                            ThrowInvalidTemplate(messageTemplate);
                        }

                        index += 2;
                        break;

                    default:
                        ++index;
                        break;
                }
            }

            return properties.ToArray();
        }

        private static TemplateProperty ParseProperty(string token, string messageTemplate)
        {
            if (token.Length == 0 || token.Contains('{', StringComparison.Ordinal))
            {
                ThrowInvalidTemplate(messageTemplate);
            }

            int index = 0;
            PropertyCaptureKind captureKind = token[index] switch
            {
                '@' => PropertyCaptureKind.Destructure,
                '$' => PropertyCaptureKind.Stringify,
                _ => PropertyCaptureKind.Default,
            };
            if (captureKind != PropertyCaptureKind.Default)
            {
                ++index;
            }

            int nameStart = index;
            while (index < token.Length && token[index] != ',' && token[index] != ':')
            {
                ++index;
            }

            string name = token[nameStart..index];
            int? position = ParsePropertyName(name, messageTemplate);

            if (index < token.Length && token[index] == ',')
            {
                ++index;
                int alignmentStart = index;
                while (index < token.Length && token[index] != ':')
                {
                    ++index;
                }

                ReadOnlySpan<char> alignment = token.AsSpan(alignmentStart, index - alignmentStart).Trim();
                if (alignment.IsEmpty
                    || alignment[0] == '+'
                    || !int.TryParse(
                        alignment,
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture,
                        out _))
                {
                    ThrowInvalidTemplate(messageTemplate);
                }
            }

            if (index < token.Length)
            {
                if (token[index] != ':')
                {
                    ThrowInvalidTemplate(messageTemplate);
                }

                ++index;
            }

            if (token.AsSpan(index).Contains('{'))
            {
                ThrowInvalidTemplate(messageTemplate);
            }

            return new TemplateProperty(name, position, captureKind);
        }

        private static int? ParsePropertyName(string name, string messageTemplate)
        {
            if (name.Length == 0)
            {
                ThrowInvalidTemplate(messageTemplate);
            }

            bool isPositional = true;
            foreach (char character in name)
            {
                if (!char.IsAsciiDigit(character))
                {
                    isPositional = false;
                    break;
                }
            }

            if (isPositional)
            {
                if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int position))
                {
                    ThrowInvalidTemplate(messageTemplate);
                }

                return position;
            }

            int segmentStart = 0;
            for (int index = 0; index <= name.Length; ++index)
            {
                if (index < name.Length && name[index] != '.')
                {
                    continue;
                }

                if (!IsValidIdentifier(name.AsSpan(segmentStart, index - segmentStart)))
                {
                    ThrowInvalidTemplate(messageTemplate);
                }

                segmentStart = index + 1;
            }

            return null;
        }

        private static bool IsValidIdentifier(ReadOnlySpan<char> identifier)
        {
            if (identifier.IsEmpty || (!char.IsLetter(identifier[0]) && identifier[0] != '_'))
            {
                return false;
            }

            foreach (char character in identifier[1..])
            {
                if (!char.IsLetterOrDigit(character) && character != '_')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsEscapedBrace(string messageTemplate, int index, char brace)
        {
            return index + 1 < messageTemplate.Length && messageTemplate[index + 1] == brace;
        }

        private static void ThrowInvalidTemplate(string messageTemplate)
        {
            throw new FormatException($"The message template '{messageTemplate}' is invalid.");
        }
    }
}
