using System.Globalization;
using System.Text;

namespace Incant.Base.Log;

internal static class LogMessageRenderer
{
    private const int MaximumAlignment = 32_768;

    internal static RenderedLogEvent Render(LogRuntime runtime, LogProducer producer, ref LogRecord record)
    {
        string? template = record.MessageTemplate;
        if (template is null)
        {
            RenderResult fallback = RenderFallback(string.Empty, record.RootDecorator, ref record);
            return CreateEvent(
                runtime,
                producer,
                ref record,
                fallback,
                "A log message template cannot be null.");
        }

        try
        {
            RenderResult result = RenderTemplate(template, record.RootDecorator, ref record);
            return CreateEvent(runtime, producer, ref record, result, null);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            RenderResult fallback = RenderFallback(template, record.RootDecorator, ref record);
            return CreateEvent(runtime, producer, ref record, fallback, exception.Message);
        }
    }

    private static RenderedLogEvent CreateEvent(
        LogRuntime runtime,
        LogProducer producer,
        ref LogRecord record,
        RenderResult result,
        string? templateError)
    {
        return new RenderedLogEvent(
            runtime.GetTimestamp(record.Timestamp),
            runtime.GetElapsedNanoseconds(record.Timestamp),
            runtime.NextOutputSequence(),
            record.Level,
            record.Category,
            runtime.ProcessId,
            producer.ThreadId,
            producer.ThreadName,
            record.MessageTemplate ?? string.Empty,
            result.Message,
            result.Root,
            result.Properties,
            record.ExceptionText,
            templateError);
    }

    private static RenderResult RenderTemplate(
        string template,
        TextDecorator? rootDecorator,
        ref LogRecord record)
    {
        var root = new ScopeBuilder(null, rootDecorator);
        var scopes = new Stack<ScopeBuilder>();
        scopes.Push(root);
        var properties = new List<LogProperty>();
        var literal = new StringBuilder();
        int argumentIndex = 0;
        int index = 0;

        while (index < template.Length)
        {
            char character = template[index];
            if (character == '{')
            {
                if (index + 1 < template.Length && template[index + 1] == '{')
                {
                    literal.Append('{');
                    index += 2;
                    continue;
                }

                FlushLiteral(scopes.Peek(), literal);
                int endIndex = template.IndexOf('}', index + 1);
                if (endIndex < 0)
                {
                    throw new FormatException("The message template contains an unclosed token.");
                }

                ReadOnlySpan<char> token = template.AsSpan(index + 1, endIndex - index - 1);
                if (token.IsEmpty || token.Contains('{'))
                {
                    throw new FormatException("The message template contains an invalid token.");
                }

                if (token[0] == '#')
                {
                    string scopeName = ParseScopeName(token[1..]);
                    if (argumentIndex >= record.ArgumentCount
                        || !record.GetArgument(argumentIndex).TryGetTextDecorator(out TextDecorator decorator))
                    {
                        throw new ArgumentException($"Text scope '{scopeName}' requires a TextDecorator argument.");
                    }

                    ++argumentIndex;
                    scopes.Push(new ScopeBuilder(scopeName, decorator));
                }
                else if (token[0] == '/')
                {
                    string scopeName = ParseScopeName(token[1..]);
                    if (scopes.Count == 1 || !string.Equals(scopes.Peek().Name, scopeName, StringComparison.Ordinal))
                    {
                        throw new FormatException($"Text scope '{scopeName}' is not correctly nested.");
                    }

                    ScopeBuilder completedScope = scopes.Pop();
                    scopes.Peek().Children.Add(completedScope.Build());
                }
                else
                {
                    TemplateProperty propertyTemplate = ParseProperty(token);
                    if (argumentIndex >= record.ArgumentCount)
                    {
                        throw new ArgumentException(
                            $"The message template requires more than {record.ArgumentCount} arguments.");
                    }

                    LogArgument argument = record.GetArgument(argumentIndex++);
                    if (argument.TryGetTextDecorator(out _))
                    {
                        throw new ArgumentException(
                            $"Property '{propertyTemplate.Name}' cannot be bound to a TextDecorator.");
                    }

                    LogValue value = argument.ToLogValue();
                    string formattedText = FormatValue(value, propertyTemplate.Format, propertyTemplate.Alignment);
                    var property = new LogProperty(
                        propertyTemplate.Name,
                        value,
                        formattedText,
                        argument.ParamDecorator);
                    properties.Add(property);
                    scopes.Peek().Children.Add(new ParamText(property));
                }

                index = endIndex + 1;
                continue;
            }

            if (character == '}')
            {
                if (index + 1 < template.Length && template[index + 1] == '}')
                {
                    literal.Append('}');
                    index += 2;
                    continue;
                }

                throw new FormatException("The message template contains an unmatched closing brace.");
            }

            literal.Append(character);
            ++index;
        }

        FlushLiteral(scopes.Peek(), literal);
        if (scopes.Count != 1)
        {
            throw new FormatException($"Text scope '{scopes.Peek().Name}' is not closed.");
        }

        if (argumentIndex != record.ArgumentCount)
        {
            throw new ArgumentException(
                $"The message template consumed {argumentIndex} arguments, but {record.ArgumentCount} were supplied.");
        }

        TextScope rootNode = root.Build();
        return new RenderResult(rootNode, RenderPlainText(rootNode), properties);
    }

