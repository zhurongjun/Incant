namespace Incant.Base.Log;

/// <summary>Provides the extensibility base for text-scope decorations.</summary>
/// <remarks>
/// The logging framework preserves decorator instances without interpreting them. Implementations should be immutable
/// because sinks observe the same instances supplied at the logging call site.
/// </remarks>
public abstract class TextDecorator
{
    /// <summary>Initializes a text decorator.</summary>
    /// <param name="next">The next decorator, ordered from outermost to innermost.</param>
    protected TextDecorator(TextDecorator? next = null)
    {
        Next = next;
    }

    /// <summary>Gets the next decorator in the chain, or <see langword="null"/> at the end.</summary>
    public TextDecorator? Next { get; }
}

/// <summary>Provides the extensibility base for parameter decorations.</summary>
/// <remarks>
/// Except for <see cref="StructuredParamDecorator"/>, the logging framework preserves decorator instances without
/// interpreting them. Implementations should be immutable because sinks observe the same instances supplied at the
/// logging call site.
/// </remarks>
public abstract class ParamDecorator
{
    /// <summary>Initializes a parameter decorator.</summary>
    /// <param name="next">The next decorator or the final parameter value.</param>
    protected ParamDecorator(object? next)
    {
        Next = next;
    }

    /// <summary>Gets the next decorator or the final parameter value.</summary>
    public object? Next { get; }
}

/// <summary>Identifies a compact set of presentation-independent decoration roles.</summary>
public enum Role
{
    /// <summary>Ordinary content that explicitly uses the sink's default presentation.</summary>
    Plain,

    /// <summary>Supporting content intended to attract less attention than ordinary content.</summary>
    Muted,

    /// <summary>A significant result or visual element that should attract attention.</summary>
    Important,

    /// <summary>A warning that may require action.</summary>
    Warning,

    /// <summary>An error that requires attention.</summary>
    Error,

    /// <summary>A short label that identifies a category, state, or other compact value.</summary>
    Label,
}

/// <summary>Describes a text scope using a standard semantic role.</summary>
public sealed class TextDecoratorRole : TextDecorator
{
    /// <summary>Initializes a role-based text decorator.</summary>
    /// <param name="role">The semantic role.</param>
    /// <param name="next">The next decorator, ordered from outermost to innermost.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="role"/> is invalid.</exception>
    public TextDecoratorRole(Role role, TextDecorator? next = null)
        : base(next)
    {
        Role = RoleUtility.Validate(role);
    }

    /// <summary>Gets the semantic role.</summary>
    public Role Role { get; }
}

/// <summary>Describes a parameter using a standard semantic role.</summary>
public sealed class ParamDecoratorRole : ParamDecorator
{
    /// <summary>Initializes a role-based parameter decorator.</summary>
    /// <param name="role">The semantic role.</param>
    /// <param name="next">The next decorator or the final parameter value.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="role"/> is invalid.</exception>
    public ParamDecoratorRole(Role role, object? next)
        : base(next)
    {
        Role = RoleUtility.Validate(role);
    }

    /// <summary>Gets the semantic role.</summary>
    public Role Role { get; }
}

/// <summary>Requests an immutable structured snapshot of the final parameter value.</summary>
/// <remarks>This decorator is the only parameter decorator interpreted by the logging framework.</remarks>
public sealed class StructuredParamDecorator : ParamDecorator
{
    /// <summary>Initializes a structured-capture decorator.</summary>
    /// <param name="next">The next decorator or the final parameter value.</param>
    public StructuredParamDecorator(object? next)
        : base(next)
    {
    }
}

/// <summary>Creates role-based and structured parameter decorator chains.</summary>
public static class Param
{
    /// <summary>Creates a parameter decorator with a standard semantic role.</summary>
    /// <param name="role">The semantic role.</param>
    /// <param name="next">The next decorator or the final parameter value.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="role"/> is invalid.</exception>
    public static ParamDecoratorRole Role(Role role, object? next) => new(role, next);

