using Serilog;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;

namespace SB
{
    public class DownloadSetup : ISetup
    {
        public string HttpProxy { get; set; } = "";
        public object StateLock { get; } = new();
        public Dictionary<string, List<DownloadSource>> FileSources { get; } = new();
        public Dictionary<string, DownloadSource> Sources { get; } = new();
        public string? DefaultSourceName { get; set; }
        public ConcurrentDictionary<string, Task> DownloadingTasks { get; } = new();
        public ConcurrentDictionary<string, System.Threading.Lock> DownloadingLocks { get; } = new();
        public bool Initialized { get; set; } = false;
        public bool ManifestsFetched { get; set; } = false;

        public void Setup(BuildInstance instance)
        {
        }
    }

    public static class Download
    {
        private static DownloadSetup GetDownloadSetup(BuildInstance instance) => instance.GetSetup<DownloadSetup>() ?? instance.AddSetup<DownloadSetup>();

        private static void EnsureInitialized(BuildInstance instance)
        {
            var downloadSetup = GetDownloadSetup(instance);
            if (downloadSetup.Initialized)
                return;

            lock (downloadSetup.StateLock)
            {
                if (downloadSetup.Initialized)
                    return;

                var buildDirs = instance.GetStage<Stages.PrepareBuildDirectoriesStage>()!;
                Directory.CreateDirectory(buildDirs.DownloadDir);
                downloadSetup.Initialized = true;
            }
        }

        public static void AddSource(BuildInstance instance, string name, string url, string? username = null, string? password = null)
        {
            EnsureInitialized(instance);
            lock (GetDownloadSetup(instance).StateLock)
            {
                AddSourceCore(instance, name, url, username, password);
            }
        }

        private static void AddSourceCore(BuildInstance instance, string name, string url, string? username = null, string? password = null)
        {
            var downloadSetup = GetDownloadSetup(instance);
            if (downloadSetup.Sources.ContainsKey(name))
                return;

            var buildDirs = instance.GetStage<Stages.PrepareBuildDirectoriesStage>()!;
            downloadSetup.Sources.Add(name, new DownloadSource
            {
                Name = name,
                URL = url,
                Destination = buildDirs.DownloadDir,
                ManifestPath = Path.Combine(buildDirs.DownloadDir, "manifests", $"{name}.json"),
                Username = username,
                Password = password
            });
            downloadSetup.DefaultSourceName ??= name;
        }

        public static void FetchManifests(BuildInstance instance)
        {
            EnsureInitialized(instance);
            var downloadSetup = GetDownloadSetup(instance);

            var buildDirs = instance.GetStage<Stages.PrepareBuildDirectoriesStage>()!;
            lock (downloadSetup.StateLock)
            {
                if (!downloadSetup.ManifestsFetched)
                {
                    downloadSetup.ManifestsFetched = true;
                    Directory.CreateDirectory(Path.Combine(buildDirs.DownloadDir, "manifests"));
                    foreach (var source in downloadSetup.Sources.Values)
                    {
                        var url = source.URL + "manifest.json";
                        try
                        {
                            Log.Information("fetching manifest ... from {URL} to {SourceManifestPath}", url, source.ManifestPath);

                            byte[] bytes;
                            using (var http = CreateHttpClient(instance, source))
                            {
                                http.Timeout = TimeSpan.FromMinutes(30);
                                var bytesTask = http.GetByteArrayAsync(url);
                                bytesTask.Wait();
                                bytes = bytesTask.Result;
                            }

                            source.ManifestString = bytes;
                            // write to disk, just for debugging
                            File.WriteAllBytes(source.ManifestPath, source.ManifestString);
                        }
                        catch (Exception e)
                        {
                            Log.Error(e, "Failed to fetch manifest from {URL}, message: {eMessage}", url, e.Message);
                        }
                    }
                    LoadManifests(instance);
                }
            }
        }

