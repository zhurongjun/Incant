using System.Globalization;
using System.Text;

namespace Incant.Base.Log;

/// <summary>Writes detailed events using the Incant v1 streaming text protocol.</summary>
public sealed class FileLogSink : ILogSink
{
    private const int DefaultBufferSize = 16 * 1024;

    private readonly int _bufferSize;
    private readonly string _path;
    private StreamWriter? _writer;
    private bool _isDisposed;

    /// <summary>Initializes a file sink.</summary>
    /// <param name="path">The file to create or truncate when logging starts.</param>
    /// <param name="minimumLevel">The minimum accepted level.</param>
    /// <param name="bufferSize">The stream and text buffer size.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bufferSize"/> is not positive.</exception>
    public FileLogSink(
        string path,
        LogLevel minimumLevel = LogLevel.Trace,
        int bufferSize = DefaultBufferSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        _path = path;
        _bufferSize = bufferSize;
        MinimumLevel = minimumLevel;
    }

    /// <inheritdoc />
    public LogLevel MinimumLevel { get; }

    /// <summary>Gets the configured file path.</summary>
    public string Path => _path;

    /// <inheritdoc />
    public void Start(LogSinkContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_writer is not null)
        {
            throw new InvalidOperationException("The file log sink is already started.");
        }

        string fullPath = System.IO.Path.GetFullPath(_path);
        string? directory = System.IO.Path.GetDirectoryName(fullPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        var stream = new FileStream(
            fullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            _bufferSize,
            FileOptions.SequentialScan);
        try
        {
            _writer = new StreamWriter(stream, new UTF8Encoding(false), _bufferSize, false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void Emit(RenderedLogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        StreamWriter writer = GetWriter();

        writer.Write("@1 ");
        writer.Write(logEvent.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        writer.Write(" [P");
        writer.Write(logEvent.ProcessId.ToString(CultureInfo.InvariantCulture));
        writer.Write(":T");
        writer.Write(logEvent.ThreadId.ToString(CultureInfo.InvariantCulture));
        writer.Write("] [");
        writer.Write(LogLevelText.Format(logEvent.Level));
        writer.Write("] [");
        writer.Write(logEvent.Category.Name);
        writer.WriteLine(']');

        WriteContent(writer, logEvent.Message);
        foreach (LogProperty property in logEvent.Properties)
        {
            writer.Write(":: property ");
            writer.Write(property.Name);
            writer.Write(": ");
            writer.WriteLine(LogFileValueCodec.Format(property.Value));
        }

        if (logEvent.ExceptionText is not null)
        {
            WriteDetail(writer, "exception", LogFileEscaping.Quote(logEvent.ExceptionText));
        }

        if (logEvent.TemplateError is not null)
        {
            WriteDetail(writer, "template-error", LogFileEscaping.Quote(logEvent.TemplateError));
        }

        writer.WriteLine();
        if (logEvent.Level >= LogLevel.Error)
        {
            writer.Flush();
        }
    }

    /// <inheritdoc />
    public void Flush()
    {
        GetWriter().Flush();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        StreamWriter? writer = _writer;
        _writer = null;
        _isDisposed = true;
        writer?.Dispose();
    }

    private static void WriteContent(TextWriter writer, string message)
    {
        int lineStart = 0;
        for (int index = 0; index < message.Length; ++index)
        {
            char character = message[index];
            if (character is not ('\r' or '\n'))
            {
                continue;
            }

            WriteContentLine(writer, message.AsSpan(lineStart, index - lineStart));
            if (character == '\r' && index + 1 < message.Length && message[index + 1] == '\n')
            {
                ++index;
            }

            lineStart = index + 1;
        }

        WriteContentLine(writer, message.AsSpan(lineStart));
    }

    private static void WriteContentLine(TextWriter writer, ReadOnlySpan<char> content)
    {
        writer.Write(">>");
        if (!content.IsEmpty)
        {
            writer.Write(' ');
            writer.Write(LogFileEscaping.EscapeContentLine(content));
        }

        writer.WriteLine();
    }

    private static void WriteDetail(TextWriter writer, string name, string value)
    {
        writer.Write(":: ");
        writer.Write(name);
        writer.Write(": ");
        writer.WriteLine(value);
    }

    private StreamWriter GetWriter()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        return _writer ?? throw new InvalidOperationException("The file log sink is not started.");
    }
}
