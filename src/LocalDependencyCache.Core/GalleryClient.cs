using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Xml;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalDependencyCache.Core;

public sealed class GalleryClient : IGalleryClient
{
    private readonly HttpClient _httpClient;
    private readonly SyncSettings _settings;
    private readonly ILogger<GalleryClient> _logger;

    public GalleryClient(HttpClient httpClient, IOptions<SyncSettings> settings, ILogger<GalleryClient> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Streams packages published after <paramref name="checkpointUtc"/>, oldest first, one page at a time.
    /// Paging advances a Published cursor instead of growing $skip, so deep full syncs stay cheap.
    /// Packages published in the same second as a page boundary can be returned twice; callers upsert.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<PackageEntry>> GetPackagePagesAsync(
        DateTimeOffset checkpointUtc,
        int pageSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var effectivePageSize = pageSize > 0 ? pageSize : _settings.PageSize;
        var cursor = TruncateToSecond(checkpointUtc);
        var inclusive = false;
        var skip = 0;

        while (true)
        {
            var requestUri = BuildRequestUri(cursor, inclusive, skip, effectivePageSize);
            var page = await GetPageWithRetryAsync(requestUri, cancellationToken);

            _logger.LogInformation(
                "Fetched {PackageCount} packages from gallery (Published {Operator} {Cursor:O}, skip {Skip}).",
                page.Count, inclusive ? "ge" : "gt", cursor, skip);

            if (page.Count > 0)
            {
                yield return page;
            }

            if (page.Count < effectivePageSize)
            {
                yield break;
            }

            var lastPublished = page[^1].PublishedUtc;
            if (lastPublished is null)
            {
                skip += effectivePageSize;
                continue;
            }

            var lastSecond = TruncateToSecond(lastPublished.Value);
            if (inclusive && lastSecond == cursor)
            {
                // The whole page shares one second; step past it with $skip to guarantee progress.
                skip += effectivePageSize;
            }
            else
            {
                cursor = lastSecond;
                inclusive = true;
                skip = 0;
            }
        }
    }

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

    private static DateTimeOffset TruncateToSecond(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }

    private Uri BuildRequestUri(DateTimeOffset cursorUtc, bool inclusive, int skip, int pageSize)
    {
        var comparison = inclusive ? "ge" : "gt";
        var builder = new UriBuilder(_settings.GalleryEndpoint)
        {
            Query = string.Join('&',
                new[]
                {
                    ("$filter", $"Published {comparison} datetime'{cursorUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}'"),
                    ("$orderby", "Published"),
                    ("$skip", skip.ToString(CultureInfo.InvariantCulture)),
                    ("$top", pageSize.ToString(CultureInfo.InvariantCulture)),
                    ("includePrerelease", "true")
                }
                .Select(pair => $"{WebUtility.UrlEncode(pair.Item1)}={WebUtility.UrlEncode(pair.Item2)}"))
        };

        return builder.Uri;
    }
}