        private static void LoadManifests(BuildInstance instance)
        {
            var downloadSetup = GetDownloadSetup(instance);
            downloadSetup.FileSources.Clear();
            foreach (var source in downloadSetup.Sources.Values)
            {
                source.FileSHAs.Clear();
                var manifestJsonReader = new System.Text.Json.Utf8JsonReader(source.ManifestString);
                manifestJsonReader.Read(); // ROOT
                while (manifestJsonReader.Read())
                {
                    if (manifestJsonReader.TokenType == System.Text.Json.JsonTokenType.PropertyName)
                    {
                        var fileName = manifestJsonReader.GetString()!;
                        manifestJsonReader.Read();
                        var sha = manifestJsonReader.GetString()!;
                        // add result to source list
                        List<DownloadSource> sourcesList;
                        if (!downloadSetup.FileSources.TryGetValue(fileName, out sourcesList!))
                        {
                            sourcesList = new List<DownloadSource>();
                            downloadSetup.FileSources.Add(fileName, sourcesList);
                        }
                        sourcesList.Add(source);
                        // add result to sha checker
                        HashSet<string> fileShas;
                        if (!source.FileSHAs.TryGetValue(fileName, out fileShas!))
                        {
                            fileShas = new HashSet<string>();
                            source.FileSHAs.Add(fileName, fileShas);
                        }
                        fileShas.Add(sha);
                    }
                }
                // check sha conflict
                source.FileSHAs.Where(static entry => entry.Value.Count > 1).ToList().ForEach(static entry =>
                {
                    throw new Exception($"{entry.Key} SHA conflict detected!");
                });
                // print manifest info
                Log.Information("----------------manifest info----------------");
                {
                    Log.Information("{SourceName} ... {SourceURL}", source.Name, source.URL);
                }
                Log.Information("----------------manifest info----------------");
            }
        }

        private static async Task DownloadFromSource(BuildInstance instance, DownloadSource source, string fileName)
        {
            var buildDirs = instance.GetStage<Stages.PrepareBuildDirectoriesStage>()!;
            var destination = Path.Combine(buildDirs.DownloadDir, fileName);
            var url = source.URL + fileName;
            Log.Information("downloading ... from {URL} to {Destination}", url, destination);
            byte[] bytes;
            using (var http = CreateHttpClient(instance, source))
            {
                http.Timeout = TimeSpan.FromMinutes(30);
                bytes = await http.GetByteArrayAsync(url);
            }
            await File.WriteAllBytesAsync(destination, bytes);
        }

        private static async Task DownloadFromUrl(BuildInstance instance, string url, string destination)
        {
            Log.Information("downloading ... from {URL} to {Destination}", url, destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            var temporaryDestination = destination + ".downloading";
            const int maxAttempts = 5;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    await DownloadHttpFileFromUrl(instance, url, temporaryDestination);

                    if (File.Exists(destination))
                        File.Delete(destination);
                    File.Move(temporaryDestination, destination);
                    return;
                }
                catch (Exception ex) when (attempt < maxAttempts)
                {
                    Log.Warning(ex, "direct url download failed, retrying {Attempt}/{MaxAttempts}: {URL}", attempt, maxAttempts, url);
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
                }
            }

            await DownloadHttpFileFromUrl(instance, url, temporaryDestination);

            if (File.Exists(destination))
                File.Delete(destination);
            File.Move(temporaryDestination, destination);
        }

        private static async Task DownloadHttpFileFromUrl(BuildInstance instance, string url, string temporaryDestination)
        {
            using var http = CreateHttpClient(instance);
            http.Timeout = TimeSpan.FromMinutes(30);

            var resumeOffset = File.Exists(temporaryDestination) ? new FileInfo(temporaryDestination).Length : 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (resumeOffset > 0)
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeOffset, null);

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var append = resumeOffset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (resumeOffset > 0 && !append)
            {
                Log.Information("direct url download source ignored range request; restarting {File}", temporaryDestination);
                File.Delete(temporaryDestination);
                resumeOffset = 0;
            }
            if (append)
            {
                Log.Information("direct url download resumes {File} from byte {Offset}", temporaryDestination, resumeOffset);
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(
                temporaryDestination,
                append ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None);
            await contentStream.CopyToAsync(fileStream);
        }

        private static string ComputeFileSha256(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
        }

