using System.Text;
using Incant.Base.Log;
using LogRecorder = Incant.Base.Log.Log;

namespace Incant.UnitTest.Base.Log;

[Collection(LogCollection.Name)]
public sealed class LogFileTests : LogTestBase
{
    [Fact]
    public async Task FileSinkAndReadersRoundTripMetadataEscapesAndAllValueFamilies()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string path = System.IO.Path.Combine(temporaryDirectory.Path, "nested", "build.log");
        var exception = new InvalidOperationException("first line\nsecond line");
        Guid guid = Guid.Parse("9a808bad-57d8-474a-bb27-5e5cf681284c");
        DateTimeOffset timestamp = new(2026, 8, 29, 14, 3, 12, TimeSpan.FromHours(8));
        DateOnly date = new(2026, 8, 29);
        TimeOnly time = new(14, 3, 12, 345);
        TimeSpan duration = TimeSpan.FromMilliseconds(1234);
        const string SpecialText = "quoted \"text\"\\\n\r\t\0雪😀";
        var sink = new FileLogSink(path);
        Start(sink);

        LogRecorder.Error(
            LogCategory.Build,
            exception,
            "Values {Null} {Bool} {Signed} {Unsigned} {Double} {Decimal} {Text} {Guid} {DateTime} {Date} {Time} {Duration} {Uri} {Enum} {NaN} {PositiveInfinity} {NegativeInfinity} {Structure} {Value} {Value}",
            null,
            true,
            -5,
            6UL,
            1.25,
            7.5m,
            SpecialText,
            guid,
            timestamp,
            date,
            time,
            duration,
            new Uri("https://example.com/a?b=1"),
            SampleState.Ready,
            double.NaN,
            double.PositiveInfinity,
            double.NegativeInfinity,
            Param.Structured(new { Name = "root", Items = new[] { 1, 2 } }),
            "first",
            "second");
        LogRecorder.Stop();

