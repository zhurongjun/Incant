using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace SB.Core
{
    public struct DependencyCheckOptions
    {
        public bool UseSHA { get; init; }
        public bool Force { get; init; }
    }

    public sealed class DependencyDatabaseCache
    {
        internal readonly ConcurrentDictionary<string, bool> FileExists = new();
        internal readonly ConcurrentDictionary<string, DateTime> FileDateTimes = new();
        internal readonly ConcurrentDictionary<string, string> FileSHAs = new();

        public void Clear()
        {
            FileExists.Clear();
            FileDateTimes.Clear();
            FileSHAs.Clear();
        }
    }

    public class DependencyDatabase
    {
        private const string DatabaseDirectorySuffix = ".db";
        private const string RecordFileExtension = ".csv";
        private const string RecordLockExtension = ".lock";
        private const string CsvFormatVersion = "1";
        private const int LockAcquireTimeoutMs = 5000;
        private const int LockAcquireRetryDelayMs = 10;

        public DependencyDatabase(string location, string name, DependencyDatabaseCache? cache = null, bool defaultUseSHA = false)
        {
            _name = name;
            _cache = cache;
            _defaultUseSHA = defaultUseSHA;
            _databaseDirectoryPath = Path.Join(location, name + DatabaseDirectorySuffix);
            EnsureStorageDirectory();
        }

        public bool RunIfOutdated(string targetName, string fileName, string emitterName, Action<DependencyRecord> func, IEnumerable<string>? files, IEnumerable<string>? args, DependencyCheckOptions? opt = null)
        {
            var option = opt ?? new DependencyCheckOptions { Force = false, UseSHA = _defaultUseSHA };
            var sortedFiles = files?.ToList() ?? new();
            var sortedArgs = args?.ToList() ?? new();
            sortedFiles.Sort();
            sortedArgs.Sort();

            var checkContext = new CheckContext
            {
                PrimaryKey = CreatePrimaryKey(targetName, fileName, emitterName),
                TargetName = targetName,
                FileName = fileName,
                EmitterName = emitterName,
                SortedFiles = sortedFiles,
                SortedArgs = sortedArgs,
                Options = option
            };

            var shouldRerun = !CheckDependency(ref checkContext, out var oldDependency);
            if (!shouldRerun && !option.Force)
                return false;

            var newDependency = new DependencyRecord
            {
                PrimaryKey = checkContext.PrimaryKey,
                InputArgs = sortedArgs,
                InputFiles = sortedFiles,
                InputFileTimes = sortedFiles.Select(path => GetFileLastWriteTime(CacheMode.NoCache, path, option)).ToList(),
                InputFileSHAs = sortedFiles.Select(path => GetFileSHA(CacheMode.NoCache, path, option)).ToList()
            };

            func(newDependency);
            UpdateDependency(newDependency, option);
            return true;
        }

        public async Task<bool> RunIfOutdated(string targetName, string fileName, string emitterName, Func<DependencyRecord, Task> func, IEnumerable<string>? files, IEnumerable<string>? args, DependencyCheckOptions? opt = null)
        {
            var option = opt ?? new DependencyCheckOptions { Force = false, UseSHA = _defaultUseSHA };
            var sortedFiles = files?.ToList() ?? new();
            var sortedArgs = args?.ToList() ?? new();
            sortedFiles.Sort();
            sortedArgs.Sort();

            var checkContext = new CheckContext
            {
                PrimaryKey = CreatePrimaryKey(targetName, fileName, emitterName),
                TargetName = targetName,
                FileName = fileName,
                EmitterName = emitterName,
                SortedFiles = sortedFiles,
                SortedArgs = sortedArgs,
                Options = option
            };

            var shouldRerun = !CheckDependency(ref checkContext, out var oldDependency);
            if (!shouldRerun && !option.Force)
                return false;

            var newDependency = new DependencyRecord
            {
                PrimaryKey = checkContext.PrimaryKey,
                InputArgs = sortedArgs,
                InputFiles = sortedFiles,
                InputFileTimes = sortedFiles.Select(path => GetFileLastWriteTime(CacheMode.NoCache, path, option)).ToList(),
                InputFileSHAs = sortedFiles.Select(path => GetFileSHA(CacheMode.NoCache, path, option)).ToList()
            };

            await func(newDependency);
            UpdateDependency(newDependency, option);
            return true;
        }

        public void ClearDatabase()
        {
            if (Directory.Exists(_databaseDirectoryPath))
                Directory.Delete(_databaseDirectoryPath, recursive: true);

            Directory.CreateDirectory(_databaseDirectoryPath);
            _cache?.Clear();

            Log.Information("Cleared dependency database: {Name}", _name);
        }

        private struct CheckContext
        {
            public required string PrimaryKey;
            public required string TargetName;
            public required string FileName;
            public required string EmitterName;
            public required List<string> SortedFiles;
            public required List<string> SortedArgs;
            public required DependencyCheckOptions Options;
        }

        private bool CheckDependency(ref CheckContext ctx, out DependencyRecord? oldDependency)
        {
            oldDependency = TryLoadDependencyRecord(ctx.PrimaryKey);
            if (oldDependency is null)
            {
                Log.Verbose("Dependency not found for {TargetName} {FileName} {EmitterName}: No previous record", ctx.TargetName, ctx.FileName, ctx.EmitterName);
                return false;
            }

            if (!ctx.SortedFiles.SequenceEqual(oldDependency.Value.InputFiles))
            {
                Log.Verbose("Dependency changed for {TargetName} {FileName} {EmitterName}: File list changed", ctx.TargetName, ctx.FileName, ctx.EmitterName);
                return false;
            }

            if (!ctx.SortedArgs.SequenceEqual(oldDependency.Value.InputArgs))
            {
                Log.Verbose("Dependency changed for {TargetName} {FileName} {EmitterName}: Arg list changed", ctx.TargetName, ctx.FileName, ctx.EmitterName);
                return false;
            }

            for (int i = 0; i < oldDependency.Value.InputFiles.Count; i++)
            {
                var inputFile = oldDependency.Value.InputFiles[i];

                if (!CheckFileExist(CacheMode.Cache, inputFile))
                {
                    Log.Verbose("Dependency changed for {TargetName} {FileName} {EmitterName}: Input file {InputFile} deleted", ctx.TargetName, ctx.FileName, ctx.EmitterName, inputFile);
                    return false;
                }

                if (ctx.Options.UseSHA)
                {
                    string shaString = GetFileSHA(CacheMode.Cache, inputFile, ctx.Options);
                    if (oldDependency.Value.InputFileSHAs[i] != shaString)
                    {
                        Log.Verbose("Dependency changed for {TargetName} {FileName} {EmitterName}: Input file {InputFile} modified", ctx.TargetName, ctx.FileName, ctx.EmitterName, inputFile);
                        return false;
                    }
                }
                else
                {
                    DateTime lastWriteTime = GetFileLastWriteTime(CacheMode.Cache, inputFile, ctx.Options);
                    if (oldDependency.Value.InputFileTimes[i] != lastWriteTime)
                    {
                        Log.Verbose("Dependency changed for {TargetName} {FileName} {EmitterName}: Input file {InputFile} modified", ctx.TargetName, ctx.FileName, ctx.EmitterName, inputFile);
                        return false;
                    }
                }
            }

            for (int i = 0; i < oldDependency.Value.ExternalFiles.Count; i++)
            {
                var externalFile = oldDependency.Value.ExternalFiles[i];

                if (!CheckFileExist(CacheMode.Cache, externalFile))
                {
                    Log.Verbose("Dependency changed for {TargetName} {FileName} {EmitterName}: External file {ExternalFile} deleted", ctx.TargetName, ctx.FileName, ctx.EmitterName, externalFile);
                    return false;
                }

                if (ctx.Options.UseSHA)
                {
                    string shaString = GetFileSHA(CacheMode.Cache, externalFile, ctx.Options);
                    if (oldDependency.Value.ExternalFileSHAs[i] != shaString)
                    {
                        Log.Verbose("Dependency changed for {TargetName} {FileName} {EmitterName}: Output file {OutputFile} modified", ctx.TargetName, ctx.FileName, ctx.EmitterName, externalFile);
                        return false;
                    }
                }
                else
                {
                    DateTime lastWriteTime = GetFileLastWriteTime(CacheMode.Cache, externalFile, ctx.Options);
                    if (lastWriteTime == DateTime.MinValue)
                    {
                        Log.Verbose("Dependency changed for {TargetName} {FileName} {EmitterName}: Output file {OutputFile} deleted", ctx.TargetName, ctx.FileName, ctx.EmitterName, externalFile);
                        return false;
                    }

                    if (oldDependency.Value.ExternalFileTimes[i] != lastWriteTime)
                    {
                        Log.Verbose("Dependency changed for {TargetName} {FileName} {EmitterName}: Output file {OutputFile} modified", ctx.TargetName, ctx.FileName, ctx.EmitterName, externalFile);
                        return false;
                    }
                }
            }

            return true;
        }

        private void UpdateDependency(DependencyRecord newDependency, DependencyCheckOptions opt)
        {
            newDependency.ExternalFileTimes = newDependency.ExternalFiles.Select(path => GetFileLastWriteTime(CacheMode.NoCache, path, opt)).ToList();
            newDependency.ExternalFileSHAs = newDependency.ExternalFiles.Select(path => GetFileSHA(CacheMode.NoCache, path, opt)).ToList();

            using (Profiler.BeginZone("WriteToDB", color: (uint)Profiler.ColorType.Gray))
            {
                SaveDependencyRecord(newDependency);
            }
        }

        private DependencyRecord? TryLoadDependencyRecord(string primaryKey)
        {
            var recordPath = GetRecordPath(primaryKey);
            if (!File.Exists(recordPath))
                return null;

            using var recordLock = AcquireRecordLock(primaryKey);
            if (!File.Exists(recordPath))
                return null;

            try
            {
                return DeserializeRecordCsv(File.ReadAllLines(recordPath), primaryKey);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to read dependency record {RecordPath}, treating it as stale cache", recordPath);
                TryDeleteFile(recordPath);
                return null;
            }
        }

        private void SaveDependencyRecord(DependencyRecord dependencyRecord)
        {
            var recordPath = GetRecordPath(dependencyRecord.PrimaryKey);
            var recordDirectory = Path.GetDirectoryName(recordPath)!;
            var tempPath = Path.Combine(recordDirectory, $"{dependencyRecord.PrimaryKey}.{Guid.NewGuid():N}.tmp");
            var csvContent = SerializeRecordCsv(dependencyRecord);

            Directory.CreateDirectory(recordDirectory);
            WriteAllTextAtomic(tempPath, recordPath, csvContent, dependencyRecord.PrimaryKey);
        }

        private void WriteAllTextAtomic(string tempPath, string recordPath, string content, string primaryKey)
        {
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(content);
                    writer.Flush();
                    stream.Flush(true);
                }

                using var recordLock = AcquireRecordLock(primaryKey);
                File.Move(tempPath, recordPath, overwrite: true);
            }
            finally
            {
                TryDeleteFile(tempPath);
            }
        }

        private string SerializeRecordCsv(DependencyRecord dependencyRecord)
        {
            var builder = new StringBuilder();

            AppendCsvRow(builder, "version", CsvFormatVersion);
            AppendCsvRow(builder, "primary_key", dependencyRecord.PrimaryKey);

            foreach (var inputArg in dependencyRecord.InputArgs)
                AppendCsvRow(builder, "input_arg", inputArg);

            for (int i = 0; i < dependencyRecord.InputFiles.Count; i++)
            {
                AppendCsvRow(
                    builder,
                    "input_file",
                    dependencyRecord.InputFiles[i],
                    SerializeDateTime(GetListValueOrDefault(dependencyRecord.InputFileTimes, i, DateTime.MinValue)),
                    GetListValueOrDefault(dependencyRecord.InputFileSHAs, i, String.Empty)
                );
            }

            for (int i = 0; i < dependencyRecord.ExternalFiles.Count; i++)
            {
                AppendCsvRow(
                    builder,
                    "external_file",
                    dependencyRecord.ExternalFiles[i],
                    SerializeDateTime(GetListValueOrDefault(dependencyRecord.ExternalFileTimes, i, DateTime.MinValue)),
                    GetListValueOrDefault(dependencyRecord.ExternalFileSHAs, i, String.Empty)
                );
            }

            return builder.ToString();
        }

        private static DependencyRecord DeserializeRecordCsv(IEnumerable<string> lines, string expectedPrimaryKey)
        {
            string? primaryKey = null;
            var inputArgs = new List<string>();
            var inputFiles = new List<string>();
            var inputFileTimes = new List<DateTime>();
            var inputFileShas = new List<string>();
            var externalFiles = new List<string>();
            var externalFileTimes = new List<DateTime>();
            var externalFileShas = new List<string>();

            foreach (var line in lines)
            {
                if (String.IsNullOrWhiteSpace(line))
                    continue;

                var fields = ParseCsvLine(line);
                if (fields.Count == 0)
                    continue;

                switch (fields[0])
                {
                    case "version":
                        if (fields.Count != 2 || fields[1] != CsvFormatVersion)
                            throw new FormatException($"Unsupported dependency record version: {String.Join(",", fields)}");
                        break;

                    case "primary_key":
                        if (fields.Count != 2)
                            throw new FormatException("Malformed primary_key record.");
                        primaryKey = fields[1];
                        break;

                    case "input_arg":
                        if (fields.Count != 2)
                            throw new FormatException("Malformed input_arg record.");
                        inputArgs.Add(fields[1]);
                        break;

                    case "input_file":
                        if (fields.Count != 4)
                            throw new FormatException("Malformed input_file record.");
                        inputFiles.Add(fields[1]);
                        inputFileTimes.Add(DeserializeDateTime(fields[2]));
                        inputFileShas.Add(fields[3]);
                        break;

                    case "external_file":
                        if (fields.Count != 4)
                            throw new FormatException("Malformed external_file record.");
                        externalFiles.Add(fields[1]);
                        externalFileTimes.Add(DeserializeDateTime(fields[2]));
                        externalFileShas.Add(fields[3]);
                        break;
                }
            }

            if (String.IsNullOrEmpty(primaryKey))
                throw new FormatException("Dependency record missing primary key.");

            if (primaryKey != expectedPrimaryKey)
                throw new FormatException("Dependency record primary key mismatch.");

            return new DependencyRecord
            {
                PrimaryKey = primaryKey,
                InputArgs = inputArgs,
                InputFiles = inputFiles,
                InputFileTimes = inputFileTimes,
                InputFileSHAs = inputFileShas,
                ExternalFiles = externalFiles,
                ExternalFileTimes = externalFileTimes,
                ExternalFileSHAs = externalFileShas
            };
        }

        private static void AppendCsvRow(StringBuilder builder, params string[] fields)
        {
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0)
                    builder.Append(',');
                AppendCsvField(builder, fields[i]);
            }
            builder.AppendLine();
        }

        private static void AppendCsvField(StringBuilder builder, string value)
        {
            bool needsQuoting = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
            if (!needsQuoting)
            {
                builder.Append(value);
                return;
            }

            builder.Append('"');
            builder.Append(value.Replace("\"", "\"\""));
            builder.Append('"');
        }

        private static List<string> ParseCsvLine(string line)
        {
            var fields = new List<string>();
            var fieldBuilder = new StringBuilder();
            bool inQuotedField = false;

            for (int i = 0; i < line.Length; i++)
            {
                char current = line[i];

                if (inQuotedField)
                {
                    if (current == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            fieldBuilder.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotedField = false;
                        }
                    }
                    else
                    {
                        fieldBuilder.Append(current);
                    }
                    continue;
                }

                if (current == ',')
                {
                    fields.Add(fieldBuilder.ToString());
                    fieldBuilder.Clear();
                }
                else if (current == '"')
                {
                    inQuotedField = true;
                }
                else
                {
                    fieldBuilder.Append(current);
                }
            }

            if (inQuotedField)
                throw new FormatException("Unterminated CSV quoted field.");

            fields.Add(fieldBuilder.ToString());
            return fields;
        }

        private static string SerializeDateTime(DateTime value) =>
            value.ToBinary().ToString(CultureInfo.InvariantCulture);

        private static DateTime DeserializeDateTime(string value) =>
            String.IsNullOrEmpty(value)
                ? DateTime.MinValue
                : DateTime.FromBinary(Int64.Parse(value, CultureInfo.InvariantCulture));

        private static T GetListValueOrDefault<T>(IReadOnlyList<T> list, int index, T defaultValue) =>
            index < list.Count ? list[index] : defaultValue;

        private string CreatePrimaryKey(string targetName, string fileName, string emitterName) =>
            ComputeSha256Hex($"{targetName}\0{fileName}\0{emitterName}");

        private string GetRecordPath(string primaryKey) =>
            Path.Combine(_databaseDirectoryPath, primaryKey + RecordFileExtension);

        private string GetRecordLockPath(string primaryKey) =>
            Path.Combine(_databaseDirectoryPath, primaryKey + RecordLockExtension);

        private RecordLockHandle AcquireRecordLock(string primaryKey)
        {
            var lockPath = GetRecordLockPath(primaryKey);
            return AcquireExclusiveLock(lockPath);
        }

        private static RecordLockHandle AcquireExclusiveLock(string lockPath)
        {
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return new RecordLockHandle(lockPath, stream);
                }
                catch (IOException) when (stopwatch.ElapsedMilliseconds < LockAcquireTimeoutMs)
                {
                    Thread.Sleep(LockAcquireRetryDelayMs);
                }
                catch (UnauthorizedAccessException) when (stopwatch.ElapsedMilliseconds < LockAcquireTimeoutMs)
                {
                    Thread.Sleep(LockAcquireRetryDelayMs);
                }
            }
        }

        private void EnsureStorageDirectory()
        {
            if (File.Exists(_databaseDirectoryPath))
            {
                File.Delete(_databaseDirectoryPath);
                Log.Information("Removed legacy dependency database file and switched to directory store: {DatabasePath}", _databaseDirectoryPath);
            }

            Directory.CreateDirectory(_databaseDirectoryPath);

            if (Directory.EnumerateDirectories(_databaseDirectoryPath).Any())
            {
                Directory.Delete(_databaseDirectoryPath, recursive: true);
                Directory.CreateDirectory(_databaseDirectoryPath);
                Log.Information("Reset dependency database layout to flat directory store: {DatabasePath}", _databaseDirectoryPath);
            }
        }

        private static string ComputeSha256Hex(string value)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private enum CacheMode
        {
            Cache,
            NoCache
        }

        private string GetFileSHA(CacheMode cacheMode, string filePath, DependencyCheckOptions opt)
        {
            bool fileExists = CheckFileExist(cacheMode, filePath);
            if (!fileExists || !opt.UseSHA)
                return String.Empty;

            if (_cache is not null &&
                cacheMode == CacheMode.Cache &&
                _cache.FileSHAs.TryGetValue(filePath, out var cachedSHA))
            {
                return cachedSHA;
            }

            if (!OperatingSystem.IsWindows())
            {
                var fileMode = File.GetUnixFileMode(filePath);
                if (!fileMode.HasFlag(UnixFileMode.UserRead))
                    File.SetUnixFileMode(filePath, fileMode | UnixFileMode.UserRead);
            }

            byte[] shaBytes = SHA256.HashData(File.ReadAllBytes(filePath));
            var shaString = Convert.ToHexString(shaBytes).ToLowerInvariant();
            if (_cache is not null)
                _cache.FileSHAs[filePath] = shaString;
            return shaString;
        }

        private DateTime GetFileLastWriteTime(CacheMode cacheMode, string filePath, DependencyCheckOptions opt)
        {
            bool fileExists = CheckFileExist(cacheMode, filePath);
            if (!fileExists || opt.UseSHA)
                return DateTime.MinValue;

            if (_cache is not null &&
                cacheMode == CacheMode.Cache &&
                _cache.FileDateTimes.TryGetValue(filePath, out var cachedLastWriteTime))
            {
                return cachedLastWriteTime;
            }

            var lastWriteTime = File.GetLastWriteTimeUtc(filePath);
            if (_cache is not null)
                _cache.FileDateTimes[filePath] = lastWriteTime;
            return lastWriteTime;
        }

        private bool CheckFileExist(CacheMode cacheMode, string path)
        {
            if (_cache is not null &&
                cacheMode == CacheMode.Cache &&
                _cache.FileExists.TryGetValue(path, out var cachedExists))
            {
                return cachedExists;
            }

            var exists = File.Exists(path);
            if (_cache is not null)
                _cache.FileExists[path] = exists;
            return exists;
        }

        private sealed class RecordLockHandle : IDisposable
        {
            public RecordLockHandle(string lockPath, FileStream stream)
            {
                _lockPath = lockPath;
                _stream = stream;
            }

            public void Dispose()
            {
                _stream.Dispose();
                TryDeleteFile(_lockPath);
            }

            private readonly string _lockPath;
            private readonly FileStream _stream;
        }

        private readonly string _name = "depend";
        private readonly string _databaseDirectoryPath;
        private readonly DependencyDatabaseCache? _cache;
        private readonly bool _defaultUseSHA;
    }

    public struct DependencyRecord
    {
        internal string PrimaryKey { get; set; } = "Invalid";
        internal List<string> InputArgs { get; init; } = new();
        internal List<string> InputFiles { get; init; } = new();
        internal List<DateTime> InputFileTimes { get; init; } = new();
        internal List<string> InputFileSHAs { get; init; } = new();
        internal List<DateTime> ExternalFileTimes { get; set; } = new();
        internal List<string> ExternalFileSHAs { get; set; } = new();
        public List<string> ExternalFiles { get; set; } = new();

        public DependencyRecord() { }
    }
}