        private static string BuildDirectDownloadFileName(string url, string fileName)
        {
            var resolvedFileName = fileName;
            if (string.IsNullOrWhiteSpace(resolvedFileName) &&
                Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                resolvedFileName = Path.GetFileName(uri.LocalPath);
            }
            if (string.IsNullOrWhiteSpace(resolvedFileName))
            {
                var urlHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
                resolvedFileName = $"{urlHash}.download";
            }

            foreach (var invalidChar in Path.GetInvalidFileNameChars())
            {
                resolvedFileName = resolvedFileName.Replace(invalidChar, '_');
            }
            return resolvedFileName;
        }

        private static bool IsDirectDownloadCacheValid(string filePath, string metadataPath, string url, string sha256)
        {
            if (!File.Exists(filePath) || !File.Exists(metadataPath))
                return false;

            try
            {
                var metadata = System.Text.Json.JsonSerializer.Deserialize<DirectDownloadMetadata>(File.ReadAllText(metadataPath));
                if (metadata is null || metadata.Url != url)
                    return false;

                var fileLength = new FileInfo(filePath).Length;
                if (metadata.FileLength != fileLength)
                    return false;

                if (string.IsNullOrWhiteSpace(sha256))
                    return true;

                var expectedSha = sha256.ToLowerInvariant();
                return metadata.ExpectedSha256 == expectedSha && metadata.ActualSha256 == expectedSha;
            }
            catch
            {
                return false;
            }
        }

