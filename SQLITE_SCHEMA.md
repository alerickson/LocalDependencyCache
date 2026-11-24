# SQLite Schema for LocalDependencyCache

## Purpose

The Azure Function (or local `--run-once` process) generates this SQLite cache
from PowerShell Gallery metadata. Consumers download and extract the published
ZIP, then search locally using `LocalDependencyCache.Query`.

This document describes the schema implemented by
`src\LocalDependencyCache.Core\SqlitePackageCache.cs`, not a proposed schema.

## Search capabilities

The shared query client supports all five search modes:

- **Name:** `FindByPackageId(name)` returns every cached version of that exact name.
- **Name + version:** `FindByPackageIdAndVersion(name, version)` matches the stored version string exactly.
- **Tag:** `FindByTag(tag)` matches an entire tag.
- **DSC resource:** `FindByDscResourceName(name)` matches `PSDscResource_<name>`.
- **Command name:** `FindByCommandName(name)` matches `PSCommand_<name>`.

Names and tags use SQLite `NOCASE` (ASCII case-insensitive comparison). Versions
use case-sensitive text equality; version ranges and equivalent version spellings
are not resolved by these search methods. Results contain package name and
version, ordered by name and then NuGet semantic-version precedence (applied in
the client after the SQL query). All cached versions, including prereleases,
are eligible.

