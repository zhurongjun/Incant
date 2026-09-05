using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Incant.Base.Deps;

/// <summary>Owns the CSV file format, framing, checksums, and low-level file operations.</summary>
/// <remarks>Database retains ownership of its index, open writer, process lock, and synchronization.</remarks>
internal static class DatabaseFileUtils
{
    internal static FileStream OpenWriter(string path, FileMode mode) =>
        new(path, mode, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, BufferSize);

    internal static FileStream OpenReadOnly(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            BufferSize, FileOptions.SequentialScan);

    internal static void AppendRecord(FileStream stream, Record record)
    {
        WriteRecord(stream, record);
        stream.Flush(flushToDisk: false);
    }

    internal static void WriteCompactedFile(string path, IEnumerable<Record> records)
    {
        using var compact = new FileStream(path + ".compact", FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);
        var header = new FileHeader(1, "csv", 0, 0, 0, 0);
        string prefix = FormatFileHeaderFields(header);
        header = header with { Md5 = MD5.HashData(Encoding.ASCII.GetBytes(prefix)) };
        compact.Write(EncodeHeader(prefix, header.Md5.Span));
        foreach (Record record in records)
        {
            WriteRecord(compact, record);
        }

        compact.Flush(flushToDisk: true);
    }

    internal static void ReplaceWithCompactedFile(string path)
    {
        // Same-directory replacement preserves either the old or the completed new file.
        // Never fall back to copying over, or deleting, the current database.
        File.Replace(path + ".compact", path, destinationBackupFileName: null);
    }

    /// <summary>Loads valid records and reports the length to retain when trimming an invalid tail.</summary>
    /// <returns>False when the header is invalid or damage precedes a valid record and compaction is required.</returns>
    internal static bool ReadRecords(
        FileStream stream, Dictionary<string, Record> records,
        out long totalRecordCount, out long validLength)
    {
        totalRecordCount = 0;
        validLength = 0;
        var reader = new RowReader(stream, stream.Length);
        if (!reader.TryRead(out ReadOnlyMemory<byte> row) || !TryParseFileHeader(row.Span, out _))
        {
            return false;
        }

        validLength = reader.Position;
        bool hasInvalidData = false;
        bool needsCompaction = false;
        ReadOnlyMemory<byte> pending = default;
        while (!pending.IsEmpty || reader.TryRead(out row))
        {
            if (!pending.IsEmpty)
            {
                row = pending;
                pending = default;
            }

            // Blank separators belong to neither adjacent record.
            if (row.Span.SequenceEqual("\n"u8))
            {
                continue;
            }

            if (!TryParseRecordHeader(row.Span, out RecordHeader header)
                || !TryReadRecord(reader, header, out Record record, out pending))
            {
                hasInvalidData = true;
                continue;
            }

            records[record.Key] = record;
            totalRecordCount++;
            validLength = reader.Position;
            needsCompaction |= hasInvalidData;
        }

        if (!hasInvalidData)
        {
            // Preserve harmless trailing separators in an otherwise healthy file.
            validLength = reader.Position;
        }

        return !needsCompaction;
    }

    private static bool TryReadRecord(
        RowReader reader, RecordHeader header, out Record record, out ReadOnlyMemory<byte> pending)
    {
        record = default;
        pending = default;
        var content = new ArrayBufferWriter<byte>();
        for (int index = 0; index < header.RowCount; index++)
        {
            if (!reader.TryRead(out ReadOnlyMemory<byte> row))
            {
                return false;
            }

            if (!row.IsEmpty && row.Span[0] == '@')
            {
                // The caller must parse this header before reading again and reusing the row buffer.
                pending = row;
                return false;
            }

            if (row.Length <= 1 || row.Span[^1] != '\n' || row.Span.Contains((byte)'\r'))
            {
                return false;
            }

            content.Write(row.Span);
        }

        if (!MD5.HashData(content.WrittenSpan).AsSpan().SequenceEqual(header.ContentMd5.Span))
        {
            return false;
        }

        try
        {
            record = DecodeRecord(content.WrittenSpan);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            // Invalid CSV, UTF-8, keys, and timestamps invalidate only this record.
            return false;
        }
    }

    private static void ValidateRecord(Record record)
    {
        if (!string.Equals(record.Key, RecordUtils.CanonicalizeKey(record.Key), StringComparison.Ordinal))
        {
            throw new FormatException("A persisted key must be canonical.");
        }

        string? previousPath = null;
        foreach (FileSnapshot file in record.Files)
        {
            ValidateFile(file, record.UseSHA);
            if (previousPath is not null && StringComparer.Ordinal.Compare(previousPath, file.Path) >= 0)
            {
                throw new FormatException("Input file paths must be sorted and unique.");
            }

            previousPath = file.Path;
        }

        foreach (FileSnapshot file in record.ExternalFiles)
        {
            ValidateFile(file, record.UseSHA);
        }
    }