        private static string WriteDirectDownloadMetadata(string metadataPath, string url, string sha256, string filePath, string actualSha = "")
        {
            if (string.IsNullOrWhiteSpace(actualSha))
                actualSha = ComputeFileSha256(filePath);
            var metadata = new DirectDownloadMetadata
            {
                Url = url,
                ExpectedSha256 = string.IsNullOrWhiteSpace(sha256) ? "" : sha256.ToLowerInvariant(),
                ActualSha256 = actualSha,
                FileLength = new FileInfo(filePath).Length,
                DownloadedAtUtc = DateTime.UtcNow.ToString("O")
            };
            File.WriteAllText(
                metadataPath,
                System.Text.Json.JsonSerializer.Serialize(metadata, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                }));
            return actualSha;
        }

        public static async Task<string> DownloadFile(BuildInstance instance, string fileName, string overrideSource = "", bool force = false)
        {
            EnsureInitialized(instance);

            var buildDirs = instance.GetStage<Stages.PrepareBuildDirectoriesStage>()!;
            using (Profiler.BeginZone($"FetchManifests", color: (uint)Profiler.ColorType.Pink1))
            {
                Download.FetchManifests(instance);
            }

            // Already exist on disk
            var filePath = Path.Combine(buildDirs.DownloadDir, fileName);
            var downloadSetup = GetDownloadSetup(instance);
            var sourceName = string.IsNullOrWhiteSpace(overrideSource)
                ? downloadSetup.DefaultSourceName
                : overrideSource;
            if (string.IsNullOrWhiteSpace(sourceName) ||
                !downloadSetup.Sources.TryGetValue(sourceName, out var source))
            {
                throw new InvalidOperationException(
                    "No download source is configured. Call AddDownloadSource before using named downloads.");
            }
            if (!force && File.Exists(filePath))
            {
                var existedSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(buildDirs.DownloadDir, fileName)))).ToUpperInvariant();
                var manifestSha = source.FileSHAs[fileName].First().ToUpperInvariant();
                if (existedSha == manifestSha)
                {
                    Log.Information("downloading ... restore existed {FileName}", fileName);
                    return filePath;
                }
            }

            // Download from source
            Task? downloadTask = null;
            lock (downloadSetup.DownloadingLocks.GetOrAdd(fileName, _ => new System.Threading.Lock()))
            {
                if (!downloadSetup.DownloadingTasks.TryGetValue(fileName, out downloadTask))
                {
                    downloadTask = DownloadFromSource(instance, source, fileName);
                    downloadSetup.DownloadingTasks.TryAdd(fileName, downloadTask);
                }
            }
            await downloadTask!;
            return filePath;
        }

        public static async Task<string> DownloadFileFromUrl(BuildInstance instance, string url, string fileName = "", string sha256 = "", bool force = false)
        {
            EnsureInitialized(instance);

            var buildDirs = instance.GetStage<Stages.PrepareBuildDirectoriesStage>()!;
            var resolvedFileName = BuildDirectDownloadFileName(url, fileName);
            var filePath = Path.Combine(buildDirs.DownloadDir, resolvedFileName);
            var metadataPath = filePath + ".direct-download.json";

            if (!force && IsDirectDownloadCacheValid(filePath, metadataPath, url, sha256))
            {
                Log.Information("downloading ... restore existed direct url file {FileName}", resolvedFileName);
                return filePath;
            }
            if (!force && File.Exists(filePath) && !string.IsNullOrWhiteSpace(sha256))
            {
                var cachedSha = ComputeFileSha256(filePath);
                var expectedCachedSha = sha256.ToLowerInvariant();
                if (cachedSha == expectedCachedSha)
                {
                    WriteDirectDownloadMetadata(metadataPath, url, sha256, filePath, cachedSha);
                    Log.Information("downloading ... restore existed direct url file by SHA256 {FileName}", resolvedFileName);
                    return filePath;
                }
            }

            var downloadSetup = GetDownloadSetup(instance);
            var downloadKey = $"direct-url|{resolvedFileName}|{url}";
            Task? downloadTask = null;
            lock (downloadSetup.DownloadingLocks.GetOrAdd(downloadKey, _ => new System.Threading.Lock()))
            {
                if (!downloadSetup.DownloadingTasks.TryGetValue(downloadKey, out downloadTask))
                {
                    downloadTask = DownloadFromUrl(instance, url, filePath);
                    downloadSetup.DownloadingTasks.TryAdd(downloadKey, downloadTask);
                }
            }
            await downloadTask!;

            var actualSha = WriteDirectDownloadMetadata(metadataPath, url, sha256, filePath);
            if (!string.IsNullOrWhiteSpace(sha256))
            {
                var expectedSha = sha256.ToLowerInvariant();
                if (actualSha != expectedSha)
                    throw new Exception($"Downloaded file SHA256 mismatch: {resolvedFileName}, expected {expectedSha}, actual {actualSha}");
            }

            return filePath;
        }

        private static HttpClient CreateHttpClient(BuildInstance instance, DownloadSource? source = null)
        {
            var downloadSetup = GetDownloadSetup(instance);
            var handler = new HttpClientHandler
            {
                Proxy = string.IsNullOrEmpty(downloadSetup.HttpProxy) ? HttpClient.DefaultProxy : new WebProxy(downloadSetup.HttpProxy)
            };
            var username = source?.Username;
            var password = source?.Password;

            if (!string.IsNullOrEmpty(username))
            {
                handler.Credentials = new NetworkCredential(username, password);
            }

            var httpClient = new HttpClient(handler);

            if (!string.IsNullOrEmpty(username))
            {
                var credentials = Convert.ToBase64String(
                    System.Text.Encoding.ASCII.GetBytes($"{username}:{password}"));
                httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
            }

            return httpClient;
        }

    }

    public class DownloadSource
    {
        public required string Name { get; init; }
        public required string URL { get; init; }
        public required string Destination { get; init; }
        public required string ManifestPath { get; init; }
        public Byte[]? ManifestString { get; set; }
        public string? Username { get; init; }
        public string? Password { get; init; }
        public Dictionary<string, HashSet<string>> FileSHAs = new();
    }

    public class DirectDownloadMetadata
    {
        public string Url { get; set; } = "";
        public string ExpectedSha256 { get; set; } = "";
        public string ActualSha256 { get; set; } = "";
        public long FileLength { get; set; } = 0;
        public string DownloadedAtUtc { get; set; } = "";
    }

    public static partial class Engine
    {
        public static void AddDownloadSource(this BuildInstance instance, string name, string url, string? username = null, string? password = null) =>
            Download.AddSource(instance, name, url, username, password);

        public static void FetchDownloadManifests(this BuildInstance instance) => Download.FetchManifests(instance);

        public static Task<string> DownloadFile(this BuildInstance instance, string fileName, string overrideSource = "", bool force = false) =>
            Download.DownloadFile(instance, fileName, overrideSource, force);

        public static Task<string> DownloadFileFromUrl(this BuildInstance instance, string url, string fileName = "", string sha256 = "", bool force = false) =>
            Download.DownloadFileFromUrl(instance, url, fileName, sha256, force);
    }
}
