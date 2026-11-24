using Microsoft.Data.Sqlite;
using LocalDependencyCache.Query;

namespace LocalDependencyCache.Core;

public sealed class SqlitePackageCache
{
    public const string LastPublishedStateKey = "last_published_utc";

    private readonly string _databasePath;

    public SqlitePackageCache(string databasePath)
    {
        _databasePath = databasePath;
    }

    public string DatabasePath => _databasePath;

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath) ?? ".");

        using var connection = OpenConnection();

        ExecuteNonQuery(connection, "PRAGMA foreign_keys = ON;");
        EnsureSchemaMigration(connection);
        ExecuteNonQuery(connection, "PRAGMA foreign_keys = ON;");
        ExecuteNonQuery(connection, "DROP INDEX IF EXISTS idx_packages_id;");
        ExecuteNonQuery(connection, "DROP INDEX IF EXISTS idx_package_ids_id;");
        ExecuteNonQuery(connection, "DROP INDEX IF EXISTS idx_packages_package_id_key;");
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS package_ids (
                package_id_key         INTEGER PRIMARY KEY,
                package_id             TEXT    NOT NULL COLLATE NOCASE,
                UNIQUE (package_id)
            );
            """);
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS packages (
                package_key            INTEGER PRIMARY KEY,
                package_id_key         INTEGER NOT NULL,
                version                TEXT    NOT NULL,
                normalized_version     TEXT    NULL,
                published_unix         INTEGER NULL,
                updated_unix           INTEGER NULL,
                is_prerelease          INTEGER NOT NULL DEFAULT 0 CHECK (is_prerelease IN (0,1)),
                UNIQUE (package_id_key, version),
                FOREIGN KEY (package_id_key) REFERENCES package_ids(package_id_key)
            );
            """);
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS tags (
                tag_key                INTEGER PRIMARY KEY,
                tag_value              TEXT    NOT NULL COLLATE NOCASE,
                is_command_tag         INTEGER NOT NULL DEFAULT 0 CHECK (is_command_tag IN (0,1)),
                command_name           TEXT    NULL,
                UNIQUE (tag_value)
            );
            """);
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS package_tags (
                package_key            INTEGER NOT NULL,
                tag_key                INTEGER NOT NULL,
                PRIMARY KEY (package_key, tag_key),
                FOREIGN KEY (package_key) REFERENCES packages(package_key) ON DELETE CASCADE,
                FOREIGN KEY (tag_key) REFERENCES tags(tag_key) ON DELETE CASCADE
            ) WITHOUT ROWID;
            """);
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS dependencies (
                package_key            INTEGER NOT NULL,
                dependency_id          TEXT    NOT NULL COLLATE NOCASE,
                version_range_raw      TEXT    NULL,
                min_version            TEXT    NULL,
                max_version            TEXT    NULL,
                include_min            INTEGER NOT NULL DEFAULT 0 CHECK (include_min IN (0,1)),
                include_max            INTEGER NOT NULL DEFAULT 0 CHECK (include_max IN (0,1)),
                is_exact               INTEGER NOT NULL DEFAULT 0 CHECK (is_exact IN (0,1)),
                is_any                 INTEGER NOT NULL DEFAULT 0 CHECK (is_any IN (0,1)),
                dependency_group       TEXT    NULL,
                PRIMARY KEY (package_key, dependency_id, version_range_raw),
                FOREIGN KEY (package_key) REFERENCES packages(package_key) ON DELETE CASCADE
            ) WITHOUT ROWID;
            """);
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS sync_state (
                state_key              TEXT PRIMARY KEY,
                state_value            TEXT NOT NULL
            ) WITHOUT ROWID;
            """);

        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_package_tags_tag_key ON package_tags(tag_key, package_key);");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_tags_command_name ON tags(command_name) WHERE is_command_tag = 1;");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_dependencies_dep_id ON dependencies(dependency_id);");
    }

    public DateTimeOffset? GetCheckpoint()
    {
        if (!File.Exists(_databasePath))
        {
            return null;
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state_value FROM sync_state WHERE state_key = $stateKey LIMIT 1;";
        command.Parameters.AddWithValue("$stateKey", LastPublishedStateKey);

        var value = command.ExecuteScalar() as string;
        return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
    }

    public string? GetState(string key)
    {
        if (!File.Exists(_databasePath))
        {
            return null;
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state_value FROM sync_state WHERE state_key = $stateKey LIMIT 1;";
        command.Parameters.AddWithValue("$stateKey", key);
        return command.ExecuteScalar() as string;
    }

    public void DeleteState(string key)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sync_state WHERE state_key = $key;";
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    public void SetState(string key, string value)
    {
        using var connection = OpenConnection();
        SetState(connection, null, key, value);
    }

    private static void SetState(SqliteConnection connection, SqliteTransaction? transaction, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sync_state (state_key, state_value)
            VALUES ($key, $value)
            ON CONFLICT(state_key) DO UPDATE SET state_value = excluded.state_value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Upserts a batch of packages. <paramref name="state"/> entries (such as the sync checkpoint) are
    /// written in the same transaction so the checkpoint never gets ahead of the stored data.
    /// Returns how many package versions were new or had changed metadata.
    /// </summary>
    public int UpsertPackages(IReadOnlyList<PackageEntry> packages, IReadOnlyDictionary<string, string>? state = null)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var changed = 0;

        foreach (var package in packages)
        {
            if (UpsertPackage(connection, transaction, package, out var packageKey))
            {
                changed++;
            }

            DeletePackageChildren(connection, transaction, packageKey);
            UpsertTags(connection, transaction, packageKey, package.Tags);
            InsertDependencies(connection, transaction, packageKey, package.Dependencies);
        }

        foreach (var (key, value) in state ?? new Dictionary<string, string>())
        {
            SetState(connection, transaction, key, value);
        }

        transaction.Commit();
        return changed;
    }

    public void Compact()
    {
        using var connection = OpenConnection();
        ExecuteNonQuery(connection, "PRAGMA optimize;");
        ExecuteNonQuery(connection, "VACUUM;");
    }

    public IReadOnlyList<PackageIdentity> FindByCommandName(string commandName)
        => new SqlitePackageQueryClient(_databasePath).FindByCommandName(commandName);

    private SqliteConnection OpenConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = false
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        ExecuteNonQuery(connection, "PRAGMA foreign_keys = ON;");
        return connection;
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static bool UpsertPackage(SqliteConnection connection, SqliteTransaction transaction, PackageEntry package, out long packageKey)
    {
        var packageIdKey = GetOrCreatePackageIdKey(connection, transaction, package.Id);
        bool changed;

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            // The WHERE clause makes an identical re-read report zero changed rows.
            command.CommandText = """
                INSERT INTO packages (
                    package_id_key, version, normalized_version,
                    published_unix, updated_unix, is_prerelease
                ) VALUES (
                    $packageIdKey, $version, $normalizedVersion,
                    $publishedUnix, $updatedUnix, $isPrerelease
                )
                ON CONFLICT(package_id_key, version) DO UPDATE SET
                    normalized_version = excluded.normalized_version,
                    published_unix = excluded.published_unix,
                    updated_unix = excluded.updated_unix,
                    is_prerelease = excluded.is_prerelease
                WHERE normalized_version IS NOT excluded.normalized_version
                    OR published_unix IS NOT excluded.published_unix
                    OR updated_unix IS NOT excluded.updated_unix
                    OR is_prerelease IS NOT excluded.is_prerelease;
                """;
            command.Parameters.AddWithValue("$packageIdKey", packageIdKey);
            command.Parameters.AddWithValue("$version", package.Version);
            command.Parameters.AddWithValue("$normalizedVersion", (object?)package.NormalizedVersion ?? DBNull.Value);
            command.Parameters.AddWithValue("$publishedUnix", package.PublishedUtc?.ToUnixTimeSeconds() is long publishedUnix ? publishedUnix : DBNull.Value);
            command.Parameters.AddWithValue("$updatedUnix", package.UpdatedUtc?.ToUnixTimeSeconds() is long updatedUnix ? updatedUnix : DBNull.Value);
            command.Parameters.AddWithValue("$isPrerelease", package.IsPrerelease ? 1 : 0);
            changed = command.ExecuteNonQuery() > 0;
        }

        using var lookup = connection.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = "SELECT package_key FROM packages WHERE package_id_key = $packageIdKey AND version = $version LIMIT 1;";
        lookup.Parameters.AddWithValue("$packageIdKey", packageIdKey);
        lookup.Parameters.AddWithValue("$version", package.Version);
        packageKey = (long)(lookup.ExecuteScalar() ?? throw new InvalidOperationException("Unable to locate package key."));
        return changed;
    }

    private static long GetOrCreatePackageIdKey(SqliteConnection connection, SqliteTransaction transaction, string packageId)
    {
        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO package_ids (package_id)
                VALUES ($packageId)
                ON CONFLICT(package_id) DO NOTHING;
                """;
            upsert.Parameters.AddWithValue("$packageId", packageId);
            upsert.ExecuteNonQuery();
        }

        using var lookup = connection.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = "SELECT package_id_key FROM package_ids WHERE package_id = $packageId LIMIT 1;";
        lookup.Parameters.AddWithValue("$packageId", packageId);
        return (long)(lookup.ExecuteScalar() ?? throw new InvalidOperationException("Unable to locate package id key."));
    }

    private static void EnsureSchemaMigration(SqliteConnection connection)
    {
        if (!TableExists(connection, "packages"))
        {
            return;
        }

        if (ColumnExists(connection, "packages", "package_id_key"))
        {
            return;
        }

        // Breaking schema change: reset old layout and let Initialize recreate compact schema.
        ExecuteNonQuery(connection, "PRAGMA foreign_keys = OFF;");
        ExecuteNonQuery(connection, "DROP TABLE IF EXISTS package_tags;");
        ExecuteNonQuery(connection, "DROP TABLE IF EXISTS dependencies;");
        ExecuteNonQuery(connection, "DROP TABLE IF EXISTS tags;");
        ExecuteNonQuery(connection, "DROP TABLE IF EXISTS packages;");
        ExecuteNonQuery(connection, "DROP TABLE IF EXISTS package_ids;");
        ExecuteNonQuery(connection, "DROP TABLE IF EXISTS sync_state;");
        ExecuteNonQuery(connection, "PRAGMA foreign_keys = ON;");
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);
        return command.ExecuteScalar() is not null;
    }

    private static bool ColumnExists(SqliteConnection connection, string tableName, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void DeletePackageChildren(SqliteConnection connection, SqliteTransaction transaction, long packageKey)
    {
        using var deleteTags = connection.CreateCommand();
        deleteTags.Transaction = transaction;
        deleteTags.CommandText = "DELETE FROM package_tags WHERE package_key = $packageKey;";
        deleteTags.Parameters.AddWithValue("$packageKey", packageKey);
        deleteTags.ExecuteNonQuery();

        using var deleteDependencies = connection.CreateCommand();
        deleteDependencies.Transaction = transaction;
        deleteDependencies.CommandText = "DELETE FROM dependencies WHERE package_key = $packageKey;";
        deleteDependencies.Parameters.AddWithValue("$packageKey", packageKey);
        deleteDependencies.ExecuteNonQuery();
    }

    private static void UpsertTags(SqliteConnection connection, SqliteTransaction transaction, long packageKey, IReadOnlyList<string> tags)
    {
        foreach (var tag in tags)
        {
            var isCommandTag = tag.StartsWith("PSCommand_", StringComparison.OrdinalIgnoreCase);
            var commandName = isCommandTag ? tag["PSCommand_".Length..] : null;

            using (var upsertTag = connection.CreateCommand())
            {
                upsertTag.Transaction = transaction;
                upsertTag.CommandText = """
                    INSERT INTO tags (tag_value, is_command_tag, command_name)
                    VALUES ($tagValue, $isCommandTag, $commandName)
                    ON CONFLICT(tag_value) DO UPDATE SET
                        is_command_tag = excluded.is_command_tag,
                        command_name = excluded.command_name;
                    """;
                upsertTag.Parameters.AddWithValue("$tagValue", tag);
                upsertTag.Parameters.AddWithValue("$isCommandTag", isCommandTag ? 1 : 0);
                upsertTag.Parameters.AddWithValue("$commandName", (object?)commandName ?? DBNull.Value);
                upsertTag.ExecuteNonQuery();
            }

            long tagKey;
            using (var lookupTag = connection.CreateCommand())
            {
                lookupTag.Transaction = transaction;
                lookupTag.CommandText = "SELECT tag_key FROM tags WHERE tag_value = $tagValue LIMIT 1;";
                lookupTag.Parameters.AddWithValue("$tagValue", tag);
                tagKey = (long)(lookupTag.ExecuteScalar() ?? throw new InvalidOperationException("Unable to locate tag key."));
            }

            using var linkTag = connection.CreateCommand();
            linkTag.Transaction = transaction;
            linkTag.CommandText = "INSERT OR IGNORE INTO package_tags (package_key, tag_key) VALUES ($packageKey, $tagKey);";
            linkTag.Parameters.AddWithValue("$packageKey", packageKey);
            linkTag.Parameters.AddWithValue("$tagKey", tagKey);
            linkTag.ExecuteNonQuery();
        }
    }

    private static void InsertDependencies(SqliteConnection connection, SqliteTransaction transaction, long packageKey, IReadOnlyList<DependencyEntry> dependencies)
    {
        foreach (var dependency in dependencies)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR REPLACE INTO dependencies (
                    package_key, dependency_id, version_range_raw, min_version, max_version,
                    include_min, include_max, is_exact, is_any, dependency_group
                ) VALUES (
                    $packageKey, $dependencyId, $versionRangeRaw, $minVersion, $maxVersion,
                    $includeMin, $includeMax, $isExact, $isAny, $dependencyGroup
                );
                """;
            command.Parameters.AddWithValue("$packageKey", packageKey);
            command.Parameters.AddWithValue("$dependencyId", dependency.Id);
            // In WITHOUT ROWID tables, PK columns are effectively NOT NULL.
            // Preserve "any version" as empty string rather than NULL.
            command.Parameters.AddWithValue("$versionRangeRaw", dependency.RawVersionRange ?? string.Empty);
            command.Parameters.AddWithValue("$minVersion", (object?)dependency.MinVersion ?? DBNull.Value);
            command.Parameters.AddWithValue("$maxVersion", (object?)dependency.MaxVersion ?? DBNull.Value);
            command.Parameters.AddWithValue("$includeMin", dependency.IncludeMin ? 1 : 0);
            command.Parameters.AddWithValue("$includeMax", dependency.IncludeMax ? 1 : 0);
            command.Parameters.AddWithValue("$isExact", dependency.IsExact ? 1 : 0);
            command.Parameters.AddWithValue("$isAny", dependency.IsAny ? 1 : 0);
            command.Parameters.AddWithValue("$dependencyGroup", DBNull.Value);
            command.ExecuteNonQuery();
        }
    }
}