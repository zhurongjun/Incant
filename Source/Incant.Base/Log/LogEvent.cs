using System.Collections.ObjectModel;

namespace Incant.Base.Log;

/// <summary>Identifies the immutable representation of a captured parameter value.</summary>
public enum LogValueKind
{
    /// <summary>A null value.</summary>
    Null,

    /// <summary>A Boolean value.</summary>
    Boolean,

    /// <summary>A signed integer.</summary>
    SignedInteger,

    /// <summary>An unsigned integer.</summary>
    UnsignedInteger,

    /// <summary>A floating-point number.</summary>
    FloatingPoint,

    /// <summary>A decimal number.</summary>
    Decimal,

    /// <summary>A string.</summary>
    String,

    /// <summary>A globally unique identifier.</summary>
    Guid,

    /// <summary>A date and time.</summary>
    DateTime,

    /// <summary>A calendar date.</summary>
    Date,

    /// <summary>A time of day.</summary>
    Time,

    /// <summary>A duration.</summary>
    Duration,

    /// <summary>A URI.</summary>
    Uri,

    /// <summary>An enumeration value.</summary>
    Enum,

    /// <summary>An ordered sequence.</summary>
    Sequence,

    /// <summary>A named structure.</summary>
    Structure,

    /// <summary>A diagnostic produced because value capture failed.</summary>
    CaptureError,
}

/// <summary>Represents an immutable captured parameter value.</summary>
public sealed class LogValue
{
    internal LogValue(LogValueKind kind, object? value, string? typeName = null)
    {
        Kind = kind;
        Value = value;
        TypeName = typeName;
    }

    /// <summary>Gets the value representation.</summary>
    public LogValueKind Kind { get; }

    /// <summary>Gets the immutable value.</summary>
    /// <remarks>
    /// Sequence values contain <see cref="IReadOnlyList{T}"/> of <see cref="LogValue"/>. Structure values contain
    /// <see cref="IReadOnlyList{T}"/> of <see cref="LogStructureProperty"/>.
    /// </remarks>
    public object? Value { get; }

    /// <summary>Gets the original type name when it is relevant to the representation.</summary>
    public string? TypeName { get; }
}

/// <summary>Represents one named member of a captured structure.</summary>
public sealed class LogStructureProperty
{
    internal LogStructureProperty(string name, LogValue value)
    {
        Name = name;
        Value = value;
    }

    /// <summary>Gets the member name.</summary>
    public string Name { get; }

    /// <summary>Gets the member value.</summary>
    public LogValue Value { get; }
}

/// <summary>Represents one ordered structured property of a rendered event.</summary>
public sealed class LogProperty
{
    internal LogProperty(
        string name,
        LogValue value,
        string formattedText,
        ParamDecorator? decorator)
    {
        Name = name;
        Value = value;
        FormattedText = formattedText;
        Decorator = decorator;
    }

    /// <summary>Gets the property name from the message template.</summary>
    public string Name { get; }

    /// <summary>Gets the immutable captured value.</summary>
    public LogValue Value { get; }

    /// <summary>Gets the text inserted into the rendered message.</summary>
    public string FormattedText { get; }

    /// <summary>Gets the outermost parameter decorator, or <see langword="null"/> when the value is undecorated.</summary>
    public ParamDecorator? Decorator { get; }
}

/// <summary>Provides the common base for nodes in a rendered message tree.</summary>
public abstract class LogTextNode
{
    private protected LogTextNode()
    {
    }
}

/// <summary>Represents literal text in a rendered message.</summary>
public sealed class LiteralText : LogTextNode
{
    internal LiteralText(string content)
    {
        Content = content;
    }

    /// <summary>Gets the literal content.</summary>
    public string Content { get; }
}

/// <summary>Represents one formatted parameter in a rendered message.</summary>
public sealed class ParamText : LogTextNode
{
    internal ParamText(LogProperty property)
    {
        Property = property;
    }

    /// <summary>Gets the structured property represented by this leaf.</summary>
    public LogProperty Property { get; }
}

/// <summary>Represents an ordered group of rendered message nodes.</summary>
public class TextScope : LogTextNode
{
    private readonly ReadOnlyCollection<LogTextNode> _children;

    internal TextScope(IReadOnlyList<LogTextNode> children)
    {
        _children = Array.AsReadOnly(children.ToArray());
    }

    /// <summary>Gets the child nodes in display order.</summary>
    public IReadOnlyList<LogTextNode> Children => _children;
}

/// <summary>Represents a semantically decorated text scope.</summary>
public sealed class DecoratedTextScope : TextScope
{
    internal DecoratedTextScope(TextDecorator decorator, IReadOnlyList<LogTextNode> children)
        : base(children)
    {
        Decorator = decorator;
    }

    /// <summary>Gets the first decorator applied to the scope.</summary>
    public TextDecorator Decorator { get; }
}

/// <summary>Represents one immutable event after background parsing and formatting.</summary>
public sealed class RenderedLogEvent
{
    private readonly ReadOnlyCollection<LogProperty> _properties;

    internal RenderedLogEvent(
        DateTimeOffset timestamp,
        long elapsedNanoseconds,
        long sequence,
        LogLevel level,
        LogCategory category,
        int processId,
        int threadId,
        string threadName,
        string messageTemplate,
        string message,
        TextScope root,
        IReadOnlyList<LogProperty> properties,
        string? exceptionText,
        string? templateError)
    {
        Timestamp = timestamp;
        ElapsedNanoseconds = elapsedNanoseconds;
        Sequence = sequence;
        Level = level;
        Category = category;
        ProcessId = processId;
        ThreadId = threadId;
        ThreadName = threadName;
        MessageTemplate = messageTemplate;
        Message = message;
        Root = root;
        _properties = Array.AsReadOnly(properties.ToArray());
        ExceptionText = exceptionText;
        TemplateError = templateError;
    }

    /// <summary>Gets the stable UTC timestamp mapped from the monotonic clock.</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>Gets the elapsed time since the runtime started, in nanoseconds.</summary>
    public long ElapsedNanoseconds { get; }

    /// <summary>Gets the output sequence assigned by the worker.</summary>
    public long Sequence { get; }

    /// <summary>Gets the event level.</summary>
    public LogLevel Level { get; }

    /// <summary>Gets the event category.</summary>
    public LogCategory Category { get; }

    /// <summary>Gets the operating-system process identifier.</summary>
    public int ProcessId { get; }

    /// <summary>Gets the managed thread identifier.</summary>
    public int ThreadId { get; }

    /// <summary>Gets the thread name captured when its producer was registered.</summary>
    public string ThreadName { get; }

    /// <summary>Gets the original message template.</summary>
    public string MessageTemplate { get; }

    /// <summary>Gets the formatted plain-text message.</summary>
    public string Message { get; }

    /// <summary>Gets the root of the immutable rendered text tree.</summary>
    public TextScope Root { get; }

    /// <summary>Gets properties in their template occurrence order.</summary>
    public IReadOnlyList<LogProperty> Properties => _properties;

    /// <summary>Gets the stable exception snapshot, when present.</summary>
    public string? ExceptionText { get; }

    /// <summary>Gets the template diagnostic when rendering used the fallback path.</summary>
    public string? TemplateError { get; }
}
