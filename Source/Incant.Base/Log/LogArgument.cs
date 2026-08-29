using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Incant.Base.Log;

internal enum CapturedArgumentKind
{
    Null,
    Boolean,
    SignedInteger,
    UnsignedInteger,
    FloatingPoint,
    Decimal,
    String,
    Guid,
    DateTime,
    DateTimeOffset,
    Date,
    Time,
    Duration,
    Uri,
    Enum,
    Structured,
    CaptureError,
    TextDecorator,
}

internal struct LogArgument
{
    private CapturedArgumentKind _kind;
    private object? _reference;
    private string? _text;
    private long _signed;
    private ulong _unsigned;
    private double _floatingPoint;
    private decimal _decimal;
    private Guid _guid;
    private JsonElement _structured;
    private TextDecorator? _textDecorator;
    private ParamDecorator? _paramDecorator;

    internal ParamDecorator? ParamDecorator => _paramDecorator;

    internal bool TryGetTextDecorator(out TextDecorator decorator)
    {
        decorator = _textDecorator!;
        return _kind == CapturedArgumentKind.TextDecorator;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static LogArgument Capture<TValue>(TValue value)
    {
        if (value is null)
        {
            return CreateNull();
        }

        if (typeof(TValue) == typeof(bool))
        {
            bool captured = Unsafe.As<TValue, bool>(ref value);
            return new LogArgument
            {
                _kind = CapturedArgumentKind.Boolean,
                _signed = captured ? 1 : 0,
            };
        }

        if (typeof(TValue) == typeof(byte))
        {
            return CreateUnsigned(Unsafe.As<TValue, byte>(ref value));
        }

        if (typeof(TValue) == typeof(ushort))
        {
            return CreateUnsigned(Unsafe.As<TValue, ushort>(ref value));
        }

        if (typeof(TValue) == typeof(uint))
        {
            return CreateUnsigned(Unsafe.As<TValue, uint>(ref value));
        }

        if (typeof(TValue) == typeof(ulong))
        {
            return CreateUnsigned(Unsafe.As<TValue, ulong>(ref value));
        }

        if (typeof(TValue) == typeof(nuint))
        {
            return CreateUnsigned(Unsafe.As<TValue, nuint>(ref value));
        }

        if (typeof(TValue) == typeof(sbyte))
        {
            return CreateSigned(Unsafe.As<TValue, sbyte>(ref value));
        }

        if (typeof(TValue) == typeof(short))
        {
            return CreateSigned(Unsafe.As<TValue, short>(ref value));
        }

        if (typeof(TValue) == typeof(int))
        {
            return CreateSigned(Unsafe.As<TValue, int>(ref value));
        }

        if (typeof(TValue) == typeof(long))
        {
            return CreateSigned(Unsafe.As<TValue, long>(ref value));
        }

        if (typeof(TValue) == typeof(nint))
        {
            return CreateSigned(Unsafe.As<TValue, nint>(ref value));
        }

        if (typeof(TValue) == typeof(char))
        {
            char captured = Unsafe.As<TValue, char>(ref value);
            return CreateString(captured.ToString());
        }

        if (typeof(TValue) == typeof(float))
        {
            return CreateFloatingPoint(Unsafe.As<TValue, float>(ref value));
        }

        if (typeof(TValue) == typeof(double))
        {
            return CreateFloatingPoint(Unsafe.As<TValue, double>(ref value));
        }

        if (typeof(TValue) == typeof(decimal))
        {
            return new LogArgument
            {
                _kind = CapturedArgumentKind.Decimal,
                _decimal = Unsafe.As<TValue, decimal>(ref value),
            };
        }

        if (typeof(TValue) == typeof(string))
        {
            return CreateString(Unsafe.As<TValue, string>(ref value));
        }

        if (typeof(TValue) == typeof(Guid))
        {
            return new LogArgument
            {
                _kind = CapturedArgumentKind.Guid,
                _guid = Unsafe.As<TValue, Guid>(ref value),
            };
        }

        if (typeof(TValue) == typeof(DateTime))
        {
            DateTime captured = Unsafe.As<TValue, DateTime>(ref value);
            return new LogArgument
            {
                _kind = CapturedArgumentKind.DateTime,
                _signed = captured.ToBinary(),
            };
        }

        if (typeof(TValue) == typeof(DateTimeOffset))
        {
            DateTimeOffset captured = Unsafe.As<TValue, DateTimeOffset>(ref value);
            return new LogArgument
            {
                _kind = CapturedArgumentKind.DateTimeOffset,
                _signed = captured.Ticks,
                _unsigned = unchecked((ulong)captured.Offset.Ticks),
            };
        }

        if (typeof(TValue) == typeof(DateOnly))
        {
            DateOnly captured = Unsafe.As<TValue, DateOnly>(ref value);
            return new LogArgument
            {
                _kind = CapturedArgumentKind.Date,
                _signed = captured.DayNumber,
            };
        }

        if (typeof(TValue) == typeof(TimeOnly))
        {
            TimeOnly captured = Unsafe.As<TValue, TimeOnly>(ref value);
            return new LogArgument
            {
                _kind = CapturedArgumentKind.Time,
                _signed = captured.Ticks,
            };
        }

        if (typeof(TValue) == typeof(TimeSpan))
        {
            TimeSpan captured = Unsafe.As<TValue, TimeSpan>(ref value);
            return new LogArgument
            {
                _kind = CapturedArgumentKind.Duration,
                _signed = captured.Ticks,
            };
        }

        if (!typeof(TValue).IsValueType && value is TextDecorator textDecorator)
        {
            return new LogArgument
            {
                _kind = CapturedArgumentKind.TextDecorator,
                _textDecorator = textDecorator,
            };
        }

        if (!typeof(TValue).IsValueType && value is ParamDecorator paramDecorator)
        {
            return CaptureParamDecorator(paramDecorator);
        }

        if (typeof(TValue).IsEnum)
        {
            return CaptureEnum(value);
        }

        return CaptureObject(value, false, null);
    }

    internal static LogArgument CaptureObjectValue(object? value)
    {
        return value switch
        {
            null => CreateNull(),
            bool captured => Capture(captured),
            byte captured => Capture(captured),
            ushort captured => Capture(captured),
            uint captured => Capture(captured),
            ulong captured => Capture(captured),
            nuint captured => Capture(captured),
            sbyte captured => Capture(captured),
            short captured => Capture(captured),
            int captured => Capture(captured),
            long captured => Capture(captured),
            nint captured => Capture(captured),
            char captured => Capture(captured),
            float captured => Capture(captured),
            double captured => Capture(captured),
            decimal captured => Capture(captured),
            string captured => Capture(captured),
            Guid captured => Capture(captured),
            DateTime captured => Capture(captured),
            DateTimeOffset captured => Capture(captured),
            DateOnly captured => Capture(captured),
            TimeOnly captured => Capture(captured),
            TimeSpan captured => Capture(captured),
            TextDecorator captured => Capture(captured),
            ParamDecorator decorator => CaptureParamDecorator(decorator),
            Enum captured => CaptureEnumObject(captured),
            _ => CaptureObject(value, false, null),
        };
    }

    internal LogValue ToLogValue()
    {
        return _kind switch
        {
            CapturedArgumentKind.Null => new LogValue(LogValueKind.Null, null),
            CapturedArgumentKind.Boolean => new LogValue(LogValueKind.Boolean, _signed != 0),
            CapturedArgumentKind.SignedInteger => new LogValue(LogValueKind.SignedInteger, _signed),
            CapturedArgumentKind.UnsignedInteger => new LogValue(LogValueKind.UnsignedInteger, _unsigned),
            CapturedArgumentKind.FloatingPoint => new LogValue(LogValueKind.FloatingPoint, _floatingPoint),
            CapturedArgumentKind.Decimal => new LogValue(LogValueKind.Decimal, _decimal),
            CapturedArgumentKind.String => new LogValue(LogValueKind.String, _text ?? string.Empty),
            CapturedArgumentKind.Guid => new LogValue(LogValueKind.Guid, _guid),
            CapturedArgumentKind.DateTime => new LogValue(LogValueKind.DateTime, DateTime.FromBinary(_signed)),
            CapturedArgumentKind.DateTimeOffset => new LogValue(
                LogValueKind.DateTime,
                new DateTimeOffset(_signed, new TimeSpan(unchecked((long)_unsigned)))),
            CapturedArgumentKind.Date => new LogValue(LogValueKind.Date, DateOnly.FromDayNumber((int)_signed)),
            CapturedArgumentKind.Time => new LogValue(LogValueKind.Time, new TimeOnly(_signed)),
            CapturedArgumentKind.Duration => new LogValue(LogValueKind.Duration, new TimeSpan(_signed)),
            CapturedArgumentKind.Uri => new LogValue(LogValueKind.Uri, _text ?? string.Empty),
            CapturedArgumentKind.Enum => CreateEnumValue(),
            CapturedArgumentKind.Structured => ConvertStructuredValue(_structured),
            CapturedArgumentKind.CaptureError => new LogValue(LogValueKind.CaptureError, _text ?? string.Empty),
            CapturedArgumentKind.TextDecorator => new LogValue(
                LogValueKind.CaptureError,
                "A text decorator was supplied where a property value was expected."),
            _ => new LogValue(LogValueKind.CaptureError, "The captured value kind is invalid."),
        };
    }

    internal void Reset()
    {
        this = default;
    }

    private static LogArgument CaptureParamDecorator(ParamDecorator decorator)
    {
        bool isStructured = false;
        object? value = decorator;
        while (value is ParamDecorator current)
        {
            isStructured |= current is StructuredParamDecorator;
            value = current.Next;
        }

        return CaptureObject(value, isStructured, decorator);
    }

    private static LogArgument CaptureObject(
        object? value,
        bool isStructured,
        ParamDecorator? decorator)
    {
        LogArgument argument;
        if (value is null)
        {
            argument = CreateNull();
        }
        else if (isStructured)
        {
            try
            {
                JsonElement snapshot = JsonSerializer.SerializeToElement(
                    value,
                    value.GetType(),
                    JsonSerializerOptions.Default);
                argument = new LogArgument
                {
                    _kind = CapturedArgumentKind.Structured,
                    _structured = snapshot,
                };
            }
            catch (Exception exception)
            {
                argument = CreateCaptureError(exception);
            }
        }
        else
        {
            argument = value switch
            {
                bool boolean => new LogArgument
                {
                    _kind = CapturedArgumentKind.Boolean,
                    _signed = boolean ? 1 : 0,
                },
                byte number => CreateUnsigned(number),
                ushort number => CreateUnsigned(number),
                uint number => CreateUnsigned(number),
                ulong number => CreateUnsigned(number),
                nuint number => CreateUnsigned(number),
                sbyte number => CreateSigned(number),
                short number => CreateSigned(number),
                int number => CreateSigned(number),
                long number => CreateSigned(number),
                nint number => CreateSigned(number),
                float number => CreateFloatingPoint(number),
                double number => CreateFloatingPoint(number),
                decimal number => new LogArgument
                {
                    _kind = CapturedArgumentKind.Decimal,
                    _decimal = number,
                },
                char character => CreateString(character.ToString()),
                string text => CreateString(text),
                Guid guid => new LogArgument
                {
                    _kind = CapturedArgumentKind.Guid,
                    _guid = guid,
                },
                DateTime dateTime => new LogArgument
                {
                    _kind = CapturedArgumentKind.DateTime,
                    _signed = dateTime.ToBinary(),
                },
                DateTimeOffset dateTimeOffset => new LogArgument
                {
                    _kind = CapturedArgumentKind.DateTimeOffset,
                    _signed = dateTimeOffset.Ticks,
                    _unsigned = unchecked((ulong)dateTimeOffset.Offset.Ticks),
                },
                DateOnly date => new LogArgument
                {
                    _kind = CapturedArgumentKind.Date,
                    _signed = date.DayNumber,
                },
                TimeOnly time => new LogArgument
                {
                    _kind = CapturedArgumentKind.Time,
                    _signed = time.Ticks,
                },
                TimeSpan duration => new LogArgument
                {
                    _kind = CapturedArgumentKind.Duration,
                    _signed = duration.Ticks,
                },
                Uri uri => new LogArgument
                {
                    _kind = CapturedArgumentKind.Uri,
                    _text = uri.OriginalString,
                },
                Enum enumeration => CaptureEnumObject(enumeration),
                _ => CaptureStringSnapshot(value),
            };
        }

        argument._paramDecorator = decorator;

        return argument;
    }

    private static LogArgument CaptureStringSnapshot(object value)
    {
        try
        {
            return CreateString(value.ToString() ?? string.Empty);
        }
        catch (Exception exception)
        {
            return CreateCaptureError(exception);
        }
    }

    private static LogArgument CaptureEnum<TValue>(TValue value)
    {
        try
        {
            Type underlyingType = EnumCaptureInfo<TValue>.UnderlyingType;
            bool isUnsigned = EnumCaptureInfo<TValue>.IsUnsigned;
            ulong unsigned = 0;
            long signed = 0;
            if (isUnsigned)
            {
                unsigned = underlyingType == typeof(byte)
                    ? Unsafe.As<TValue, byte>(ref value)
                    : underlyingType == typeof(ushort)
                        ? Unsafe.As<TValue, ushort>(ref value)
                        : underlyingType == typeof(uint)
                            ? Unsafe.As<TValue, uint>(ref value)
                            : Unsafe.As<TValue, ulong>(ref value);
            }
            else
            {
                signed = underlyingType == typeof(sbyte)
                    ? Unsafe.As<TValue, sbyte>(ref value)
                    : underlyingType == typeof(short)
                        ? Unsafe.As<TValue, short>(ref value)
                        : underlyingType == typeof(int)
                            ? Unsafe.As<TValue, int>(ref value)
                            : Unsafe.As<TValue, long>(ref value);
            }

            return new LogArgument
            {
                _kind = CapturedArgumentKind.Enum,
                _reference = typeof(TValue),
                _signed = signed,
                _unsigned = unsigned,
                _text = isUnsigned ? "unsigned" : "signed",
            };
        }
        catch (Exception exception)
        {
            return CreateCaptureError(exception);
        }
    }

    private static LogArgument CaptureEnumObject(Enum value)
    {
        Type underlyingType = Enum.GetUnderlyingType(value.GetType());
        bool isUnsigned = underlyingType == typeof(byte)
            || underlyingType == typeof(ushort)
            || underlyingType == typeof(uint)
            || underlyingType == typeof(ulong);

        return new LogArgument
        {
            _kind = CapturedArgumentKind.Enum,
            _reference = value.GetType(),
            _signed = isUnsigned ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture),
            _unsigned = isUnsigned ? Convert.ToUInt64(value, CultureInfo.InvariantCulture) : 0,
            _text = isUnsigned ? "unsigned" : "signed",
        };
    }