        byte[] bytes = File.ReadAllBytes(path);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        string content = File.ReadAllText(path, Encoding.UTF8);
        string normalizedContent = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        string[] physicalLines = normalizedContent.Split('\n');
        Assert.StartsWith("@1 ", physicalLines[0], StringComparison.Ordinal);
        Assert.EndsWith("[Build]", physicalLines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Values", physicalLines[0], StringComparison.Ordinal);
        Assert.StartsWith(">> ", physicalLines[1], StringComparison.Ordinal);
        Assert.Contains(">>", physicalLines);
        Assert.Contains(physicalLines, line => line.StartsWith(":: property Null: ", StringComparison.Ordinal));
        Assert.DoesNotContain(physicalLines, line => line.StartsWith(":: sequence: ", StringComparison.Ordinal));
        Assert.DoesNotContain(physicalLines, line => line.StartsWith(":: elapsed-ns: ", StringComparison.Ordinal));
        Assert.DoesNotContain(physicalLines, line => line.StartsWith(":: thread: ", StringComparison.Ordinal));
        Assert.DoesNotContain(physicalLines, line => line.StartsWith(":: template: ", StringComparison.Ordinal));
        Assert.All(
            physicalLines.Skip(1).Where(line => line.Length != 0),
            line => Assert.True(
                line.StartsWith(">>", StringComparison.Ordinal)
                    || line.StartsWith("::", StringComparison.Ordinal)));
        LogFileRecord synchronous;
        using (var reader = new StringReader(content))
        {
            synchronous = Assert.Single(LogFileReader.Read(reader));
        }

        LogFileRecord asynchronous;
        using (var reader = new StringReader(content))
        {
            var records = new List<LogFileRecord>();
            await foreach (LogFileRecord record in LogFileReader.ReadAsync(
                               reader,
                               TestContext.Current.CancellationToken))
            {
                records.Add(record);
            }

            asynchronous = Assert.Single(records);
        }

        Assert.Equal(LogLevel.Error, synchronous.Level);
        Assert.Equal(LogCategory.Build, synchronous.Category);
        Assert.Equal(Environment.ProcessId, synchronous.ProcessId);
        Assert.Contains("first line\nsecond line", synchronous.ExceptionText, StringComparison.Ordinal);
        Assert.Equal(synchronous.Message, asynchronous.Message);
        Assert.Equal(20, synchronous.Properties.Count);
        Assert.Equal(
            [
                LogValueKind.Null,
                LogValueKind.Boolean,
                LogValueKind.SignedInteger,
                LogValueKind.UnsignedInteger,
                LogValueKind.FloatingPoint,
                LogValueKind.Decimal,
                LogValueKind.String,
                LogValueKind.Guid,
                LogValueKind.DateTime,
                LogValueKind.Date,
                LogValueKind.Time,
                LogValueKind.Duration,
                LogValueKind.Uri,
                LogValueKind.Enum,
                LogValueKind.FloatingPoint,
                LogValueKind.FloatingPoint,
                LogValueKind.FloatingPoint,
                LogValueKind.Structure,
                LogValueKind.String,
                LogValueKind.String,
            ],
            synchronous.Properties.Select(property => property.Value.Kind));
        Assert.Equal(
            ["Value", "Value"],
            synchronous.Properties.Skip(synchronous.Properties.Count - 2).Select(property => property.Name));
        Assert.Equal(guid, synchronous.Properties[7].Value.Value);
        Assert.Equal(SampleState.Ready.ToString(), synchronous.Properties[13].Value.Value);
        Assert.Equal(SpecialText, synchronous.Properties[6].Value.Value);
        Assert.Contains(
            SpecialText.Replace("\r", "\n", StringComparison.Ordinal),
            synchronous.Message,
            StringComparison.Ordinal);
        Assert.True(double.IsNaN((double)synchronous.Properties[14].Value.Value!));
        Assert.Equal(double.PositiveInfinity, synchronous.Properties[15].Value.Value);
        Assert.Equal(double.NegativeInfinity, synchronous.Properties[16].Value.Value);
    }

    [Fact]
    public void FileSinkAndReaderPreserveMultilineMessageStructure()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string path = System.IO.Path.Combine(temporaryDirectory.Path, "multiline.log");
        const string Message = "first\n  second\r\n\rthird\n";
        Start(new FileLogSink(path));

        LogRecorder.Info("{Message}", Message);
        LogRecorder.Stop();

        string content = File.ReadAllText(path, Encoding.UTF8)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        string[] contentLines = content
            .Split('\n')
            .Where(line => line.StartsWith(">>", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal([">> first", ">>   second", ">>", ">> third", ">>"], contentLines);

        LogFileRecord record = Assert.Single(Read(content));
        Assert.Equal("first\n  second\n\nthird\n", record.Message);
        Assert.Equal(Message, Assert.Single(record.Properties).Value.Value);
    }

    [Fact]
    public void ReaderAcceptsBothLineEndingsImplicitBoundariesAndCompleteEofRecords()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string path = System.IO.Path.Combine(temporaryDirectory.Path, "boundaries.log");
        Start(new FileLogSink(path));
        LogRecorder.Info("first");
        LogRecorder.Info("second");
        LogRecorder.Stop();

        string content = File.ReadAllText(path, Encoding.UTF8)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        string withoutFinalBlank = content.TrimEnd('\n');
        string implicitHeaderBoundary = content.Replace("\n\n@1 ", "\n@1 ", StringComparison.Ordinal);
        string crlf = content.Replace("\n", "\r\n", StringComparison.Ordinal);

        Assert.Equal(2, Read(withoutFinalBlank).Count);
        Assert.Equal(2, Read(implicitHeaderBoundary).Count);
        Assert.Equal(2, Read(crlf).Count);
    }

    [Fact]
    public void ErrorEventsAreFlushedWithoutAnExplicitBarrier()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string path = System.IO.Path.Combine(temporaryDirectory.Path, "error.log");
        Start(new FileLogSink(path));

