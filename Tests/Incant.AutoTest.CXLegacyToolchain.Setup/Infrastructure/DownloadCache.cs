using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;

namespace Incant.AutoTest.CXLegacyToolchain.Setup;

internal sealed class DownloadCache(SetupContext context)
{
    private const int DownloadAttempts = 3;
    private const int BufferSize = 128 * 1024;
    private const long ProgressIntervalBytes = 32L * 1024 * 1024;
    private static readonly TimeSpan s_inactivityTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_progressInterval = TimeSpan.FromSeconds(15);
    private static readonly HttpClient s_httpClient = CreateHttpClient();

    internal async Task<string> GetAsync(
        string uri,
        string sha256,
        string? fileName,
        CancellationToken cancellationToken)
    {
        Uri source = Validate(uri, sha256);
        string cacheName = fileName ?? Uri.UnescapeDataString(source.Segments[^1]);
        if (string.IsNullOrWhiteSpace(cacheName)
            || !string.Equals(Path.GetFileName(cacheName), cacheName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Download cache name '{cacheName}' must be a file name.",
                nameof(fileName));
        }

        string expectedHash = sha256.ToLowerInvariant();
        Directory.CreateDirectory(context.Paths.DownloadsRoot);
        string archive = context.Paths.AssertChild(
            Path.Combine(context.Paths.DownloadsRoot, cacheName));
        if (File.Exists(archive))
        {
            string cachedHash = await ComputeSha256Async(archive, cancellationToken)
                .ConfigureAwait(false);
            if (string.Equals(cachedHash, expectedHash, StringComparison.Ordinal))
            {
                var information = new FileInfo(archive);
                Console.WriteLine(
                    $"[download:cache-hit] file={archive} bytes={information.Length} sha256={cachedHash}");
                return archive;
            }

            Console.Error.WriteLine(
                $"[download:cache-invalid] file={archive} expectedSha256={expectedHash} "
                + $"actualSha256={cachedHash}");
            context.Paths.DeleteFile(archive);
        }

        Exception? lastError = null;
        for (int attempt = 1; attempt <= DownloadAttempts; ++attempt)
        {
            string temporary = context.Paths.AssertChild(
                archive + $".downloading.{Environment.ProcessId}.{Guid.NewGuid():N}");
            try
            {
                await DownloadAsync(
                    source, temporary, expectedHash, attempt, cancellationToken)
                    .ConfigureAwait(false);
                File.Move(temporary, archive);
                return archive;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException
                or IOException
                or TimeoutException
                or UnauthorizedAccessException)
            {
                lastError = exception;
                if (File.Exists(temporary))
                {
                    context.Paths.DeleteFile(temporary);
                }

                if (attempt == DownloadAttempts)
                {
                    break;
                }

                TimeSpan delay = TimeSpan.FromSeconds(attempt * 2);
                Console.Error.WriteLine(
                    $"[download:retry] attempt={attempt} nextAttempt={attempt + 1} "
                    + $"delayMs={delay.TotalMilliseconds:F0} reason={exception.Message}");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    context.Paths.DeleteFile(temporary);
                }
            }
        }

        throw new IOException(
            $"Could not download '{source}' after {DownloadAttempts} attempts.",
            lastError);
    }

    internal Task<string> GetAsync(
        string uri,
        string sha256,
        CancellationToken cancellationToken) =>
        GetAsync(uri, sha256, null, cancellationToken);

    internal static async Task CopyAtomicallyAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        string fullDestination = Path.GetFullPath(destination);
        string? directory = Path.GetDirectoryName(fullDestination);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("The copy destination has no parent directory.", nameof(destination));
        }

        Directory.CreateDirectory(directory);
        string temporary = fullDestination + $".copying.{Environment.ProcessId}.{Guid.NewGuid():N}";
        try
        {
            await using (FileStream input = new(
                source,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (FileStream output = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, BufferSize, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, fullDestination, overwrite: true);
            Console.WriteLine($"[download:copy] source={source} destination={fullDestination}");
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static async Task DownloadAsync(
        Uri source,
        string destination,
        string expectedHash,
        int attempt,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"[download:start] uri={source} attempt={attempt}/{DownloadAttempts}");
        Console.WriteLine(
            $"[download:expected] sha256={expectedHash} destination={destination}");
        long started = Stopwatch.GetTimestamp();
        using var inactivity = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        inactivity.CancelAfter(s_inactivityTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            request.Headers.UserAgent.ParseAdd("Incant-Toolchain-AutoTest-Setup/2");
            using HttpResponseMessage response = await s_httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                inactivity.Token).ConfigureAwait(false);
            if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
            {
                throw new HttpRequestException(
                    $"Download '{source}' returned HTTP {(int)response.StatusCode} "
                    + $"{response.ReasonPhrase}.",
                    null,
                    response.StatusCode);
            }

            long? declaredLength = response.Content.Headers.ContentLength;
            Console.WriteLine(
                $"[download:response] status={(int)response.StatusCode} finalUri={response.RequestMessage?.RequestUri} "
                + $"contentLength={declaredLength?.ToString() ?? "unknown"}");
            await using Stream input = await response.Content.ReadAsStreamAsync(inactivity.Token)
                .ConfigureAwait(false);
            await using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[BufferSize];
            long received = 0;
            long lastReportedBytes = 0;
            long lastReported = Stopwatch.GetTimestamp();
            while (true)
            {
                inactivity.CancelAfter(s_inactivityTimeout);
                int count = await input.ReadAsync(buffer, inactivity.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken)
                    .ConfigureAwait(false);
                hash.AppendData(buffer, 0, count);
                received += count;
                TimeSpan sinceReport = Stopwatch.GetElapsedTime(lastReported);
                if (received - lastReportedBytes >= ProgressIntervalBytes
                    || sinceReport >= s_progressInterval)
                {
                    Console.WriteLine(
                        $"[download:progress] bytes={received} total={declaredLength?.ToString() ?? "unknown"} "
                        + $"elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
                    lastReportedBytes = received;
                    lastReported = Stopwatch.GetTimestamp();
                }
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (declaredLength is long expectedLength && received != expectedLength)
            {
                throw new IOException(
                    $"Download '{source}' was truncated: expected {expectedLength} bytes, "
                    + $"received {received}.");
            }

            string actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"SHA-256 mismatch for '{source}': expected {expectedHash}, found {actualHash}.");
            }

            Console.WriteLine(
                $"[download:success] bytes={received} sha256={actualHash} "
                + $"durationMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && inactivity.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Download '{source}' received no data for {s_inactivityTimeout.TotalMilliseconds:F0} ms.",
                exception);
        }
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static Uri Validate(string uri, string sha256)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? source)
            || source.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException($"Download URI '{uri}' must be an absolute HTTPS URI.", nameof(uri));
        }

        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                $"Download '{uri}' has an invalid SHA-256 value '{sha256}'.",
                nameof(sha256));
        }

        return source;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }
}