    private static RenderResult RenderFallback(
        string template,
        TextDecorator? rootDecorator,
        ref LogRecord record)
    {
        var children = new List<LogTextNode>
        {
            new LiteralText(template),
        };
        var properties = new List<LogProperty>();

        for (int index = 0; index < record.ArgumentCount; ++index)
        {
            LogArgument argument = record.GetArgument(index);
            if (argument.TryGetTextDecorator(out _))
            {
                continue;
            }

            LogValue value = argument.ToLogValue();
            string formattedText = FormatValue(value, null, null);
            var property = new LogProperty(
                $"Argument{index}",
                value,
                formattedText,
                argument.ParamDecorator);
            properties.Add(property);
        }

        TextScope root = rootDecorator is TextDecorator decorator
            ? new DecoratedTextScope(decorator, children)
            : new TextScope(children);
        return new RenderResult(root, template, properties);
    }

    private static TemplateProperty ParseProperty(ReadOnlySpan<char> token)
    {
        int index = 0;
        while (index < token.Length && token[index] != ',' && token[index] != ':')
        {
            ++index;
        }

        string name = token[..index].ToString();
        ValidateIdentifier(name, "property");

        int? alignment = null;
        if (index < token.Length && token[index] == ',')
        {
            ++index;
            int alignmentStart = index;
            while (index < token.Length && token[index] != ':')
            {
                ++index;
            }

            ReadOnlySpan<char> alignmentText = token[alignmentStart..index].Trim();
            if (alignmentText.IsEmpty
                || alignmentText[0] == '+'
                || !int.TryParse(
                    alignmentText,
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out int parsedAlignment)
                || Math.Abs((long)parsedAlignment) > MaximumAlignment)
            {
                throw new FormatException($"Property '{name}' has an invalid alignment.");
            }

            alignment = parsedAlignment;
        }

        string? format = null;
        if (index < token.Length)
        {
            if (token[index] != ':')
            {
                throw new FormatException($"Property '{name}' has an invalid suffix.");
            }

            ++index;
            ReadOnlySpan<char> formatText = token[index..];
            if (formatText.Contains('{') || formatText.Contains('}'))
            {
                throw new FormatException($"Property '{name}' has an invalid format.");
            }

            format = formatText.ToString();
        }

        return new TemplateProperty(name, alignment, format);
    }

    private static string ParseScopeName(ReadOnlySpan<char> name)
    {
        string result = name.ToString();
        ValidateIdentifier(result, "text scope");
        return result;
    }

    private static void ValidateIdentifier(string name, string role)
    {
        if (name.Length == 0)
        {
            throw new FormatException($"A {role} name cannot be empty.");
        }

        int segmentStart = 0;
        for (int index = 0; index <= name.Length; ++index)
        {
            if (index < name.Length && name[index] != '.')
            {
                continue;
            }

            ReadOnlySpan<char> segment = name.AsSpan(segmentStart, index - segmentStart);
            if (segment.IsEmpty || (!char.IsLetter(segment[0]) && segment[0] != '_'))
            {
                throw new FormatException($"The {role} name '{name}' is invalid.");
            }

            foreach (char character in segment[1..])
            {
                if (!char.IsLetterOrDigit(character) && character != '_')
                {
                    throw new FormatException($"The {role} name '{name}' is invalid.");
                }
            }

            segmentStart = index + 1;
        }
    }