Dependency resolution is provided by `GetDependencies(name, version)`,
`FindVersionsSatisfying(name, range, includePrerelease)`, and
`FindLatestVersionSatisfying(name, range, includePrerelease)`. See
[Dependency resolution](#dependency-resolution).

The existing `SearchByPackageIdPrefix(prefix, maxResults = 100)` remains available.
It treats `%`, `_`, and backslash in the prefix literally, not as SQL patterns.
Exact searches do not support wildcard patterns or multiple-tag AND/OR expressions.
SQL parameters are used for every search value. Connections are read-only.

```csharp
var client = new SqlitePackageQueryClient(@".artifacts\psgallery-cache.sqlite");
var names = client.FindByPackageId("Sample.Module");
var version = client.FindByPackageIdAndVersion("Sample.Module", "1.2.3");
var tags = client.FindByTag("Azure");
var resources = client.FindByDscResourceName("File");
var commands = client.FindByCommandName("Get-Sample");
```

## Stored metadata

Package names are stored once in `package_ids`, independently of their versions.
All nonempty Gallery tags are retained, trimmed, and deduplicated
case-insensitively. A shared tag dictionary avoids repeating tag strings for
each package version.

Only identity/version data, publication/update timestamps, prerelease status,
tags, dependency constraints, and sync state are persisted. Descriptions,
titles, authors, owners, URLs, download counts, hashes, and file lists are not
stored in SQLite. Some descriptive fields still exist in the ingestion model.

## DDL

```sql
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS package_ids (
    package_id_key INTEGER PRIMARY KEY,
    package_id TEXT NOT NULL COLLATE NOCASE,
    UNIQUE (package_id)
);

CREATE TABLE IF NOT EXISTS packages (
    package_key INTEGER PRIMARY KEY,
    package_id_key INTEGER NOT NULL,
    version TEXT NOT NULL,
    normalized_version TEXT NULL,
    published_unix INTEGER NULL,
    updated_unix INTEGER NULL,
    is_prerelease INTEGER NOT NULL DEFAULT 0 CHECK (is_prerelease IN (0,1)),
    UNIQUE (package_id_key, version),
    FOREIGN KEY (package_id_key) REFERENCES package_ids(package_id_key)
);

CREATE TABLE IF NOT EXISTS tags (
    tag_key INTEGER PRIMARY KEY,
    tag_value TEXT NOT NULL COLLATE NOCASE,
    is_command_tag INTEGER NOT NULL DEFAULT 0 CHECK (is_command_tag IN (0,1)),
    command_name TEXT NULL,
    UNIQUE (tag_value)
);

CREATE TABLE IF NOT EXISTS package_tags (
    package_key INTEGER NOT NULL,
    tag_key INTEGER NOT NULL,
    PRIMARY KEY (package_key, tag_key),
    FOREIGN KEY (package_key) REFERENCES packages(package_key) ON DELETE CASCADE,
    FOREIGN KEY (tag_key) REFERENCES tags(tag_key) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS dependencies (
    package_key INTEGER NOT NULL,
    dependency_id TEXT NOT NULL COLLATE NOCASE,
    version_range_raw TEXT NULL,
    min_version TEXT NULL,
    max_version TEXT NULL,
    include_min INTEGER NOT NULL DEFAULT 0 CHECK (include_min IN (0,1)),
    include_max INTEGER NOT NULL DEFAULT 0 CHECK (include_max IN (0,1)),
    is_exact INTEGER NOT NULL DEFAULT 0 CHECK (is_exact IN (0,1)),
    is_any INTEGER NOT NULL DEFAULT 0 CHECK (is_any IN (0,1)),
    dependency_group TEXT NULL,
    PRIMARY KEY (package_key, dependency_id, version_range_raw),
    FOREIGN KEY (package_key) REFERENCES packages(package_key) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS sync_state (
    state_key TEXT PRIMARY KEY,
    state_value TEXT NOT NULL
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS idx_package_tags_tag_key
    ON package_tags(tag_key, package_key);
CREATE INDEX IF NOT EXISTS idx_tags_command_name
    ON tags(command_name) WHERE is_command_tag = 1;
CREATE INDEX IF NOT EXISTS idx_dependencies_dep_id
    ON dependencies(dependency_id);
```

In a `WITHOUT ROWID` table, primary-key columns are implicitly `NOT NULL`.
The writer therefore stores an unconstrained dependency's raw range as an empty
string, not `NULL`. `dependency_group` is currently always `NULL`.

## Query paths and indexes

The unique constraint on `package_ids.package_id` provides the name index.
The unique constraint on `(package_id_key, version)` provides both the
package-version lookup and the package-to-versions lookup; separate indexes
on their leading columns are unnecessary.

Tag, command, and DSC searches share this query, with `$tag` set to the
ordinary tag or the appropriate prefixed tag:

```sql
SELECT DISTINCT pi.package_id, p.version
FROM tags t
JOIN package_tags pt ON pt.tag_key = t.tag_key
JOIN packages p ON p.package_key = pt.package_key
JOIN package_ids pi ON pi.package_id_key = p.package_id_key
WHERE t.tag_value = $tag
ORDER BY pi.package_id, p.version;
```

The unique tag index finds the tag, and `idx_package_tags_tag_key` finds its
package versions without scanning the entire mapping table. The mapping's
primary key supports the reverse direction during package updates.

The legacy `is_command_tag`, `command_name`, and command-name index are retained
for schema compatibility; shared searches now use `tag_value` instead.

## Updates, sync state, and existing caches

Each ingestion batch upserts packages by name and version in one transaction.
Existing tag mappings and dependencies for each refreshed version are replaced.
The tag dictionary can retain unreferenced tags; compaction does not delete them.

The sync service records `last_published_utc` and `last_success_utc` in
`sync_state`. It uses the publication checkpoint with an overlap window for
subsequent runs. Dates in package rows are Unix seconds.

Initialization removes redundant `idx_package_ids_id` and
`idx_packages_package_id_key` indexes and adds the reverse tag-mapping index.
Existing compact-schema rows are preserved. The older schema with package
names directly in `packages` is reset by the existing migration code.

**Older caches have discarded ordinary tags.** A full metadata refresh is
required to recover them for older package versions. Set
`PackageCache__FullSyncFromUtc` to a date preceding the desired catalog history
for one sync, then clear it to resume incremental updates. This re-fetches and
upserts data; it does not clear an existing compact cache or remove packages
that no longer appear in the Gallery.

## Size tradeoffs and distribution

Names and tags are deduplicated; joins use integer keys; dense mapping,
dependency, and state tables use `WITHOUT ROWID`. Redundant name indexes are
removed, but the reverse tag index intentionally adds space to accelerate
all three tag-based search modes.

`normalized_version`, command helper columns, and raw plus parsed dependency
ranges remain for compatibility and downstream use. This is a compact schema,
not a claim of minimum possible size. Retaining all tags increases storage
relative to the earlier filtered cache, but is necessary for generic tag search.

The producer runs `PRAGMA optimize` and `VACUUM`, zips the database, and publishes
the ZIP to a versioned location and a latest alias. A separate JSON manifest
contains `artifactUrl`, `version`, `sha256`, `etag`, and `lastModifiedUtc`.
The SHA-256 is for the ZIP, not the extracted SQLite file.

## Dependency resolution

Gallery dependency strings have the form `Id:Range:TargetFramework`, joined by
`|`, for example `Az.Accounts:[5.5.5, ):|Az.Aks:[7.2.1, 7.2.1]:`. The writer
stores each dependency's `Range` segment in `version_range_raw`; the target
framework segment is ignored.

`GetDependencies` reads `dependencies` for one exact package version.
`FindVersionsSatisfying` loads the candidate versions for the dependency name,
then filters them in .NET with `NuGet.Versioning` `VersionRange.Satisfies`.
The stored `min_version`/`max_version` columns are not used for matching,
because SQL text comparison does not follow semantic-version precedence.
Empty, `*`, and `(, )` ranges are unbounded. Prerelease versions are excluded
unless requested, and stored versions that are not valid NuGet versions never
satisfy a range.
