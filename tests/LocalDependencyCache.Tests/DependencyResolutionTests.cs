using LocalDependencyCache.Core;
using LocalDependencyCache.Query;
using NuGet.Versioning;

namespace LocalDependencyCache.Tests;

public sealed class DependencyResolutionTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "LocalDependencyCache.Tests", Guid.NewGuid().ToString("N"));
    private readonly SqlitePackageQueryClient _client;

    public DependencyResolutionTests()
    {
        var path = Path.Combine(_rootPath, "cache.sqlite");
        var cache = new SqlitePackageCache(path);
        cache.Initialize();
        cache.UpsertPackages(new[]
        {
            Package("Az.Accounts", "5.5.4"),
            Package("Az.Accounts", "5.5.5"),
            Package("Az.Accounts", "5.6.0"),
            Package("Az.Accounts", "6.0.0-preview"),
            Package("Az.Storage", "1.9.0"),
            Package("Az.Storage", "1.10.0"),
            Package("Az.Storage", "1.10.0-beta"),
            Package("Az.Storage", "2.0.0"),
            Package("Az.Compute", "1.0.0",
                "Az.Accounts:[5.5.5, ):|Az.Storage:[1.10.0, 1.10.0]:|EntraIDAccessToken:(, ):|Framework.Dep:[1.0, ):net45")
        });
        _client = new SqlitePackageQueryClient(path);
    }

    [Theory]
    [InlineData("", null, false, null, false)]
    [InlineData("*", null, false, null, false)]
    [InlineData("(, )", null, false, null, false)]
    [InlineData("[1.0.0, )", "1.0.0", true, null, false)]
    [InlineData("1.0", "1.0.0", true, null, false)]
    [InlineData("[1.0, 2.0)", "1.0.0", true, "2.0.0", false)]
    [InlineData("(1.0, 2.0]", "1.0.0", false, "2.0.0", true)]
    [InlineData("(, 2.0]", null, false, "2.0.0", true)]
    [InlineData("[1.2.3]", "1.2.3", true, "1.2.3", true)]
    [InlineData("[1.2.3, 1.2.3]", "1.2.3", true, "1.2.3", true)]
    public void Range_parser_handles_gallery_and_nuget_forms(string raw, string? min, bool includeMin, string? max, bool includeMax)
    {
        Assert.True(VersionRangeParser.TryParse(raw, out var range));
        Assert.Equal(min, range.MinVersion?.ToNormalizedString());
        Assert.Equal(max, range.MaxVersion?.ToNormalizedString());
        if (min is not null)
        {
            Assert.Equal(includeMin, range.IsMinInclusive);
        }

        if (max is not null)
        {
            Assert.Equal(includeMax, range.IsMaxInclusive);
        }
    }

    [Theory]
    [InlineData("not-a-range")]
    [InlineData("[2.0, 1.0]")]
    [InlineData("[1.0")]
    public void Range_parser_rejects_invalid_ranges(string raw)
    {
        Assert.False(VersionRangeParser.TryParse(raw, out _));
        Assert.Null(NuGetRangeParser.Parse(raw));
    }

    [Theory]
    [InlineData("[1.0, 2.0)", "1.0", true)]
    [InlineData("[1.0, 2.0)", "1.9.9", true)]
    [InlineData("[1.0, 2.0)", "2.0.0", false)]
    [InlineData("(1.0, 2.0]", "1.0.0", false)]
    [InlineData("(1.0, 2.0]", "2.0", true)]
    [InlineData("[1.2.3]", "1.2.3.0", true)]
    [InlineData("1.10", "1.9", false)]
    public void Range_satisfaction_uses_semantic_version_rules(string raw, string version, bool expected)
    {
        Assert.True(VersionRangeParser.TryParse(raw, out var range));
        Assert.Equal(expected, range.Satisfies(NuGetVersion.Parse(version)));
    }

    [Fact]
    public void Dependency_entries_parse_gallery_format()
    {
        var entries = DependencyEntry.ParseMany("A:[1.0, ):|B:(, ):|C:[2.0, 2.0]:|D:[3.0, ):net45").ToList();

        Assert.Equal(new[] { "A", "B", "C", "D" }, entries.Select(entry => entry.Id));
        Assert.True(entries[1].IsAny);
        Assert.True(entries[2].IsExact);
        Assert.Equal("[3.0, )", entries[3].RawVersionRange);
        Assert.Equal("3.0.0", entries[3].MinVersion);
        Assert.False(entries[3].IsAny);
    }

    [Fact]
    public void Search_results_are_ordered_by_semantic_version()
    {
        Assert.Equal(new[] { "1.9.0", "1.10.0-beta", "1.10.0", "2.0.0" },
            _client.FindByPackageId("Az.Storage").Select(match => match.Version));
    }

    [Fact]
    public void GetDependencies_returns_declared_dependencies_and_ranges()
    {
        var dependencies = _client.GetDependencies("az.compute", "1.0.0");

        Assert.Equal(new[]
        {
            new PackageDependency("Az.Accounts", "[5.5.5, )"),
            new PackageDependency("Az.Storage", "[1.10.0, 1.10.0]"),
            new PackageDependency("EntraIDAccessToken", "(, )"),
            new PackageDependency("Framework.Dep", "[1.0, )")
        }, dependencies);
        Assert.Empty(_client.GetDependencies("Az.Accounts", "5.5.5"));
        Assert.Empty(_client.GetDependencies("Az.Compute", "9.9.9"));
    }

    [Fact]
    public void FindVersionsSatisfying_applies_range_and_prerelease_filter()
    {
        Assert.Equal(new[] { "5.5.5", "5.6.0" },
            _client.FindVersionsSatisfying("Az.Accounts", "[5.5.5, )").Select(match => match.Version));
        Assert.Equal(new[] { "5.5.5", "5.6.0", "6.0.0-preview" },
            _client.FindVersionsSatisfying("Az.Accounts", "[5.5.5, )", includePrerelease: true).Select(match => match.Version));
        Assert.Equal(new[] { "5.5.4", "5.5.5", "5.6.0" },
            _client.FindVersionsSatisfying("Az.Accounts", "(, )").Select(match => match.Version));
        Assert.Equal(new[] { "5.5.4", "5.5.5", "5.6.0" },
            _client.FindVersionsSatisfying("Az.Accounts", null).Select(match => match.Version));
        Assert.Empty(_client.FindVersionsSatisfying("Az.Accounts", "[7.0, )"));
        Assert.Empty(_client.FindVersionsSatisfying("Missing.Module", "[1.0, )"));
    }

    [Fact]
    public void FindLatestVersionSatisfying_picks_highest_match()
    {
        Assert.Equal("5.6.0", _client.FindLatestVersionSatisfying("Az.Accounts", "[5.5.5, )")?.Version);
        Assert.Equal("6.0.0-preview", _client.FindLatestVersionSatisfying("Az.Accounts", "[5.5.5, )", includePrerelease: true)?.Version);
        Assert.Equal("1.10.0", _client.FindLatestVersionSatisfying("Az.Storage", "[1.10.0, 1.10.0]")?.Version);
        Assert.Equal("2.0.0", _client.FindLatestVersionSatisfying("Az.Storage", "1.10")?.Version);
        Assert.Null(_client.FindLatestVersionSatisfying("Az.Storage", "[3.0, )"));
    }

    [Fact]
    public void Dependencies_resolve_end_to_end()
    {
        var resolved = _client.GetDependencies("Az.Compute", "1.0.0")
            .ToDictionary(dependency => dependency.Id,
                dependency => _client.FindLatestVersionSatisfying(dependency.Id, dependency.VersionRange)?.Version);

        Assert.Equal("5.6.0", resolved["Az.Accounts"]);
        Assert.Equal("1.10.0", resolved["Az.Storage"]);
        Assert.Null(resolved["EntraIDAccessToken"]);
    }

    [Fact]
    public void Dependency_queries_reject_invalid_arguments()
    {
        Assert.Throws<ArgumentException>(() => _client.GetDependencies("", "1.0.0"));
        Assert.Throws<ArgumentException>(() => _client.GetDependencies("Az.Compute", " "));
        Assert.Throws<ArgumentException>(() => _client.FindVersionsSatisfying("", "[1.0, )"));
        Assert.Throws<ArgumentException>(() => _client.FindVersionsSatisfying("Az.Accounts", "not-a-range"));
    }

    private static PackageEntry Package(string id, string version, string? dependencies = null)
        => PackageEntry.Create(id, version, version, null, null, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, version.Contains('-'), Array.Empty<string>(), dependencies);

    public void Dispose()
        => Directory.Delete(_rootPath, recursive: true);
}
