using System.Globalization;

namespace Incant.Base.Cli;

/// <summary>Provides common metadata and parse state for a command-line option.</summary>
public abstract class Option : IOption
{
    /// <summary>Gets the long-option name without leading hyphens.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the optional short-option character.</summary>
    public char? ShortName { get; init; }

    /// <summary>Gets the user-facing option help text.</summary>
    public required string Help { get; init; }

    /// <summary>Gets a value indicating whether the option must be provided. The default is <see langword="true"/>.</summary>
    public bool IsRequired { get; init; } = true;

    /// <summary>Gets a value indicating whether parsing supplied this option.</summary>
    public bool WasProvided { get; private set; }

    /// <inheritdoc />
    public virtual bool IsToggle => false;

    /// <inheritdoc />
    public abstract string DefaultValue { get; }

    IEnumerable<string>? IOption.Selections => GetSelectionStrings();

    /// <inheritdoc />
    public abstract string ValueTypeName { get; }

    void IOption.Assign(ParseContext context, string value)
    {
        AssignCore(context, value);
    }

    void IOption.Toggle(ParseContext context)
    {
        ToggleCore(context);
    }

    /// <summary>Parses and stores a value supplied for this option.</summary>
    /// <param name="context">The active parse context.</param>
    /// <param name="value">The raw option value.</param>
    protected abstract void AssignCore(ParseContext context, string value);

    /// <summary>Returns formatted selection values for parsing and help output.</summary>
    /// <returns>The formatted selections, or <see langword="null"/> when values are unrestricted.</returns>
    protected abstract IEnumerable<string>? GetSelectionStrings();

    /// <summary>Marks this option as supplied by the current parse operation.</summary>
    protected void MarkProvided()
    {
        WasProvided = true;
    }

    /// <summary>Activates this option as a value-less toggle.</summary>
    /// <param name="context">The active parse context.</param>
    /// <exception cref="InvalidOperationException">This option is not a toggle.</exception>
    protected virtual void ToggleCore(ParseContext context)
    {
        throw new InvalidOperationException($"Option '--{Name}' is not a toggle option.");
    }
}

/// <summary>Stores a strongly typed command-line option value.</summary>
/// <typeparam name="TValue">The option value type.</typeparam>
public abstract class Option<TValue> : Option
{
    private TValue _value;
    private TValue _defaultValue;

    /// <summary>Initializes an option with its type-specific default value.</summary>
    /// <param name="defaultValue">The initial value used when the option is not supplied.</param>
    protected Option(TValue defaultValue)
    {
        _value = defaultValue;
        _defaultValue = defaultValue;
    }

    /// <summary>
    /// Gets the current value or configures the initial value before the option is parsed.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// The configured value is <see langword="null"/> and the concrete option does not support null values.
    /// </exception>
    public TValue Value
    {
        get => _value;
        init
        {
            if (value is null && !AllowsNullValue)
            {
                throw new ArgumentNullException(nameof(value));
            }

            _value = value;
            _defaultValue = value;
        }
    }

    /// <summary>Gets the statically configured accepted values.</summary>
    public IReadOnlyList<TValue> Selections { get; init; } = [];

    /// <summary>
    /// Gets a provider for accepted values when no static selections are configured.
    /// </summary>
    public Func<IEnumerable<TValue>>? SelectionProvider { get; init; }

    /// <inheritdoc />
    public sealed override string DefaultValue => FormatDefaultValue(_defaultValue);

    /// <summary>Gets a value indicating whether <see cref="Value"/> accepts <see langword="null"/>.</summary>
    protected virtual bool AllowsNullValue => false;

    /// <inheritdoc />
    protected override void AssignCore(ParseContext context, string value)
    {
        if (!TryParseValue(value, out TValue result))
        {
            context.Error(
                $"Option '--{Name}' requires a {ValueTypeName} value, but got '{value}'");
            return;
        }

        SetParsedValue(result);
    }

    /// <summary>Formats the configured initial value for help output.</summary>
    /// <param name="value">The initial value.</param>
    /// <returns>The formatted value.</returns>
    protected virtual string FormatDefaultValue(TValue value)
    {
        return FormatValue(value);
    }

    /// <summary>Formats a typed value for command-line comparison and display.</summary>
    /// <param name="value">The typed value.</param>
    /// <returns>The invariant command-line representation.</returns>
    protected abstract string FormatValue(TValue value);

    /// <inheritdoc />
    protected override IEnumerable<string>? GetSelectionStrings()
    {
        IReadOnlyList<TValue> selections = GetSelections();
        if (selections.Count == 0)
        {
            return null;
        }

        string[] formattedSelections = new string[selections.Count];
        for (int index = 0; index < selections.Count; ++index)
        {
            TValue selection = selections[index];
            if (selection is null)
            {
                throw new InvalidOperationException($"Option '--{Name}' contains a null selection.");
            }

            formattedSelections[index] = FormatValue(selection);
        }

        return formattedSelections;
    }

    /// <summary>Stores a parsed value and marks the option as supplied.</summary>
    /// <param name="value">The parsed value.</param>
    protected void SetParsedValue(TValue value)
    {
        _value = value;
        MarkProvided();
    }