        LogRecorder.Error("immediate error");

        bool wasFlushed = SpinWait.SpinUntil(
            () =>
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        return false;
                    }

                    using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    return reader.ReadToEnd().Contains("immediate error", StringComparison.Ordinal);
                }
                catch (IOException)
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(5));
        LogRecorder.Stop();

        Assert.True(wasFlushed);
    }

    [Theory]
    [MemberData(nameof(InvalidRecords))]
    public void ReaderRejectsMalformedRecordsWithALineNumber(string content)
    {
        using var reader = new StringReader(content);

        FormatException exception = Assert.Throws<FormatException>(
            () => LogFileReader.Read(reader).ToArray());

        Assert.Contains("Line ", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReaderDoesNotDisposeTheCallerOwnedReader()
    {
        using var reader = new TrackingStringReader(ValidRecord);

        Assert.Single(LogFileReader.Read(reader));

        Assert.False(reader.WasDisposed);
    }

    public static TheoryData<string> InvalidRecords => new()
    {
        ValidRecord.Replace("@1 ", "@2 ", StringComparison.Ordinal),
        "@1 invalid",
        ValidRecord.Replace(":: property Value: 1", "  property Value: 1", StringComparison.Ordinal),
        ValidRecord.Replace(
            ":: property Value: 1",
            ":: property Value: [1,]",
            StringComparison.Ordinal),
        ValidRecord.Replace(":: property Value: 1", ":: unknown: value", StringComparison.Ordinal),
        ValidRecord.Replace(":: property Value: 1", ":: sequence: 1", StringComparison.Ordinal),
        ValidRecord.Replace(":: property Value: 1", ":: elapsed-ns: 0", StringComparison.Ordinal),
        ValidRecord.Replace(":: property Value: 1", ":: thread: \"worker\"", StringComparison.Ordinal),
        ValidRecord.Replace(":: property Value: 1", ":: template: \"message\"", StringComparison.Ordinal),
        ValidRecord.Replace("[Build]\n", "[Build] message\n", StringComparison.Ordinal),
        ValidRecord.Replace(">> message\n", string.Empty, StringComparison.Ordinal),
        ValidRecord.Replace(">> message", ">>message", StringComparison.Ordinal),
        ValidRecord.Replace(">> message", ">> ", StringComparison.Ordinal),
        ValidRecord.Replace(">> message", ">> bad\\x", StringComparison.Ordinal),
        ValidRecord.Replace(":: property Value: 1", "::property Value: 1", StringComparison.Ordinal),
        ValidRecord.Replace(":: property Value: 1\n", ":: property Value: 1\n>> late\n", StringComparison.Ordinal),
        ValidRecord.Replace(
            ">> message\n:: property Value: 1",
            ":: property Value: 1\n>> message",
            StringComparison.Ordinal),
        ValidRecord.Replace(
            ":: property Value: 1",
            ":: exception: \"first\"\n:: exception: \"second\"",
            StringComparison.Ordinal),
    };

    private const string ValidRecord =
        "@1 2026-08-29T06:03:12.1234567Z [P1:T2] [INF] [Build]\n"
        + ">> message\n"
        + ":: property Value: 1\n";

    private static IReadOnlyList<LogFileRecord> Read(string content)
    {
        using var reader = new StringReader(content);
        return LogFileReader.Read(reader).ToArray();
    }

    private static void Start(ILogSink sink)
    {
        LogRecorder.MinimumLevel = LogLevel.Trace;
        LogRecorder.AddSink(sink);
        LogRecorder.Start(
            new LogOptions
            {
                FlushInterval = TimeSpan.FromSeconds(30),
            });
    }

    private enum SampleState
    {
        Ready,
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Incant.UnitTest.Base",
                Guid.NewGuid().ToString("N"));
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }

    private sealed class TrackingStringReader(string value) : StringReader(value)
    {
        internal bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