    /// <summary>Creates an ordinary-content decorator.</summary>
    /// <param name="next">The next decorator or the final parameter value.</param>
    public static ParamDecoratorRole Plain(object? next) => new(Incant.Base.Log.Role.Plain, next);

    /// <summary>Creates a supporting, low-attention decorator.</summary>
    /// <param name="next">The next decorator or the final parameter value.</param>
    public static ParamDecoratorRole Muted(object? next) => new(Incant.Base.Log.Role.Muted, next);

    /// <summary>Creates an important-content decorator.</summary>
    /// <param name="next">The next decorator or the final parameter value.</param>
    public static ParamDecoratorRole Important(object? next) => new(Incant.Base.Log.Role.Important, next);

    /// <summary>Creates a warning decorator.</summary>
    /// <param name="next">The next decorator or the final parameter value.</param>
    public static ParamDecoratorRole Warning(object? next) => new(Incant.Base.Log.Role.Warning, next);

    /// <summary>Creates an error decorator.</summary>
    /// <param name="next">The next decorator or the final parameter value.</param>
    public static ParamDecoratorRole Error(object? next) => new(Incant.Base.Log.Role.Error, next);

    /// <summary>Creates a compact-label decorator.</summary>
    /// <param name="next">The next decorator or the final parameter value.</param>
    public static ParamDecoratorRole Label(object? next) => new(Incant.Base.Log.Role.Label, next);

    /// <summary>Requests an immutable structured snapshot when the event is enabled.</summary>
    /// <param name="value">The value to capture.</param>
    public static StructuredParamDecorator Structured(object? value) => new(value);
}

/// <summary>Creates role-based text decorator chains.</summary>
public static class Text
{
    private static readonly TextDecoratorRole s_plain = new(Role.Plain);
    private static readonly TextDecoratorRole s_muted = new(Role.Muted);
    private static readonly TextDecoratorRole s_important = new(Role.Important);
    private static readonly TextDecoratorRole s_warning = new(Role.Warning);
    private static readonly TextDecoratorRole s_error = new(Role.Error);
    private static readonly TextDecoratorRole s_label = new(Role.Label);

    /// <summary>Creates an ordinary-content decorator.</summary>
    /// <param name="next">The next decorator, ordered from outermost to innermost.</param>
    public static TextDecoratorRole Plain(TextDecorator? next = null) =>
        next is null ? s_plain : new TextDecoratorRole(Role.Plain, next);

    /// <summary>Creates a supporting, low-attention decorator.</summary>
    /// <param name="next">The next decorator, ordered from outermost to innermost.</param>
    public static TextDecoratorRole Muted(TextDecorator? next = null) =>
        next is null ? s_muted : new TextDecoratorRole(Role.Muted, next);

    /// <summary>Creates an important-content decorator.</summary>
    /// <param name="next">The next decorator, ordered from outermost to innermost.</param>
    public static TextDecoratorRole Important(TextDecorator? next = null) =>
        next is null ? s_important : new TextDecoratorRole(Role.Important, next);

    /// <summary>Creates a warning decorator.</summary>
    /// <param name="next">The next decorator, ordered from outermost to innermost.</param>
    public static TextDecoratorRole Warning(TextDecorator? next = null) =>
        next is null ? s_warning : new TextDecoratorRole(Role.Warning, next);

    /// <summary>Creates an error decorator.</summary>
    /// <param name="next">The next decorator, ordered from outermost to innermost.</param>
    public static TextDecoratorRole Error(TextDecorator? next = null) =>
        next is null ? s_error : new TextDecoratorRole(Role.Error, next);

    /// <summary>Creates a compact-label decorator.</summary>
    /// <param name="next">The next decorator, ordered from outermost to innermost.</param>
    public static TextDecoratorRole Label(TextDecorator? next = null) =>
        next is null ? s_label : new TextDecoratorRole(Role.Label, next);
}

internal static class RoleUtility
{
    internal static Role Validate(Role role)
    {
        return role is >= Role.Plain and <= Role.Label
            ? role
            : throw new ArgumentOutOfRangeException(nameof(role), role, "The decorator role is invalid.");
    }
}