    /// <summary>Attempts to parse a raw command-line value.</summary>
    /// <param name="value">The raw value.</param>
    /// <param name="result">The parsed value when parsing succeeds.</param>
    /// <returns><see langword="true"/> when parsing succeeds.</returns>
    protected abstract bool TryParseValue(string value, out TValue result);

    private IReadOnlyList<TValue> GetSelections()
    {
        if (Selections == null)
        {
            throw new InvalidOperationException($"Selections for option '--{Name}' must not be null.");
        }

        if (Selections.Count != 0 || SelectionProvider == null)
        {
            return Selections;
        }

        IEnumerable<TValue>? providedSelections = SelectionProvider.Invoke();
        if (providedSelections == null)
        {
            throw new InvalidOperationException(
                $"Selection provider for option '--{Name}' returned null.");
        }

        return providedSelections.ToArray();
    }
}

/// <summary>Stores an optional strongly typed value whose default is <see langword="null"/>.</summary>
/// <typeparam name="TValue">The non-nullable value type.</typeparam>
public abstract class NullableOption<TValue> : Option<TValue?>
    where TValue : struct
{
    /// <summary>Initializes an optional value with a <see langword="null"/> default.</summary>
    protected NullableOption()
        : base(null)
    {
        IsRequired = false;
    }

    /// <inheritdoc />
    protected sealed override bool AllowsNullValue => true;

    /// <inheritdoc />
    protected sealed override string FormatDefaultValue(TValue? value)
    {
        return value.HasValue ? FormatDefinedValue(value.Value) : "null";
    }

    /// <inheritdoc />
    protected sealed override string FormatValue(TValue? value)
    {
        if (!value.HasValue)
        {
            throw new InvalidOperationException(
                $"Selections for option '--{Name}' must not contain null.");
        }

        return FormatDefinedValue(value.Value);
    }

    /// <summary>Formats a defined value for command-line comparison and display.</summary>
    /// <param name="value">The defined value.</param>
    /// <returns>The invariant command-line representation.</returns>
    protected abstract string FormatDefinedValue(TValue value);

    /// <inheritdoc />
    protected sealed override bool TryParseValue(string value, out TValue? result)
    {
        if (TryParseDefinedValue(value, out TValue parsedValue))
        {
            result = parsedValue;
            return true;
        }

        result = null;
        return false;
    }

    /// <summary>Attempts to parse a defined command-line value.</summary>
    /// <param name="value">The raw value.</param>
    /// <param name="result">The parsed value when parsing succeeds.</param>
    /// <returns><see langword="true"/> when parsing succeeds.</returns>
    protected abstract bool TryParseDefinedValue(string value, out TValue result);
}

/// <summary>Represents a signed 32-bit integer command-line option.</summary>
public sealed class IntOption : Option<int>
{
    /// <summary>Initializes an integer option with a default value of zero.</summary>
    public IntOption()
        : base(0)
    {
    }

    /// <inheritdoc />
    public override string ValueTypeName => "int";

    /// <inheritdoc />
    protected override string FormatValue(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    protected override bool TryParseValue(string value, out int result)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }
}

/// <summary>Represents an unsigned 32-bit integer command-line option.</summary>
public sealed class UIntOption : Option<uint>
{
    /// <summary>Initializes an unsigned integer option with a default value of zero.</summary>
    public UIntOption()
        : base(0)
    {
    }

    /// <inheritdoc />
    public override string ValueTypeName => "uint";

    /// <inheritdoc />
    protected override string FormatValue(uint value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    protected override bool TryParseValue(string value, out uint result)
    {
        return uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }
}

/// <summary>Represents a single-precision floating-point command-line option.</summary>
public sealed class FloatOption : Option<float>
{
    /// <summary>Initializes a floating-point option with a default value of zero.</summary>
    public FloatOption()
        : base(0)
    {
    }

    /// <inheritdoc />
    public override string ValueTypeName => "float";

    /// <inheritdoc />
    protected override string FormatValue(float value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    protected override bool TryParseValue(string value, out float result)
    {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }
}

/// <summary>Represents a double-precision floating-point command-line option.</summary>
public sealed class DoubleOption : Option<double>
{
    /// <summary>Initializes a double-precision option with a default value of zero.</summary>
    public DoubleOption()
        : base(0)
    {
    }

    /// <inheritdoc />
    public override string ValueTypeName => "double";

    /// <inheritdoc />
    protected override string FormatValue(double value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    protected override bool TryParseValue(string value, out double result)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }
}

/// <summary>Represents an optional signed 32-bit integer command-line option.</summary>
public sealed class NullableIntOption : NullableOption<int>
{
    /// <summary>Initializes an optional integer option.</summary>
    public NullableIntOption()
    {
    }

    /// <inheritdoc />
    public override string ValueTypeName => "int?";

    /// <inheritdoc />
    protected override string FormatDefinedValue(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    protected override bool TryParseDefinedValue(string value, out int result)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }
}

/// <summary>Represents an optional unsigned 32-bit integer command-line option.</summary>
public sealed class NullableUIntOption : NullableOption<uint>
{
    /// <summary>Initializes an optional unsigned integer option.</summary>
    public NullableUIntOption()
    {
    }

