using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using LocalDependencyCache.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LocalDependencyCache.Tests;

public sealed class GalleryClientTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-01T10:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Paging_advances_published_cursor_and_returns_every_package()
    {
        var packages = new List<(string Id, DateTimeOffset Published)>();
        for (var i = 0; i < 7; i++)
        {
            packages.Add(($"Module{i}", Start.AddSeconds(i + 1).AddMilliseconds(250)));
        }

        var gallery = new FakeGallery(packages);
        var pages = await CollectAsync(CreateClient(gallery), Start, pageSize: 3);

        Assert.Equal(packages.Select(p => p.Id).Order(), pages.SelectMany(p => p).Select(p => p.Id).Distinct().Order());
        Assert.All(gallery.Requests.Skip(1), request => Assert.Contains("Published ge datetime", request));
        Assert.DoesNotContain(gallery.Requests, request => request.Contains("$skip=3"));
    }

    [Fact]
    public async Task Paging_steps_through_more_packages_than_a_page_in_one_second()
    {
        var packages = Enumerable.Range(0, 7)
            .Select(i => (Id: $"Burst{i}", Published: Start.AddSeconds(5).AddMilliseconds(i)))
            .Append((Id: "After", Published: Start.AddSeconds(9)))
            .ToList();

        var gallery = new FakeGallery(packages);
        var pages = await CollectAsync(CreateClient(gallery), Start, pageSize: 3);

        Assert.Equal(packages.Select(p => p.Id).Order(), pages.SelectMany(p => p).Select(p => p.Id).Distinct().Order());
        Assert.True(gallery.Requests.Count < 10, $"Expected paging to terminate quickly, made {gallery.Requests.Count} requests.");
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task Transient_status_codes_are_retried(HttpStatusCode statusCode)
    {
        var gallery = new FakeGallery(new[] { ("Module", Start.AddMinutes(1)) }) { FailuresBeforeSuccess = 2, FailureStatus = statusCode };

        var pages = await CollectAsync(CreateClient(gallery, maxRetries: 3), Start, pageSize: 10);

        Assert.Equal("Module", Assert.Single(Assert.Single(pages)).Id);
        Assert.Equal(3, gallery.Requests.Count);
    }

    [Fact]
    public async Task Network_errors_are_retried()
    {
        var gallery = new FakeGallery(new[] { ("Module", Start.AddMinutes(1)) }) { NetworkFailuresBeforeSuccess = 1 };

        var pages = await CollectAsync(CreateClient(gallery, maxRetries: 2), Start, pageSize: 10);

        Assert.Single(pages);
        Assert.Equal(2, gallery.Requests.Count);
    }

    [Fact]
    public async Task Non_transient_status_codes_fail_without_retry()
    {
        var gallery = new FakeGallery(Array.Empty<(string, DateTimeOffset)>()) { FailuresBeforeSuccess = 10, FailureStatus = HttpStatusCode.NotFound };

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => CollectAsync(CreateClient(gallery, maxRetries: 3), Start, 10));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Single(gallery.Requests);
    }

    [Fact]
    public async Task Retries_are_bounded()
    {
        var gallery = new FakeGallery(Array.Empty<(string, DateTimeOffset)>()) { FailuresBeforeSuccess = 10, FailureStatus = HttpStatusCode.InternalServerError };

        await Assert.ThrowsAsync<HttpRequestException>(() => CollectAsync(CreateClient(gallery, maxRetries: 2), Start, 10));

        Assert.Equal(3, gallery.Requests.Count);
    }

    private static GalleryClient CreateClient(FakeGallery gallery, int maxRetries = 0)
        => new(new HttpClient(gallery),
            Options.Create(new SyncSettings
            {
                GalleryEndpoint = "https://gallery.example.test/api/v2/Search()",
                MaxRetries = maxRetries,
                RetryBaseDelayMilliseconds = 0
            }),
            NullLogger<GalleryClient>.Instance);

    private static async Task<List<IReadOnlyList<PackageEntry>>> CollectAsync(GalleryClient client, DateTimeOffset from, int pageSize)
    {
        var pages = new List<IReadOnlyList<PackageEntry>>();
        await foreach (var page in client.GetPackagePagesAsync(from, pageSize, CancellationToken.None))
        {
            pages.Add(page);
        }

        return pages;
    }

    /// <summary>
    /// Serves an OData feed from memory, honoring the Published filter, $orderby=Published, $skip, and $top.
    /// </summary>
    private sealed class FakeGallery : HttpMessageHandler
    {
        private static readonly Regex FilterPattern = new(@"Published (gt|ge) datetime'([^']+)'", RegexOptions.Compiled);
        private readonly IReadOnlyList<(string Id, DateTimeOffset Published)> _packages;

        public FakeGallery(IEnumerable<(string Id, DateTimeOffset Published)> packages)
        {
            _packages = packages.OrderBy(p => p.Published).ToList();
        }

        public List<string> Requests { get; } = new();

        public int FailuresBeforeSuccess { get; init; }

        public HttpStatusCode FailureStatus { get; init; } = HttpStatusCode.ServiceUnavailable;

        public int NetworkFailuresBeforeSuccess { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query.Replace('+', ' '));
            Requests.Add(query);

            if (Requests.Count <= NetworkFailuresBeforeSuccess)
            {
                throw new HttpRequestException("Simulated network failure.");
            }

            if (Requests.Count <= FailuresBeforeSuccess)
            {
                var failure = new HttpResponseMessage(FailureStatus);
                if (FailureStatus == HttpStatusCode.TooManyRequests)
                {
                    failure.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                }

                return Task.FromResult(failure);
            }

            var match = FilterPattern.Match(query);
            var cursor = DateTimeOffset.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            var inclusive = match.Groups[1].Value == "ge";
            var skip = int.Parse(Regex.Match(query, @"\$skip=(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
            var top = int.Parse(Regex.Match(query, @"\$top=(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);

            var page = _packages
                .Where(p => inclusive ? p.Published >= cursor : p.Published > cursor)
                .Skip(skip)
                .Take(top);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(BuildFeed(page), Encoding.UTF8, "application/atom+xml") });
        }

        private static string BuildFeed(IEnumerable<(string Id, DateTimeOffset Published)> packages)
        {
            var entries = string.Concat(packages.Select(p => $"""
                <entry>
                  <m:properties>
                    <d:Id>{p.Id}</d:Id>
                    <d:Version>1.0.0</d:Version>
                    <d:Published m:type="Edm.DateTime">{p.Published.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fff}</d:Published>
                  </m:properties>
                </entry>
                """));

            return $"""
                <?xml version="1.0" encoding="utf-8"?>
                <feed xmlns="http://www.w3.org/2005/Atom"
                      xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices"
                      xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
                {entries}
                </feed>
                """;
        }
    }
}
