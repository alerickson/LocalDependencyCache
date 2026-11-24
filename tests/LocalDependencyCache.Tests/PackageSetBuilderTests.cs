using LocalDependencyCache.Core;
using Microsoft.Extensions.Logging.Abstractions;
using static LocalDependencyCache.Tests.FakeGalleryClient;

namespace LocalDependencyCache.Tests;

public sealed class PackageSetBuilderTests
{
    [Fact]
    public async Task Selects_latest_top_packages_in_the_requested_count()
    {
        var gallery = new FakeGalleryClient()
            .WithTop(Package("A", "2.0.0"), Package("A", "1.0.0"))
            .WithTop(Package("B", "1.0.0"))
            .WithTop(Package("C", "1.0.0"));

        var set = await Build(gallery, top: 2);

        Assert.Equal(new[] { "A 2.0.0", "B 1.0.0" }, Names(set));
        Assert.Equal(2, set.TopPackageCount);
    }

    [Fact]
    public async Task Resolves_dependencies_transitively_using_latest_versions()
    {
        var gallery = new FakeGalleryClient()
            .WithTop(Package("App", "1.0.0", "Lib:[1.0, )"))
            .With(Package("Lib", "1.5.0", "Core:[2.0, )"), Package("Lib", "1.0.0"))
            .With(Package("Core", "2.1.0"), Package("Core", "3.0.0-preview"));

        var set = await Build(gallery);

        Assert.Equal(new[] { "App 1.0.0", "Core 2.1.0", "Lib 1.5.0" }, Names(set));
        Assert.Equal(1, set.TopPackageCount);
        Assert.Empty(set.UnresolvedDependencies);
        Assert.Empty(gallery.AllVersionsRequests);
    }

    [Fact]
    public async Task Picks_highest_older_version_when_latest_does_not_satisfy_the_range()
    {
        var gallery = new FakeGalleryClient()
            .WithTop(Package("App", "1.0.0", "Lib:[1.0, 2.0)"))
            .With(Package("Lib", "2.0.0"), Package("Lib", "1.9.0"), Package("Lib", "1.2.0"), Package("Lib", "1.10.0-beta"));

        var set = await Build(gallery);

        Assert.Contains("Lib 1.9.0", Names(set));
        Assert.DoesNotContain("Lib 2.0.0", Names(set));
        Assert.Equal(new[] { "Lib" }, gallery.AllVersionsRequests);
    }

    [Fact]
    public async Task Uses_a_prerelease_only_when_no_stable_version_satisfies()
    {
        var gallery = new FakeGalleryClient()
            .WithTop(Package("App", "1.0.0", "Next:[3.0.0-alpha, )"))
            .With(Package("Next", "2.0.0"), Package("Next", "3.0.0-beta"), Package("Next", "3.0.0-alpha"));

        Assert.Contains("Next 3.0.0-beta", Names(await Build(gallery)));
    }

    [Fact]
    public async Task Reuses_an_already_selected_version_that_satisfies_the_range()
    {
        // Lib 1.5.0 is a top package; App's range is satisfied by it, so no extra Lib version is added.
        var gallery = new FakeGalleryClient()
            .WithTop(Package("Lib", "1.5.0"))
            .WithTop(Package("App", "1.0.0", "Lib:[1.0, )"))
            .With(Package("Lib", "1.6.0-preview"));

        var set = await Build(gallery);

        Assert.Equal(new[] { "App 1.0.0", "Lib 1.5.0" }, Names(set));
        Assert.Empty(gallery.LatestRequests);
    }

    [Fact]
    public async Task Adds_a_second_version_when_ranges_need_different_versions()
    {
        var gallery = new FakeGalleryClient()
            .WithTop(Package("Lib", "2.0.0"), Package("Lib", "1.0.0"))
            .WithTop(Package("Old", "1.0.0", "Lib:[1.0, 1.0]"));

        Assert.Equal(new[] { "Lib 1.0.0", "Lib 2.0.0", "Old 1.0.0" }, Names(await Build(gallery)));
    }

    [Fact]
    public async Task Handles_dependency_cycles()
    {
        var gallery = new FakeGalleryClient()
            .WithTop(Package("A", "1.0.0", "B:[1.0, )"))
            .With(Package("B", "1.0.0", "A:[1.0, )"));

        Assert.Equal(new[] { "A 1.0.0", "B 1.0.0" }, Names(await Build(gallery)));
    }

    [Fact]
    public async Task Reports_dependencies_that_cannot_be_resolved()
    {
        var gallery = new FakeGalleryClient()
            .WithTop(Package("App", "1.0.0", "Missing:[1.0, )|Lib:[5.0, )|Any"))
            .With(Package("Lib", "1.0.0"));

        var set = await Build(gallery);

        Assert.Equal(new[] { "App 1.0.0" }, Names(set));
        Assert.Equal(new[] { "Any (any)", "Lib [5.0, )", "Missing [1.0, )" }, set.UnresolvedDependencies);
    }

    [Fact]
    public async Task Dependencies_can_be_excluded()
    {
        var gallery = new FakeGalleryClient()
            .WithTop(Package("App", "1.0.0", "Lib:[1.0, )"))
            .With(Package("Lib", "1.0.0"));

        var set = await new PackageSetBuilder(gallery, NullLogger.Instance).BuildAsync(100, includeDependencies: false, CancellationToken.None);

        Assert.Equal(new[] { "App 1.0.0" }, Names(set));
        Assert.Empty(gallery.LatestRequests);
    }

    [Fact]
    public void Content_hash_ignores_order_and_detects_changes()
    {
        var a = Package("A", "1.0.0", "B:[1.0, )", "x", "y");
        var b = Package("B", "1.0.0");

        var hash = PackageSetBuilder.ComputeContentHash(new[] { a, b });

        Assert.Equal(hash, PackageSetBuilder.ComputeContentHash(new[] { b, Package("A", "1.0.0", "B:[1.0, )", "y", "x") }));
        Assert.NotEqual(hash, PackageSetBuilder.ComputeContentHash(new[] { a }));
        Assert.NotEqual(hash, PackageSetBuilder.ComputeContentHash(new[] { Package("A", "1.0.0", "B:[1.0, )", "x"), b }));
        Assert.NotEqual(hash, PackageSetBuilder.ComputeContentHash(new[] { Package("A", "1.0.0", "B:[2.0, )", "x", "y"), b }));
        Assert.NotEqual(hash, PackageSetBuilder.ComputeContentHash(new[] { a, Package("B", "1.0.1") }));
    }

    private static Task<PackageSet> Build(FakeGalleryClient gallery, int top = 100)
        => new PackageSetBuilder(gallery, NullLogger.Instance).BuildAsync(top, includeDependencies: true, CancellationToken.None);

    private static string[] Names(PackageSet set)
        => set.Packages.Select(p => $"{p.Id} {p.Version}").ToArray();
}
