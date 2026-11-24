using Microsoft.Data.Sqlite;
using NuGet.Versioning;

namespace LocalDependencyCache.Query;

public sealed class SqlitePackageQueryClient
{
    private readonly string _databasePath;

    public SqlitePackageQueryClient(string databasePath)
    {
        _databasePath = databasePath;
    }

    public IReadOnlyList<PackageIdentity> FindByCommandName(string commandName)
    {
        Guard.NotNullOrWhiteSpace(commandName);
        return FindByTag("PSCommand_" + commandName);
    }

    public IReadOnlyList<PackageIdentity> FindByDscResourceName(string resourceName)
    {
        Guard.NotNullOrWhiteSpace(resourceName);
        return FindByTag("PSDscResource_" + resourceName);
    }

    public IReadOnlyList<PackageIdentity> FindByTag(string tag)
    {
        Guard.NotNullOrWhiteSpace(tag);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT pi.package_id, p.version
            FROM tags t
            JOIN package_tags pt ON pt.tag_key = t.tag_key
            JOIN packages p ON p.package_key = pt.package_key
            JOIN package_ids pi ON pi.package_id_key = p.package_id_key
            WHERE t.tag_value = $tag
            ORDER BY pi.package_id, p.version;
            """;
        command.Parameters.AddWithValue("$tag", tag);

        return ReadIdentities(command);
    }

    public IReadOnlyList<PackageIdentity> FindByPackageId(string packageId)
        => FindByPackageIdCore(packageId, null);

    public IReadOnlyList<PackageIdentity> FindByPackageIdAndVersion(string packageId, string version)
    {
        Guard.NotNullOrWhiteSpace(version);
        return FindByPackageIdCore(packageId, version);
    }

    private IReadOnlyList<PackageIdentity> FindByPackageIdCore(string packageId, string? version)
    {
        Guard.NotNullOrWhiteSpace(packageId);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT pi.package_id, p.version
            FROM package_ids pi
            JOIN packages p ON p.package_id_key = pi.package_id_key
            WHERE pi.package_id = $packageId
            """;
        command.Parameters.AddWithValue("$packageId", packageId);
        if (version is not null)
        {
            command.CommandText += " AND p.version = $version";
            command.Parameters.AddWithValue("$version", version);
        }

        command.CommandText += " ORDER BY pi.package_id, p.version;";
        return ReadIdentities(command);
    }

    public IReadOnlyList<PackageIdentity> SearchByPackageIdPrefix(string packageIdPrefix, int maxResults = 100)
    {
        Guard.NotNull(packageIdPrefix);
        Guard.Positive(maxResults);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT pi.package_id, p.version
            FROM packages p
            JOIN package_ids pi ON pi.package_id_key = p.package_id_key
            WHERE pi.package_id LIKE $prefix || '%' ESCAPE '\'
            ORDER BY pi.package_id, p.version
            LIMIT $maxResults;
            """;
        command.Parameters.AddWithValue("$prefix", packageIdPrefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_"));
        command.Parameters.AddWithValue("$maxResults", maxResults);

        return ReadIdentities(command);
    }

    /// <summary>
    /// Returns the dependencies declared by an exact cached package version.
    /// Returns an empty list when the version has no dependencies or is not cached.
    /// </summary>
    public IReadOnlyList<PackageDependency> GetDependencies(string packageId, string version)
    {
        Guard.NotNullOrWhiteSpace(packageId);
        Guard.NotNullOrWhiteSpace(version);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.dependency_id, d.version_range_raw
            FROM package_ids pi
            JOIN packages p ON p.package_id_key = pi.package_id_key
            JOIN dependencies d ON d.package_key = p.package_key
            WHERE pi.package_id = $packageId AND p.version = $version
            ORDER BY d.dependency_id;
            """;
        command.Parameters.AddWithValue("$packageId", packageId);
        command.Parameters.AddWithValue("$version", version);

        using var reader = command.ExecuteReader();
        var result = new List<PackageDependency>();
        while (reader.Read())
        {
            result.Add(new PackageDependency(reader.GetString(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1)));
        }

        return result;
    }

    /// <summary>
    /// Returns cached versions of <paramref name="packageId"/> that satisfy a NuGet version range,
    /// ordered from lowest to highest. An empty or null range matches any version.
    /// </summary>
    public IReadOnlyList<PackageIdentity> FindVersionsSatisfying(string packageId, string? versionRange, bool includePrerelease = false)
    {
        Guard.NotNullOrWhiteSpace(packageId);
        if (!VersionRangeParser.TryParse(versionRange, out var range))
        {
            throw new ArgumentException($"'{versionRange}' is not a valid NuGet version range.", nameof(versionRange));
        }

        return FindByPackageId(packageId)
            .Where(candidate => NuGetVersion.TryParse(candidate.Version, out var version)
                && (includePrerelease || !version.IsPrerelease)
                && range.Satisfies(version))
            .ToList();
    }

    /// <summary>
    /// Returns the highest cached version that satisfies the range, or null when none does.
    /// </summary>
    public PackageIdentity? FindLatestVersionSatisfying(string packageId, string? versionRange, bool includePrerelease = false)
        => FindVersionsSatisfying(packageId, versionRange, includePrerelease).LastOrDefault();

    private static IReadOnlyList<PackageIdentity> ReadIdentities(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var result = new List<PackageIdentity>();
        while (reader.Read())
        {
            result.Add(new PackageIdentity(reader.GetString(0), reader.GetString(1)));
        }

        return result
            .OrderBy(identity => identity.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(identity => identity.Version, VersionStringComparer.Instance)
            .ToList();
    }

    private SqliteConnection OpenConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}
