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
    [Fact]
    public async Task Top_packages_are_the_latest_stable_versions_ordered_by_downloads()
    {
        var gallery = new FakeGallery(Ranked(3));

        var top = await CreateClient(gallery).GetTopPackagesAsync(3, CancellationToken.None);

        Assert.Equal(new[] { "Pkg0", "Pkg1", "Pkg2" }, top.Select(p => p.Id));
        var request = Assert.Single(gallery.Requests);
        Assert.Contains("$filter=IsLatestVersion&", request);
        Assert.Contains("$orderby=DownloadCount desc", request);
        Assert.Contains("$skip=0&$top=3", request);
        Assert.Contains("includePrerelease=false", request);
    }

    [Fact]
    public async Task Top_packages_page_beyond_the_100_item_limit()
    {
        var gallery = new FakeGallery(Ranked(300));

        var top = await CreateClient(gallery).GetTopPackagesAsync(150, CancellationToken.None);

        Assert.Equal(150, top.Count);
        Assert.Equal("Pkg149", top[^1].Id);
        Assert.Collection(gallery.Requests,
            first => Assert.Contains("$skip=0&$top=100", first),
            second => Assert.Contains("$skip=100&$top=50", second));
    }

    [Fact]
    public async Task Top_packages_stop_when_the_gallery_has_fewer()
    {
        var gallery = new FakeGallery(Ranked(120));

        Assert.Equal(120, (await CreateClient(gallery).GetTopPackagesAsync(500, CancellationToken.None)).Count);
        Assert.Equal(2, gallery.Requests.Count);
    }

    [Fact]
    public async Task Latest_version_queries_one_stable_version_by_id()
    {
        var gallery = new FakeGallery(_ => new[] { ("Az.Accounts", "5.5.5") });

        var latest = await CreateClient(gallery).GetLatestVersionAsync("Az.Accounts", CancellationToken.None);

        Assert.Equal("5.5.5", latest?.Version);
        var request = Assert.Single(gallery.Requests);
        Assert.Contains("$filter=Id eq 'Az.Accounts' and IsLatestVersion&", request);
        Assert.Contains("$top=1", request);
        Assert.Contains("includePrerelease=false", request);
    }

    [Fact]
    public async Task Latest_version_is_null_when_the_package_has_none()
    {
        var gallery = new FakeGallery(_ => Array.Empty<(string, string)>());

        Assert.Null(await CreateClient(gallery).GetLatestVersionAsync("Missing", CancellationToken.None));
    }

    [Fact]
    public async Task Package_ids_are_escaped_in_filters()
    {
        var gallery = new FakeGallery(_ => Array.Empty<(string, string)>());

        await CreateClient(gallery).GetLatestVersionAsync("O'Brien", CancellationToken.None);

        Assert.Contains("Id eq 'O''Brien'", Assert.Single(gallery.Requests));
    }

    [Fact]
    public async Task All_versions_page_and_include_prereleases()
    {
        var versions = Enumerable.Range(0, 230).Select(i => ("Lib", $"1.0.{i}")).ToList();
        var gallery = new FakeGallery(query => versions.Skip(query.Skip).Take(query.Top));

        var all = await CreateClient(gallery).GetAllVersionsAsync("Lib", CancellationToken.None);

        Assert.Equal(230, all.Count);
        Assert.Equal(3, gallery.Requests.Count);
        Assert.All(gallery.Requests, request =>
        {
            Assert.Contains("$filter=Id eq 'Lib'&", request);
            Assert.Contains("includePrerelease=true", request);
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task Transient_status_codes_are_retried(HttpStatusCode statusCode)
    {
        var gallery = new FakeGallery(Ranked(1)) { FailuresBeforeSuccess = 2, FailureStatus = statusCode };

        var top = await CreateClient(gallery, maxRetries: 3).GetTopPackagesAsync(1, CancellationToken.None);

        Assert.Single(top);
        Assert.Equal(3, gallery.Requests.Count);
    }

    [Fact]
    public async Task Network_errors_are_retried()
    {
        var gallery = new FakeGallery(Ranked(1)) { NetworkFailuresBeforeSuccess = 1 };

        Assert.Single(await CreateClient(gallery, maxRetries: 2).GetTopPackagesAsync(1, CancellationToken.None));
        Assert.Equal(2, gallery.Requests.Count);
    }

    [Fact]
    public async Task Non_transient_status_codes_fail_without_retry()
    {
        var gallery = new FakeGallery(Ranked(1)) { FailuresBeforeSuccess = 10, FailureStatus = HttpStatusCode.NotFound };

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => CreateClient(gallery, maxRetries: 3).GetTopPackagesAsync(1, CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Single(gallery.Requests);
    }

    [Fact]
    public async Task Retries_are_bounded()
    {
        var gallery = new FakeGallery(Ranked(1)) { FailuresBeforeSuccess = 10, FailureStatus = HttpStatusCode.InternalServerError };

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateClient(gallery, maxRetries: 2).GetTopPackagesAsync(1, CancellationToken.None));

        Assert.Equal(3, gallery.Requests.Count);
    }

    private static Func<Query, IEnumerable<(string Id, string Version)>> Ranked(int total)
        => query => Enumerable.Range(0, total).Select(i => ($"Pkg{i}", "1.0.0")).Skip(query.Skip).Take(query.Top);

    private static GalleryClient CreateClient(FakeGallery gallery, int maxRetries = 0)
        => new(new HttpClient(gallery),
            Options.Create(new SyncSettings
            {
                GalleryEndpoint = "https://gallery.example.test/api/v2/Search()",
                MaxRetries = maxRetries,
                RetryBaseDelayMilliseconds = 0
            }),
            NullLogger<GalleryClient>.Instance);

    private sealed record Query(int Skip, int Top);

    /// <summary>
    /// Records decoded query strings and serves an OData feed chosen by the test.
    /// </summary>
    private sealed class FakeGallery : HttpMessageHandler
    {
        private readonly Func<Query, IEnumerable<(string Id, string Version)>> _respond;

        public FakeGallery(Func<Query, IEnumerable<(string Id, string Version)>> respond) => _respond = respond;

        public List<string> Requests { get; } = new();

        public int FailuresBeforeSuccess { get; init; }

        public HttpStatusCode FailureStatus { get; init; } = HttpStatusCode.ServiceUnavailable;

        public int NetworkFailuresBeforeSuccess { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query.TrimStart('?').Replace('+', ' '));
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

            var skip = int.Parse(Regex.Match(query, @"\$skip=(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
            var top = int.Parse(Regex.Match(query, @"\$top=(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
            var feed = BuildFeed(_respond(new Query(skip, top)));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(feed, Encoding.UTF8, "application/atom+xml") });
        }

        private static string BuildFeed(IEnumerable<(string Id, string Version)> packages)
        {
            var entries = string.Concat(packages.Select(p => $"""
                <entry>
                  <m:properties>
                    <d:Id>{WebUtility.HtmlEncode(p.Id)}</d:Id>
                    <d:Version>{p.Version}</d:Version>
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