    /// <inheritdoc />
    public override string ValueTypeName => "uint?";

    /// <inheritdoc />
    protected override string FormatDefinedValue(uint value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    protected override bool TryParseDefinedValue(string value, out uint result)
    {
        return uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }
}

/// <summary>Represents an optional single-precision floating-point command-line option.</summary>
public sealed class NullableFloatOption : NullableOption<float>
{
    /// <summary>Initializes an optional floating-point option.</summary>
    public NullableFloatOption()
    {
    }

    /// <inheritdoc />
    public override string ValueTypeName => "float?";

    /// <inheritdoc />
    protected override string FormatDefinedValue(float value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    protected override bool TryParseDefinedValue(string value, out float result)
    {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }
}

/// <summary>Represents an optional double-precision floating-point command-line option.</summary>
public sealed class NullableDoubleOption : NullableOption<double>
{
    /// <summary>Initializes an optional double-precision option.</summary>
    public NullableDoubleOption()
    {
    }

    /// <inheritdoc />
    public override string ValueTypeName => "double?";

    /// <inheritdoc />
    protected override string FormatDefinedValue(double value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    protected override bool TryParseDefinedValue(string value, out double result)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }
}

/// <summary>Represents a text command-line option.</summary>
public sealed class StringOption : Option<string>
{
    /// <summary>Initializes a string option with an empty default value.</summary>
    public StringOption()
        : base(string.Empty)
    {
    }

    /// <inheritdoc />
    public override string ValueTypeName => "string";

    /// <inheritdoc />
    protected override string FormatDefaultValue(string value)
    {
        string escapedValue = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{escapedValue}\"";
    }

    /// <inheritdoc />
    protected override string FormatValue(string value)
    {
        return value;
    }

    /// <inheritdoc />
    protected override bool TryParseValue(string value, out string result)
    {
        result = value;
        return true;
    }
}

/// <summary>Represents an optional text command-line option.</summary>
public sealed class NullableStringOption : Option<string?>
{
    /// <summary>Initializes an optional string option.</summary>
    public NullableStringOption()
        : base(null)
    {
        IsRequired = false;
    }

    /// <inheritdoc />
    public override string ValueTypeName => "string?";

    /// <inheritdoc />
    protected override bool AllowsNullValue => true;

    /// <inheritdoc />
    protected override string FormatDefaultValue(string? value)
    {
        return value == null ? "null" : Quote(value);
    }

    /// <inheritdoc />
    protected override string FormatValue(string? value)
    {
        return value
            ?? throw new InvalidOperationException(
                $"Selections for option '--{Name}' must not contain null.");
    }

    /// <inheritdoc />
    protected override bool TryParseValue(string value, out string? result)
    {
        result = value;
        return true;
    }

    private static string Quote(string value)
    {
        string escapedValue = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{escapedValue}\"";
    }
}

/// <summary>Represents a boolean option that becomes true when it is supplied.</summary>
public sealed class FlagOption : Option<bool>
{
    /// <summary>Initializes a flag option with a default value of <see langword="false"/>.</summary>
    public FlagOption()
        : base(false)
    {
    }

    /// <inheritdoc />
    public override bool IsToggle => true;

    /// <inheritdoc />
    public override string ValueTypeName => "bool";

    /// <inheritdoc />
    protected override void AssignCore(ParseContext context, string value)
    {
        throw new InvalidOperationException($"Flag option '--{Name}' does not accept a value.");
    }

    /// <inheritdoc />
    protected override string FormatValue(bool value)
    {
        return value ? "true" : "false";
    }

    /// <inheritdoc />
    protected override IEnumerable<string>? GetSelectionStrings()
    {
        return null;
    }

    /// <inheritdoc />
    protected override void ToggleCore(ParseContext context)
    {
        SetParsedValue(true);
    }

    /// <inheritdoc />
    protected override bool TryParseValue(string value, out bool result)
    {
        return bool.TryParse(value, out result);
    }
}

/// <summary>Collects command-line arguments that are not consumed as named options.</summary>
public sealed class RestOption : IRestOption
{
    private IReadOnlyList<string> _values = [];

    /// <summary>Gets the user-facing help text for remaining arguments.</summary>
    public required string Help { get; init; }

    /// <summary>Gets a value indicating whether at least one remaining argument must be provided.</summary>
    public bool IsRequired { get; init; }

    /// <summary>Gets a value indicating whether options may appear between remaining arguments.</summary>
    public bool AllowMixed { get; init; }

    /// <summary>Gets a value indicating whether remaining arguments require a preceding <c>--</c>.</summary>
    public bool RequireDoubleDash { get; init; }

    /// <summary>Gets the arguments collected by the parser.</summary>
    public IReadOnlyList<string> Values => _values;

    /// <summary>Gets a value indicating whether at least one remaining argument was supplied.</summary>
    public bool WasProvided { get; private set; }

    void IRestOption.Assign(ParseContext context, List<string> values)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(values);

        _values = [.. values];
        WasProvided = values.Count != 0;
    }
}

