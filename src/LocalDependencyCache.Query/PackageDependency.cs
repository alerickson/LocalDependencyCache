namespace LocalDependencyCache.Query;

/// <summary>
/// A dependency declared by a cached package version. <see cref="VersionRange"/> is the raw
/// NuGet range from the Gallery; an empty string means any version.
/// </summary>
public sealed record PackageDependency(string Id, string VersionRange);
