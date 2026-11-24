using System.Globalization;
using System.Net;
using System.Xml;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalDependencyCache.Core;

/// <summary>
/// Reads package metadata from the PowerShell Gallery OData Search() endpoint. Every query sends
/// $filter, $orderby, $skip, and $top together; the Gallery rejects some other operator combinations.
/// </summary>
public sealed class GalleryClient : IGalleryClient
{
    private const int MaxPageSize = 100;

    private readonly HttpClient _httpClient;
    private readonly SyncSettings _settings;
    private readonly ILogger<GalleryClient> _logger;

    public GalleryClient(HttpClient httpClient, IOptions<SyncSettings> settings, ILogger<GalleryClient> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PackageEntry>> GetTopPackagesAsync(int count, CancellationToken cancellationToken)
    {
        var results = new List<PackageEntry>();
        while (results.Count < count)
        {
            var top = Math.Min(PageSize, count - results.Count);
            var page = await GetPageWithRetryAsync(
                BuildRequestUri("IsLatestVersion", "DownloadCount desc", results.Count, top, includePrerelease: false),
                cancellationToken);
            results.AddRange(page);

            if (page.Count < top)
            {
                break;
            }
        }

        _logger.LogInformation("Fetched the top {Count} packages by download count.", results.Count);
        return results;
    }

    public async Task<PackageEntry?> GetLatestVersionAsync(string packageId, CancellationToken cancellationToken)
    {
        var page = await GetPageWithRetryAsync(
            BuildRequestUri($"Id eq {QuoteLiteral(packageId)} and IsLatestVersion", "Published desc", 0, 1, includePrerelease: false),
            cancellationToken);
        return page.Count > 0 ? page[0] : null;
    }

    public async Task<IReadOnlyList<PackageEntry>> GetAllVersionsAsync(string packageId, CancellationToken cancellationToken)
    {
        var results = new List<PackageEntry>();
        while (true)
        {
            var page = await GetPageWithRetryAsync(
                BuildRequestUri($"Id eq {QuoteLiteral(packageId)}", "Published", results.Count, PageSize, includePrerelease: true),
                cancellationToken);
            results.AddRange(page);

            if (page.Count < PageSize)
            {
                return results;
            }
        }
    }

    private int PageSize => _settings.PageSize is > 0 and <= MaxPageSize ? _settings.PageSize : MaxPageSize;

    private static string QuoteLiteral(string value) => "'" + value.Replace("'", "''") + "'";

    private async Task<IReadOnlyList<PackageEntry>> GetPageWithRetryAsync(Uri requestUri, CancellationToken cancellationToken)
    {
        var maxRetries = Math.Max(0, _settings.MaxRetries);

        for (var attempt = 0; ; attempt++)
        {
            TimeSpan? retryAfter = null;

            try
            {
                using var response = await _httpClient.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    return GalleryFeedParser.ParsePackages(stream);
                }

                if (!IsTransient(response.StatusCode) || attempt >= maxRetries)
                {
                    response.EnsureSuccessStatusCode();
                }

                retryAfter = GetRetryAfter(response);
                _logger.LogWarning("Gallery returned {StatusCode} for {Uri}.", (int)response.StatusCode, requestUri);
            }
            catch (Exception exception) when (attempt < maxRetries && IsTransient(exception, cancellationToken))
            {
                _logger.LogWarning(exception, "Transient error fetching {Uri}.", requestUri);
            }

            var delay = retryAfter ?? ComputeBackoff(attempt);
            _logger.LogInformation("Retrying gallery request in {Delay} (attempt {Attempt} of {MaxRetries}).", delay, attempt + 1, maxRetries);
            await Task.Delay(delay, cancellationToken);
        }
    }

    private TimeSpan ComputeBackoff(int attempt)
    {
        var baseDelayMs = Math.Max(0, _settings.RetryBaseDelayMilliseconds);
        var exponential = baseDelayMs * Math.Pow(2, attempt);
        var jitter = Random.Shared.NextDouble() * baseDelayMs;
        return TimeSpan.FromMilliseconds(Math.Min(exponential + jitter, TimeSpan.FromMinutes(1).TotalMilliseconds));
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private static bool IsTransient(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static bool IsTransient(Exception exception, CancellationToken cancellationToken)
        => exception switch
        {
            HttpRequestException { StatusCode: { } statusCode } => IsTransient(statusCode),
            HttpRequestException => true,
            // HttpClient timeouts surface as TaskCanceledException without the caller's token being cancelled.
            TaskCanceledException => !cancellationToken.IsCancellationRequested,
            IOException => true,
            XmlException => true,
            _ => false
        };

    private Uri BuildRequestUri(string filter, string orderBy, int skip, int top, bool includePrerelease)
    {
        var builder = new UriBuilder(_settings.GalleryEndpoint)
        {
            Query = string.Join('&',
                new[]
                {
                    ("$filter", filter),
                    ("$orderby", orderBy),
                    ("$skip", skip.ToString(CultureInfo.InvariantCulture)),
                    ("$top", top.ToString(CultureInfo.InvariantCulture)),
                    ("includePrerelease", includePrerelease ? "true" : "false")
                }
                .Select(pair => $"{WebUtility.UrlEncode(pair.Item1)}={WebUtility.UrlEncode(pair.Item2)}"))
        };

        return builder.Uri;
    }
}