    private LogValue CreateEnumValue()
    {
        Type type = (Type)_reference!;
        object value = _text == "unsigned"
            ? Enum.ToObject(type, _unsigned)
            : Enum.ToObject(type, _signed);
        return new LogValue(LogValueKind.Enum, value.ToString() ?? string.Empty, type.FullName ?? type.Name);
    }

    private static LogValue ConvertStructuredValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => new LogValue(LogValueKind.Null, null),
            JsonValueKind.True => new LogValue(LogValueKind.Boolean, true),
            JsonValueKind.False => new LogValue(LogValueKind.Boolean, false),
            JsonValueKind.String => new LogValue(LogValueKind.String, element.GetString() ?? string.Empty),
            JsonValueKind.Number => ConvertStructuredNumber(element),
            JsonValueKind.Array => ConvertStructuredSequence(element),
            JsonValueKind.Object => ConvertStructuredObject(element),
            _ => new LogValue(LogValueKind.CaptureError, "The structured value kind is unsupported."),
        };
    }

    private static LogValue ConvertStructuredNumber(JsonElement element)
    {
        if (element.TryGetInt64(out long signed))
        {
            return new LogValue(LogValueKind.SignedInteger, signed);
        }

        if (element.TryGetUInt64(out ulong unsigned))
        {
            return new LogValue(LogValueKind.UnsignedInteger, unsigned);
        }

        if (element.TryGetDecimal(out decimal decimalValue))
        {
            return new LogValue(LogValueKind.Decimal, decimalValue);
        }

        return new LogValue(LogValueKind.FloatingPoint, element.GetDouble());
    }

    private static LogValue ConvertStructuredSequence(JsonElement element)
    {
        var items = new List<LogValue>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            items.Add(ConvertStructuredValue(item));
        }

        ReadOnlyCollection<LogValue> values = Array.AsReadOnly(items.ToArray());
        return new LogValue(LogValueKind.Sequence, values);
    }

    private static LogValue ConvertStructuredObject(JsonElement element)
    {
        var properties = new List<LogStructureProperty>();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            properties.Add(new LogStructureProperty(property.Name, ConvertStructuredValue(property.Value)));
        }

        ReadOnlyCollection<LogStructureProperty> values = Array.AsReadOnly(properties.ToArray());
        return new LogValue(LogValueKind.Structure, values);
    }

    private static LogArgument CreateNull() => new()
    {
        _kind = CapturedArgumentKind.Null,
    };

    private static LogArgument CreateSigned(long value) => new()
    {
        _kind = CapturedArgumentKind.SignedInteger,
        _signed = value,
    };

    private static LogArgument CreateUnsigned(ulong value) => new()
    {
        _kind = CapturedArgumentKind.UnsignedInteger,
        _unsigned = value,
    };

    private static LogArgument CreateFloatingPoint(double value) => new()
    {
        _kind = CapturedArgumentKind.FloatingPoint,
        _floatingPoint = value,
    };

    private static LogArgument CreateString(string value) => new()
    {
        _kind = CapturedArgumentKind.String,
        _text = value,
    };

    private static LogArgument CreateCaptureError(Exception exception)
    {
        string message;
        try
        {
            message = exception.Message;
        }
        catch
        {
            message = "Exception details are unavailable.";
        }

        return new LogArgument
        {
            _kind = CapturedArgumentKind.CaptureError,
            _text = $"{exception.GetType().FullName}: {message}",
        };
    }

    private static class EnumCaptureInfo<TValue>
    {
        internal static Type UnderlyingType { get; } = Enum.GetUnderlyingType(typeof(TValue));

        internal static bool IsUnsigned { get; } = UnderlyingType == typeof(byte)
            || UnderlyingType == typeof(ushort)
            || UnderlyingType == typeof(uint)
            || UnderlyingType == typeof(ulong);
    }
}
