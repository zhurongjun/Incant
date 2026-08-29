namespace Incant.Base.Log;

/// <summary>Controls ANSI color emission for <see cref="CliLogSink"/>.</summary>
public enum CliColorMode
{
    /// <summary>Uses color only for a non-redirected standard output stream.</summary>
    Auto,

    /// <summary>Always emits ANSI color sequences.</summary>
    Always,

    /// <summary>Never emits ANSI color sequences.</summary>
    Never,
}

/// <summary>Writes compact, semantically colored log events to a command-line stream.</summary>
public sealed class CliLogSink : ILogSink
{
    private const string ResetStyle = "\u001b[0m";

    private readonly CliColorMode _colorMode;
    private readonly bool _includePrefix;
    private readonly TextWriter _writer;
    private bool _isDisposed;

    /// <summary>Initializes a sink that writes to standard output.</summary>
    /// <param name="minimumLevel">The minimum accepted level. The default is <see cref="LogLevel.Info"/>.</param>
    /// <param name="colorMode">The ANSI color policy.</param>
    /// <param name="includePrefix">Whether to include the event level and category before the message.</param>
    public CliLogSink(
        LogLevel minimumLevel = LogLevel.Info,
        CliColorMode colorMode = CliColorMode.Auto,
        bool includePrefix = false)
        : this(Console.Out, minimumLevel, colorMode, includePrefix)
    {
    }

    /// <summary>Initializes a sink that writes to a caller-owned text writer.</summary>
    /// <param name="writer">The destination writer. The sink does not own it.</param>
    /// <param name="minimumLevel">The minimum accepted level. The default is <see cref="LogLevel.Info"/>.</param>
    /// <param name="colorMode">The ANSI color policy.</param>
    /// <param name="includePrefix">Whether to include the event level and category before the message.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is null.</exception>
    public CliLogSink(
        TextWriter writer,
        LogLevel minimumLevel = LogLevel.Info,
        CliColorMode colorMode = CliColorMode.Auto,
        bool includePrefix = false)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
        MinimumLevel = minimumLevel;
        _colorMode = colorMode;
        _includePrefix = includePrefix;
    }

    /// <inheritdoc />
    public LogLevel MinimumLevel { get; }

    /// <inheritdoc />
    public void Start(LogSinkContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    /// <inheritdoc />
    public void Emit(RenderedLogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        bool useColor = ShouldUseColor();
        if (_includePrefix)
        {
            WritePrefix(logEvent, useColor);
        }

        WriteNode(logEvent.Root, string.Empty, useColor);
        if (useColor)
        {
            _writer.Write(ResetStyle);
        }

        _writer.WriteLine();
    }

    /// <inheritdoc />
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        _writer.Flush();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _writer.Flush();
    }

    private void WritePrefix(RenderedLogEvent logEvent, bool useColor)
    {
        if (useColor)
        {
            _writer.Write(GetLevelStyle(logEvent.Level));
        }

        _writer.Write('[');
        _writer.Write(LogLevelText.Format(logEvent.Level));
        _writer.Write(']');
        if (useColor)
        {
            _writer.Write(ResetStyle);
            _writer.Write(GetRoleStyle(Role.Muted));
        }

        _writer.Write(" [");
        _writer.Write(logEvent.Category.Name);
        _writer.Write(']');
        if (useColor)
        {
            _writer.Write(ResetStyle);
        }

        _writer.Write(' ');
    }

    private bool ShouldUseColor()
    {
        return _colorMode switch
        {
            CliColorMode.Always => true,
            CliColorMode.Never => false,
            CliColorMode.Auto => ReferenceEquals(_writer, Console.Out) && !Console.IsOutputRedirected,
            _ => false,
        };
    }

    private void WriteNode(LogTextNode node, string inheritedStyle, bool useColor)
    {
        switch (node)
        {
            case LiteralText literal:
                _writer.Write(literal.Content);
                break;
            case ParamText parameter:
                WriteParameter(parameter.Property, inheritedStyle, useColor);
                break;
            case TextScope scope:
                string scopeStyle = inheritedStyle;
                TextDecorator? decorator = (scope as DecoratedTextScope)?.Decorator;
                bool appliesRole = false;
                Role selectedRole = default;
                while (decorator is not null)
                {
                    if (decorator is TextDecoratorRole roleDecorator)
                    {
                        appliesRole = true;
                        selectedRole = roleDecorator.Role;
                    }

                    decorator = decorator.Next;
                }

                if (appliesRole)
                {
                    scopeStyle = GetRoleStyle(selectedRole);
                    if (useColor)
                    {
                        WriteRoleStyle(scopeStyle);
                    }
                }

                foreach (LogTextNode child in scope.Children)
                {
                    WriteNode(child, scopeStyle, useColor);
                }

                if (useColor && appliesRole)
                {
                    RestoreStyle(inheritedStyle);
                }

                break;
        }
    }

    private void WriteParameter(LogProperty property, string inheritedStyle, bool useColor)
    {
        bool appliesRole = false;
        Role selectedRole = default;
        if (useColor)
        {
            object? current = property.Decorator;
            while (current is ParamDecorator decorator)
            {
                if (decorator is ParamDecoratorRole roleDecorator)
                {
                    appliesRole = true;
                    selectedRole = roleDecorator.Role;
                }

                current = decorator.Next;
            }

            if (appliesRole)
            {
                WriteRoleStyle(GetRoleStyle(selectedRole));
            }
        }

        _writer.Write(property.FormattedText);
        if (useColor && appliesRole)
        {
            RestoreStyle(inheritedStyle);
        }
    }

    private void WriteRoleStyle(string style)
    {
        _writer.Write(ResetStyle);
        if (style.Length != 0)
        {
            _writer.Write(style);
        }
    }

    private void RestoreStyle(string inheritedStyle)
    {
        _writer.Write(ResetStyle);
        if (inheritedStyle.Length != 0)
        {
            _writer.Write(inheritedStyle);
        }
    }

    private static string GetLevelStyle(LogLevel level)
    {
        return level switch
        {
            LogLevel.Trace => "\u001b[90m",
            LogLevel.Debug => "\u001b[36m",
            LogLevel.Info => "\u001b[32m",
            LogLevel.Warning => "\u001b[33m",
            LogLevel.Error => "\u001b[31m",
            LogLevel.Fatal => "\u001b[1;31m",
            _ => ResetStyle,
        };
    }

    private static string GetRoleStyle(Role role)
    {
        return role switch
        {
            Role.Plain => string.Empty,
            Role.Muted => "\u001b[90m",
            Role.Important => "\u001b[1;32m",
            Role.Warning => "\u001b[1;33m",
            Role.Error => "\u001b[1;31m",
            Role.Label => "\u001b[1;35m",
            _ => string.Empty,
        };
    }
}