    private static void WriteRecord(Stream stream, Record record)
    {
        // Finish formatting before touching the file.
        byte[] content = EncodeRecord(record, out int rowCount);
        var header = new RecordHeader(rowCount, MD5.HashData(content));
        string prefix = FormatRecordHeaderFields(header);
        header = header with { Md5 = MD5.HashData(Encoding.ASCII.GetBytes(prefix)) };
        byte[] encodedHeader = EncodeHeader(prefix, header.Md5.Span);
        stream.WriteByte((byte)'\n');
        stream.Write(encodedHeader);
        stream.Write(content);
    }

    private static byte[] EncodeRecord(Record record, out int rowCount)
    {
        var builder = new StringBuilder();
        AppendContentRow(builder, new ContentRow(RowKind.Key, Text: record.Key));
        AppendContentRow(builder, new ContentRow(RowKind.Mode, UseSHA: record.UseSHA));
        foreach (string argument in record.Args)
        {
            AppendContentRow(builder, new ContentRow(RowKind.Argument, Text: argument));
        }

        foreach (FileSnapshot file in record.Files)
        {
            AppendContentRow(builder, new ContentRow(RowKind.InputFile, File: file));
        }

        foreach (FileSnapshot file in record.ExternalFiles)
        {
            AppendContentRow(builder, new ContentRow(RowKind.ExternalFile, File: file));
        }

        rowCount = 2 + record.Args.Count + record.Files.Count + record.ExternalFiles.Count;
        return s_utf8.GetBytes(builder.ToString());
    }

    private static Record DecodeRecord(ReadOnlySpan<byte> content)
    {
        string text = s_utf8.GetString(content);
        string? key = null;
        bool? useSHA = null;
        var args = new List<string>();
        var files = new List<FileSnapshot>();
        var externalFiles = new List<FileSnapshot>();
        foreach (string line in text[..^1].Split('\n'))
        {
            ContentRow row = ParseContentRow(line);
            switch (row.Kind)
            {
                case RowKind.Key when key is null:
                    key = row.Text;
                    break;
                case RowKind.Mode when useSHA is null:
                    useSHA = row.UseSHA;
                    break;
                case RowKind.Argument:
                    args.Add(row.Text);
                    break;
                case RowKind.InputFile:
                    files.Add(row.File);
                    break;
                case RowKind.ExternalFile:
                    externalFiles.Add(row.File);
                    break;
                default:
                    throw new FormatException("The record contains duplicate metadata.");
            }
        }

        if (key is null || useSHA is null)
        {
            throw new FormatException("The record is missing its key or validation mode.");
        }

        var record = new Record
        {
            Key = key,
            UseSHA = useSHA.Value,
            Args = args,
            Files = files,
            ExternalFiles = externalFiles
        };
        ValidateRecord(record);
        return record;
    }