    internal static string FormatValue(LogValue value, string? format = null, int? alignment = null)
    {
        string text = value.Kind switch
        {
            LogValueKind.Null => "null",
            LogValueKind.String or LogValueKind.Uri or LogValueKind.Enum or LogValueKind.CaptureError =>
                value.Value?.ToString() ?? string.Empty,
            LogValueKind.Sequence or LogValueKind.Structure => FormatStructuredValue(value),
            _ when value.Value is IFormattable formattable =>
                formattable.ToString(format, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.Value?.ToString() ?? string.Empty,
        };

        if (alignment is not int width || width == 0 || text.Length >= Math.Abs(width))
        {
            return text;
        }

        return width > 0 ? text.PadLeft(width) : text.PadRight(-width);
    }

    private static string FormatStructuredValue(LogValue value)
    {
        var builder = new StringBuilder();
        AppendStructuredValue(builder, value);
        return builder.ToString();
    }

    private static void AppendStructuredValue(StringBuilder builder, LogValue value)
    {
        switch (value.Kind)
        {
            case LogValueKind.Sequence:
                builder.Append('[');
                IReadOnlyList<LogValue> items = (IReadOnlyList<LogValue>)value.Value!;
                for (int index = 0; index < items.Count; ++index)
                {
                    if (index > 0)
                    {
                        builder.Append(", ");
                    }

                    AppendStructuredValue(builder, items[index]);
                }

                builder.Append(']');
                break;

            case LogValueKind.Structure:
                builder.Append('{');
                IReadOnlyList<LogStructureProperty> properties =
                    (IReadOnlyList<LogStructureProperty>)value.Value!;
                for (int index = 0; index < properties.Count; ++index)
                {
                    if (index > 0)
                    {
                        builder.Append(", ");
                    }

                    LogStructureProperty property = properties[index];
                    builder.Append(property.Name);
                    builder.Append('=');
                    AppendStructuredValue(builder, property.Value);
                }

                builder.Append('}');
                break;

            case LogValueKind.String:
                builder.Append('"');
                builder.Append(value.Value?.ToString());
                builder.Append('"');
                break;

            default:
                builder.Append(FormatValue(value));
                break;
        }
    }

    private static void FlushLiteral(ScopeBuilder scope, StringBuilder literal)
    {
        if (literal.Length == 0)
        {
            return;
        }

        scope.Children.Add(new LiteralText(literal.ToString()));
        literal.Clear();
    }

    private static string RenderPlainText(TextScope root)
    {
        var builder = new StringBuilder();
        AppendPlainText(builder, root);
        return builder.ToString();
    }

    private static void AppendPlainText(StringBuilder builder, LogTextNode node)
    {
        switch (node)
        {
            case LiteralText literal:
                builder.Append(literal.Content);
                break;
            case ParamText parameter:
                builder.Append(parameter.Property.FormattedText);
                break;
            case TextScope scope:
                foreach (LogTextNode child in scope.Children)
                {
                    AppendPlainText(builder, child);
                }

                break;
        }
    }

    private readonly record struct TemplateProperty(string Name, int? Alignment, string? Format);

    private readonly record struct RenderResult(
        TextScope Root,
        string Message,
        IReadOnlyList<LogProperty> Properties);

    private sealed class ScopeBuilder
    {
        internal ScopeBuilder(string? name, TextDecorator? decorator)
        {
            Name = name;
            Decorator = decorator;
        }

        internal string? Name { get; }

        internal TextDecorator? Decorator { get; }

        internal List<LogTextNode> Children { get; } = [];

        internal TextScope Build()
        {
            return Decorator is TextDecorator decorator
                ? new DecoratedTextScope(decorator, Children)
                : new TextScope(Children);
        }
    }
}
