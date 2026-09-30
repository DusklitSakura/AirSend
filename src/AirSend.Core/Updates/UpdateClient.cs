using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AirSend.Core.Logging;

namespace AirSend.Core.Updates;

/// <summary>
/// Talks to the GitHub releases API. Unauthenticated calls are rate limited per
/// IP (60/hour), which is far more than a daily or weekly check needs.
/// </summary>
public sealed class UpdateClient : IDisposable
{
    public const string DefaultRepository = "DusklitSakura/AirSend";

    private const int DownloadAttempts = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly string _repository;
    private readonly bool _ownsClient;

    public UpdateClient(string repository = DefaultRepository, HttpClient? http = null)
    {
        _repository = repository;
        _ownsClient = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd("AirSend"))
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("AirSend/1.0");
        }
    }

    /// <summary>Pause between download attempts; tests set it to zero.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Latest published, non-pre-release release; null when there is none.</summary>
    public async Task<UpdateRelease?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        string url = $"https://api.github.com/repos/{_repository}/releases/latest";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using HttpResponseMessage response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        GitHubRelease? payload = JsonSerializer.Deserialize<GitHubRelease>(json, JsonOptions);

        if (payload?.TagName is null || payload.Draft || payload.Prerelease)
        {
            return null;
        }

        var assets = (payload.Assets ?? [])
            .Where(asset => !string.IsNullOrWhiteSpace(asset.Name) && !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            .Select(asset => new UpdateAsset(asset.Name!, asset.BrowserDownloadUrl!, asset.Size))
            .ToArray();

        return new UpdateRelease(
            payload.TagName,
            payload.Name ?? payload.TagName,
            payload.Body ?? string.Empty,
            payload.HtmlUrl ?? $"https://github.com/{_repository}/releases",
            assets);
    }

    /// <summary>The latest release, but only when it is newer than <paramref name="current"/>.</summary>
    public async Task<UpdateRelease?> GetNewerReleaseAsync(
        UpdateVersion current,
        CancellationToken cancellationToken = default)
    {
        UpdateRelease? release = await GetLatestAsync(cancellationToken).ConfigureAwait(false);
        return release is not null && release.Version.IsNewerThan(current) ? release : null;
    }

    public async Task DownloadAsync(
        UpdateAsset asset,
        string targetPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // A zip of ~90 MB from a release asset host is exactly the kind of transfer
        // that a firewall or an antivirus inspector tears down mid-flight, so a
        // partial download is resumed with a Range request instead of starting over.
        long copied = 0;
        long? total = asset.Size > 0 ? asset.Size : null;
        Exception? lastError = null;

        for (int attempt = 1; attempt <= DownloadAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
                if (copied > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(copied, null);
                }

                using HttpResponseMessage response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (copied > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                {
                    // The server ignored the range: whatever is on disk is not a
                    // prefix of the package, so start again from zero.
                    copied = 0;
                }

                response.EnsureSuccessStatusCode();

                if (response.Content.Headers.ContentLength is { } length)
                {
                    total = copied + length;
                }

                await using Stream source = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using var destination = new FileStream(
                    targetPath,
                    copied > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true);

                byte[] buffer = new byte[81920];
                int read;

                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;

                    if (total is > 0)
                    {
                        progress?.Report(Math.Clamp((double)copied / total.Value, 0d, 1d));
                    }
                }

                progress?.Report(1d);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or System.Net.Sockets.SocketException
                                       && !cancellationToken.IsCancellationRequested)
            {
                lastError = ex;

                if (attempt == DownloadAttempts)
                {
                    break;
                }

                    AppLog.Warn("log.update.download_interrupted", new
                    {
                        package = asset.Name,
                        err = AirSendError.Describe(ex),
                        mb = copied / 1024 / 1024,
                    });

                await Task.Delay(RetryDelay * attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new UpdateException(
            UpdateFailure.Download,
            "error.update.download_failed",
            new
            {
                package = asset.Name,
                err = lastError?.Message ?? Localization.AppText.Get("error.update.attempts_exhausted"),
            });
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}