    private static void AppendContentRow(StringBuilder builder, ContentRow row)
    {
        switch (row.Kind)
        {
            case RowKind.Key:
                AppendCsvFields(builder, "key", row.Text);
                break;
            case RowKind.Mode:
                AppendCsvFields(builder, "mode", row.UseSHA ? "sha256" : "timestamp");
                break;
            case RowKind.Argument:
                AppendCsvFields(builder, "input_arg", row.Text);
                break;
            case RowKind.InputFile:
                AppendFile(builder, "input_file", row.File);
                break;
            case RowKind.ExternalFile:
                AppendFile(builder, "external_file", row.File);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(row));
        }
    }

    private static ContentRow ParseContentRow(string line)
    {
        List<string> fields = ParseCsvFields(line);
        return fields[0] switch
        {
            "key" when fields.Count == 2 => new ContentRow(RowKind.Key, Text: fields[1]),
            "mode" when fields.Count == 2 => new ContentRow(RowKind.Mode, UseSHA: fields[1] switch
            {
                "sha256" => true,
                "timestamp" => false,
                _ => throw new FormatException("The validation mode is invalid.")
            }),
            "input_arg" when fields.Count == 2 => new ContentRow(RowKind.Argument, Text: fields[1]),
            "input_file" when fields.Count == 4 => new ContentRow(RowKind.InputFile, File: ParseFile(fields)),
            "external_file" when fields.Count == 4 => new ContentRow(RowKind.ExternalFile, File: ParseFile(fields)),
            _ => throw new FormatException("The CSV row type or field count is invalid.")
        };
    }

    private static string FormatFileHeaderFields(FileHeader header) =>
        string.Create(CultureInfo.InvariantCulture,
            $"@incant-deps,{header.Version},{header.Format},{header.Reserved0},{header.Reserved1},{header.Reserved2},{header.Reserved3}");

    private static string FormatRecordHeaderFields(RecordHeader header) =>
        string.Create(CultureInfo.InvariantCulture, $"@record,{header.RowCount},{Convert.ToHexString(header.ContentMd5.Span)}");

    private static byte[] EncodeHeader(string prefix, ReadOnlySpan<byte> md5) =>
        Encoding.ASCII.GetBytes(prefix + "," + Convert.ToHexString(md5) + "\n");

    private static bool TryParseFileHeader(ReadOnlySpan<byte> row, out FileHeader header)
    {
        header = default;
        if (!TryParseHeader(row, out string[] fields, out ReadOnlyMemory<byte> md5)
            || fields is not ["@incant-deps", "1", "csv", "0", "0", "0", "0"])
        {
            return false;
        }

        header = new FileHeader(1, "csv", 0, 0, 0, 0, md5);
        return true;
    }

    private static bool TryParseRecordHeader(ReadOnlySpan<byte> row, out RecordHeader header)
    {
        header = default;
        if (!TryParseHeader(row, out string[] fields, out ReadOnlyMemory<byte> md5)
            || fields.Length != 3 || fields[0] != "@record"
            || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int rowCount)
            || rowCount <= 0 || !TryParseMd5(fields[2], out byte[] contentMd5))
        {
            return false;
        }

        header = new RecordHeader(rowCount, contentMd5, md5);
        return true;
    }

    private static bool TryParseHeader(ReadOnlySpan<byte> row, out string[] fields, out ReadOnlyMemory<byte> md5)
    {
        fields = [];
        md5 = default;
        if (row.IsEmpty || row[^1] != '\n' || row[0] != '@')
        {
            return false;
        }

        row = row[..^1];
        int separator = row.LastIndexOf((byte)',');
        if (separator < 0 || row.Length - separator - 1 != 32)
        {
            return false;
        }

        foreach (byte item in row)
        {
            if (item is < 0x20 or > 0x7E)
            {
                return false;
            }
        }

        if (!TryParseMd5(Encoding.ASCII.GetString(row[(separator + 1)..]), out byte[] digest)
            || !MD5.HashData(row[..separator]).AsSpan().SequenceEqual(digest))
        {
            return false;
        }

        md5 = digest;
        fields = Encoding.ASCII.GetString(row[..separator]).Split(',');
        return true;
    }

    private static bool TryParseMd5(string text, out byte[] digest)
    {
        digest = [];
        if (text.Length != 32)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[16];
        if (Convert.FromHexString(text, bytes, out _, out _) != OperationStatus.Done)
        {
            return false;
        }

        digest = bytes.ToArray();
        return true;
    }

    private static void ValidateFile(FileSnapshot file, bool useSHA)
    {
        if (string.IsNullOrEmpty(file.Path)
            || (!useSHA && file.Sha256 is not null)
            || (file.LastWriteTimeUtc != DateTime.MinValue && file.LastWriteTimeUtc.Kind != DateTimeKind.Utc))
        {
            throw new FormatException("The record contains an invalid file snapshot.");
        }
    }

    private static void AppendFile(StringBuilder builder, string kind, FileSnapshot file) =>
        AppendCsvFields(builder, kind, file.Path,
            file.LastWriteTimeUtc.ToBinary().ToString(CultureInfo.InvariantCulture),
            file.Sha256?.ToString() ?? string.Empty);

    private static FileSnapshot ParseFile(List<string> fields)
    {
        if (!long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long timestamp))
        {
            throw new FormatException("The file timestamp is invalid.");
        }

        return new FileSnapshot(fields[1], DateTime.FromBinary(timestamp),
            fields[3].Length == 0 ? null : Sha256Digest.Parse(fields[3]));
    }

    private static void AppendCsvFields(StringBuilder builder, params string[] fields)
    {
        for (int index = 0; index < fields.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            string value = Escape(fields[index]);
            if (value.Contains(',') || value.Contains('"'))
            {
                builder.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            }
            else
            {
                builder.Append(value);
            }
        }

        builder.Append('\n');
    }

    private static List<string> ParseCsvFields(string line)
    {
        var fields = new List<string>();
        int offset = 0;
        while (true)
        {
            var field = new StringBuilder();
            if (offset < line.Length && line[offset] == '"')
            {
                offset++;
                bool closed = false;
                while (offset < line.Length)
                {
                    char character = line[offset++];
                    if (character != '"')
                    {
                        field.Append(character);
                    }
                    else if (offset < line.Length && line[offset] == '"')
                    {
                        field.Append('"');
                        offset++;
                    }
                    else
                    {
                        closed = true;
                        break;
                    }
                }

                if (!closed || (offset < line.Length && line[offset] != ','))
                {
                    throw new FormatException("The quoted CSV field is invalid.");
                }
            }
            else
            {
                while (offset < line.Length && line[offset] != ',')
                {
                    char character = line[offset++];
                    if (character == '"')
                    {
                        throw new FormatException("A CSV quote must begin a field.");
                    }

                    field.Append(character);
                }
            }

            fields.Add(Unescape(field.ToString()));
            if (offset == line.Length)
            {
                return fields;
            }

            offset++;
        }
    }

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            switch (character)
            {
                case '\\':
                    builder.Append(@"\\");
                    break;
                case '\r':
                    builder.Append(@"\r");
                    break;
                case '\n':
                    builder.Append(@"\n");
                    break;
                case '\t':
                    builder.Append(@"\t");
                    break;
                case '\0':
                    builder.Append(@"\0");
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        builder.Append(@"\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    private static string Unescape(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (character != '\\')
            {
                builder.Append(character);
                continue;
            }

            if (++index >= value.Length)
            {
                throw new FormatException("A CSV escape is incomplete.");
            }

            switch (value[index])
            {
                case '\\':
                    builder.Append('\\');
                    break;
                case 'r':
                    builder.Append('\r');
                    break;
                case 'n':
                    builder.Append('\n');
                    break;
                case 't':
                    builder.Append('\t');
                    break;
                case '0':
                    builder.Append('\0');
                    break;
                case 'u':
                    if (index + 4 >= value.Length
                        || !ushort.TryParse(value.AsSpan(index + 1, 4),
                            NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort codePoint))
                    {
                        throw new FormatException("A CSV Unicode escape is invalid.");
                    }

                    builder.Append((char)codePoint);
                    index += 4;
                    break;
                default:
                    throw new FormatException("The CSV escape is unsupported.");
            }
        }

        return builder.ToString();
    }

    /// <summary>The checksum-verified metadata carried by the first physical line.</summary>
    private readonly record struct FileHeader(
        int Version,
        string Format,
        int Reserved0,
        int Reserved1,
        int Reserved2,
        int Reserved3,
        ReadOnlyMemory<byte> Md5 = default);

    /// <summary>The frame size and checksums carried by a record's header line.</summary>
    private readonly record struct RecordHeader(
        int RowCount,
        ReadOnlyMemory<byte> ContentMd5,
        ReadOnlyMemory<byte> Md5 = default);

    /// <summary>A logical CSV row; Kind selects text, validation mode, or a file snapshot.</summary>
    private readonly record struct ContentRow(
        RowKind Kind,
        string Text = "",
        bool UseSHA = false,
        FileSnapshot File = default);

    private enum RowKind
    {
        Key,
        Mode,
        Argument,
        InputFile,
        ExternalFile
    }

    private sealed class RowReader
    {
        internal RowReader(Stream stream, long length)
        {
            _stream = stream;
            _remaining = length;
        }

        internal long Position => _stream.Position - (_count - _offset);

        internal bool TryRead(out ReadOnlyMemory<byte> row)
        {
            ArrayBufferWriter<byte>? overflow = null;
            while (true)
            {
                if (_offset == _count)
                {
                    _count = _remaining == 0 ? 0 : _stream.Read(_buffer, 0, (int)Math.Min(_buffer.Length, _remaining));
                    _offset = 0;
                    _remaining -= _count;
                    if (_count == 0)
                    {
                        row = overflow?.WrittenMemory ?? ReadOnlyMemory<byte>.Empty;
                        if (row.IsEmpty)
                        {
                            return false;
                        }

                        return true;
                    }
                }

                ReadOnlySpan<byte> unread = _buffer.AsSpan(_offset, _count - _offset);
                int end = unread.IndexOf((byte)'\n');
                if (end >= 0)
                {
                    int length = end + 1;
                    if (overflow is null)
                    {
                        row = _buffer.AsMemory(_offset, length);
                    }
                    else
                    {
                        overflow.Write(unread[..length]);
                        row = overflow.WrittenMemory;
                    }

                    _offset += length;
                    return true;
                }

                // Most rows borrow the read buffer; allocate only for rows spanning buffer boundaries.
                overflow ??= new ArrayBufferWriter<byte>();
                overflow.Write(unread);
                _offset = _count;
            }
        }

        private readonly Stream _stream;

        private readonly byte[] _buffer = new byte[BufferSize];

        private long _remaining;

        private int _offset;

        private int _count;
    }

    private const int BufferSize = 64 * 1024;

    private static readonly UTF8Encoding s_utf8 = new(false, true);
}
