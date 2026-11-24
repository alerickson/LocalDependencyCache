# LocalDependencyCache

LocalDependencyCache is a C# Azure Functions solution that builds a compact SQLite cache from PowerShell Gallery metadata and publishes that cache as a downloadable artifact.

The intended flow is:

1. The Azure Function reads package metadata from the PowerShell Gallery OData `Search()` endpoint.
2. The function parses package identity, tags, and dependency version ranges.
3. The function writes the normalized data into a local SQLite database.
4. The function compacts the SQLite database, packages it into a ZIP artifact, and publishes that ZIP plus a JSON manifest to either:
   - Azure Blob Storage, or
   - a local filesystem folder for development and test.
5. PSResourceGet downloads the published ZIP artifact from Blob Storage through Azure CDN or a static website endpoint, extracts the SQLite file locally, and performs queries locally.

## Solution layout

- `src/LocalDependencyCache.Core`
  - Core cache generation logic
  - PowerShell Gallery feed parsing
  - SQLite schema creation and data upsert logic
  - Artifact publishing abstractions and implementations
- `src/LocalDependencyCache.Query`
  - Shared query library for cache consumers (including PSResourceGet scenarios)
  - Targets `netstandard2.0`, so .NET Framework 4.7.2+ (PSResourceGet) and .NET 8 can both reference it; consumers can use any C# version
  - Read-only SQLite query APIs for name, name + version, tag, DSC resource, command, and package-prefix search
- `src/LocalDependencyCache.FunctionApp`
  - Azure Functions isolated worker host
  - Timer-triggered sync entry point
  - `--run-once` local execution path for development
- `tests/LocalDependencyCache.Tests`
  - Parser tests
  - End-to-end local filesystem publish tests

## Implementation overview

### Function host

The Functions host is configured in [src/LocalDependencyCache.FunctionApp/Program.cs](src/LocalDependencyCache.FunctionApp/Program.cs).

Key points:

- Uses the Azure Functions isolated worker model.
- Binds configuration into `SyncSettings`.
- Registers a typed `HttpClient` for `GalleryClient`.
- Chooses artifact publishing mode at startup:
  - `FileSystemArtifactStore` for local development
  - `BlobArtifactStore` for Azure publishing
- Supports `--run-once` for local execution without relying on a running timer host.

### Timer trigger

The scheduled function entry point is in [src/LocalDependencyCache.FunctionApp/Functions/SyncCacheTimerFunction.cs](src/LocalDependencyCache.FunctionApp/Functions/SyncCacheTimerFunction.cs).

It uses the `TimerSchedule` setting and delegates all real work to `PackageCacheSyncService`.

### Sync pipeline

The main orchestration is in [src/LocalDependencyCache.Core/PackageCacheSyncService.cs](src/LocalDependencyCache.Core/PackageCacheSyncService.cs).

Per run, it:

1. Takes the sync lock (`LockBlobName`). If another run holds it, the run is skipped without changes.
   If the lock is lost mid-run, the run stops without publishing.
2. Creates or reuses the working output directory and downloads the current `latest` SQLite artifact when one already exists.
3. Determines the sync start time using this priority order:
   - `FullSyncFromUtc` if set and not yet completed — re-syncs from that date, resuming from saved progress if an earlier run stopped part-way.
   - Stored checkpoint minus `OverlapMinutes` — normal incremental run.
   - `now - InitialLookBackHours` — first run, no stored checkpoint.
4. Streams packages published after that start time from PowerShell Gallery one page at a time.
   Paging advances a `Published` cursor rather than a growing `$skip`, and transient failures
   (network errors, timeouts, 408, 429, 5xx) are retried with exponential backoff.
5. Commits each page to SQLite — packages, tags, dependencies, and the checkpoint — in one transaction.
   The checkpoint only moves forward.
6. Stops early when `MaxSyncDurationMinutes` elapses. If the Gallery fails after some pages were
   committed, it publishes that progress and then rethrows. In both cases the next run resumes from the checkpoint.
7. Skips publishing when no package version was added or changed (the overlap window re-reads recent
   packages every run), unless nothing is published yet or a full sync is recording progress.
8. Compacts the SQLite database with `PRAGMA optimize` and `VACUUM`, packages it into a ZIP artifact,
   and computes a SHA-256 hash for that ZIP.
9. Publishes, in this order:
   - `versions/<checkpoint>-<sha256 prefix>/psgallery-cache.zip` (never overwritten)
   - `latest/psgallery-cache.zip`
   - `latest/psgallery-cache.manifest.json` (written last, so it always references a complete artifact)
10. Deletes versioned artifacts beyond `RetainVersions`.

### SQLite layout

The SQLite schema is created in [src/LocalDependencyCache.Core/SqlitePackageCache.cs](src/LocalDependencyCache.Core/SqlitePackageCache.cs).

Main tables:

- `packages`
- `tags`
- `package_tags`
- `dependencies`
- `sync_state`

Supported lookup patterns include:

- package identity search by `Id` and `Version`
- command search via tags prefixed with `PSCommand_`
- dependency lookup and NuGet version-range resolution

### Shared query library

Use [src/LocalDependencyCache.Query](src/LocalDependencyCache.Query) from cache consumers to query an existing SQLite cache file without depending on the sync pipeline.

Example:

```csharp
using LocalDependencyCache.Query;

var client = new SqlitePackageQueryClient(@".artifacts\psgallery-cache.sqlite");
var nameMatches = client.FindByPackageId("Sample.Module");
var versionMatches = client.FindByPackageIdAndVersion("Sample.Module", "1.2.3");
var tagMatches = client.FindByTag("Azure");
var dscMatches = client.FindByDscResourceName("File");
var commandMatches = client.FindByCommandName("Get-Module");
var idPrefixMatches = client.SearchByPackageIdPrefix("Az.", maxResults: 50);

// Dependency resolution
var dependencies = client.GetDependencies("Az.Compute", "1.0.0");
foreach (var dependency in dependencies)
{
    var best = client.FindLatestVersionSatisfying(dependency.Id, dependency.VersionRange);
}
var candidates = client.FindVersionsSatisfying("Az.Accounts", "[5.5.5, )", includePrerelease: true);
```

Exact names and tags are ASCII case-insensitive; versions match the stored
string exactly. Searches return all matching cached versions, including
prereleases, sorted by name and then by semantic version (so `1.9.0` precedes
`1.10.0-beta`, which precedes `1.10.0`). `SearchByPackageIdPrefix` applies its
limit before that sort, in name and version-text order.

`GetDependencies` returns the raw NuGet range declared for each dependency;
an empty range means any version. `FindVersionsSatisfying` and
`FindLatestVersionSatisfying` apply NuGet range semantics (via
`NuGet.Versioning`) and exclude prereleases unless `includePrerelease` is set.
Gallery forms such as `(, )` are treated as unbounded. Resolution is one level
deep; callers recurse to build a full dependency graph.
Command and DSC-resource searches match `PSCommand_` and `PSDscResource_` tags.
All tags are now retained. Older caches need a one-time full metadata refresh
using `PackageCache__FullSyncFromUtc` to recover previously discarded ordinary
tags.

### Artifact publishing

Artifact publishing implementations are in [src/LocalDependencyCache.Core/ArtifactStores.cs](src/LocalDependencyCache.Core/ArtifactStores.cs).

- `FileSystemArtifactStore`
  - Used for local development and tests
  - Writes versioned and latest ZIP artifacts under a local folder
- `BlobArtifactStore`
  - Used for Azure deployment
  - Uploads the versioned ZIP with `If-None-Match: *`, so a published version is never replaced
  - Uploads the latest alias, then a JSON manifest with artifact URL, version, hash, and `ETag`

Version names are `<checkpoint yyyyMMddHHmmss>-<first 12 hex characters of the ZIP's SHA-256>`, so a
name always identifies the same bytes. Blob uploads set CDN-friendly headers:

| Blob | `Cache-Control` | `Content-Type` |
|---|---|---|
| `versions/...` | `public, max-age=31536000, immutable` | `application/zip` |
| `latest/...zip` | `public, max-age=300` | `application/zip` |
| `latest/...manifest.json` | `public, max-age=300` | `application/json` |

Clients should read the manifest and download `artifactUrl` (a versioned, cache-forever URL), then verify
`sha256`. The `latest` ZIP alias is up to 5 minutes stale behind a CDN.

## Configuration

Example local configuration is in [src/LocalDependencyCache.FunctionApp/local.settings.json.example](src/LocalDependencyCache.FunctionApp/local.settings.json.example).

Common settings:

- `TimerSchedule`
  - Azure Functions timer CRON expression
- `PackageCache__GalleryEndpoint`
  - PowerShell Gallery OData endpoint
- `PackageCache__PageSize`
  - Number of packages requested per page
- `PackageCache__OverlapMinutes`
  - Minutes to re-read before the last checkpoint on incremental runs, to avoid missing late-published packages
- `PackageCache__InitialLookBackHours`
  - How far back to look on the very first run when no checkpoint is stored
  - Defaults to `5` in code; set to `87600` (10 years) in local settings to seed the full catalog
- `PackageCache__FullSyncFromUtc`
  - Optional: re-syncs the catalog from this UTC timestamp, ignoring the stored checkpoint
  - Example: `2010-01-01T00:00:00Z` to re-seed the entire catalog
  - Each value runs to completion once (resuming across runs if interrupted), then normal incremental sync resumes; change the value to run another full sync
  - Leave empty for normal incremental behavior
- `PackageCache__MaxSyncDurationMinutes`
  - Stop fetching after this many minutes and publish progress; the next run continues from the checkpoint
  - Set below the Functions timeout (for example `8` on a Consumption plan with a 10-minute timeout) so a full catalog sync spans several runs; `0` (default) means no limit
- `PackageCache__MaxRetries`
  - Retries per page for transient Gallery failures; defaults to `4`
- `PackageCache__RetryBaseDelayMilliseconds`
  - Base delay for exponential backoff with jitter (capped at one minute); a `Retry-After` header takes precedence; defaults to `1000`
- `PackageCache__StorageMode`
  - `FileSystem` or `Blob`
- `PackageCache__OutputDirectory`
  - Local artifact folder used by filesystem mode
- `PackageCache__DatabaseName`
  - SQLite filename
- `PackageCache__LatestBlobName`
  - Published latest ZIP path
- `PackageCache__ManifestBlobName`
  - Published latest manifest path
- `PackageCache__RetainVersions`
  - Number of versioned artifacts to keep (newest by creation time; the current one is always kept); defaults to `10`, `0` keeps all
- `PackageCache__LockBlobName`
  - Lock used to stop overlapping sync runs; defaults to `locks/psgallery-cache.lock`
  - In Blob mode this blob is leased for the run (60-second lease, renewed every 15 seconds, released at the end; a crashed run's lease expires on its own)
  - In FileSystem mode it is an exclusive lock file under `OutputDirectory`
  - A run that cannot take the lock logs a warning and exits without changes
- `PackageCache__BlobConnectionString`
  - Blob Storage connection string when using connection-string auth
- `PackageCache__BlobContainerName`
  - Blob container name
- `PackageCache__BlobContainerUrl`
  - Container URL when using managed identity
- `PackageCache__BlobBaseUrl`
  - Public CDN or static-website base URL for manifest artifact links

## Local testing

### Prerequisites

- .NET 8 SDK
- Internet access to `https://www.powershellgallery.com`

Optional for full Functions-host testing:

- Azure Functions Core Tools

### Run automated tests

From the repository root:

```powershell
dotnet test .\LocalDependencyCache.slnx
```

Current automated coverage includes:

- Atom feed parsing
- dependency parsing and NuGet version-range resolution
- gallery paging, retries, per-page checkpoints, time budget, and full-sync resume
- sync locking (file lock, lease loss, skipped runs)
- artifact publishing: change detection, immutable content-addressed versions, cache headers, retention
- command-tag lookup against generated SQLite

The Blob lease tests are skipped unless `LDC_TEST_BLOB_CONNECTION_STRING` is set.
To run them against the Azurite emulator:

```powershell
npx -p azurite azurite-blob --silent --location $env:TEMP\azurite   # in a separate terminal
$env:LDC_TEST_BLOB_CONNECTION_STRING = 'UseDevelopmentStorage=true'
dotnet test .\LocalDependencyCache.slnx
```

### Run the sync locally without Azure Functions Core Tools

This is the fastest local validation path.

Use the built-in one-shot execution mode:

```powershell
dotnet run --project .\src\LocalDependencyCache.FunctionApp\LocalDependencyCache.FunctionApp.csproj -- --run-once
```

By default, local settings use filesystem publishing and write artifacts under:

- `.artifacts`

During development I also validated the same flow with a custom output folder, which produced:

- `.artifacts-local\latest\psgallery-cache.zip`
- `.artifacts-local\latest\psgallery-cache.manifest.json`
- `.artifacts-local\versions\<version>\psgallery-cache.zip`

### Inspect the generated artifacts locally

After a successful local run, verify that these files exist:

```powershell
Get-ChildItem .\.artifacts\latest
Get-ChildItem .\.artifacts\versions -Recurse
```

You can also inspect the manifest:

```powershell
Get-Content .\.artifacts\latest\psgallery-cache.manifest.json
```

## Explore the SQLite database locally

After a successful run, the working database is typically at:

- `.artifacts\psgallery-cache.sqlite`

The published artifact is typically at:

- `.artifacts\latest\psgallery-cache.zip`

Use one of the methods below.

### Method 1: Use sqlite3 CLI

1. Open a terminal in repo root.
2. Start sqlite3:

```powershell
sqlite3 .\.artifacts\psgallery-cache.sqlite
```

3. In the sqlite prompt, run:

```sql
.tables
.schema packages
.schema tags
.schema dependencies
```

4. Run useful inspection queries:

```sql
-- Basic row counts
SELECT 'packages' AS table_name, COUNT(*) AS row_count FROM packages
UNION ALL
SELECT 'tags', COUNT(*) FROM tags
UNION ALL
SELECT 'package_tags', COUNT(*) FROM package_tags
UNION ALL
SELECT 'dependencies', COUNT(*) FROM dependencies;

-- Most recently published packages
SELECT package_id, version, datetime(published_unix, 'unixepoch') AS published_utc
FROM packages
WHERE published_unix IS NOT NULL
ORDER BY published_unix DESC
LIMIT 20;

-- Packages that expose a specific command tag
SELECT DISTINCT p.package_id, p.version
FROM tags t
JOIN package_tags pt ON pt.tag_key = t.tag_key
JOIN packages p ON p.package_key = pt.package_key
WHERE t.is_command_tag = 1
  AND t.command_name = 'Get-Module'
ORDER BY p.package_id, p.version;

-- Dependencies for a specific package
SELECT p.package_id, p.version, d.dependency_id, d.version_range_raw, d.min_version, d.max_version
FROM dependencies d
JOIN packages p ON p.package_key = d.package_key
WHERE p.package_id = 'Sample.Module'
ORDER BY p.version;

-- Sync state/checkpoints
SELECT state_key, state_value
FROM sync_state
ORDER BY state_key;
```

5. Exit sqlite3:

```sql
.quit
```

### Method 2: Use a SQLite GUI

If you prefer a GUI, open `.artifacts\psgallery-cache.sqlite` with DB Browser for SQLite or a VS Code SQLite extension.

Suggested first checks:

1. Verify `sync_state` contains `last_published_utc` and `last_success_utc`.
2. Check row counts in `packages`, `tags`, and `dependencies`.
3. Run the command-tag lookup query above to confirm `PSCommand_*` tags were parsed correctly.

### Notes

- `published_unix` and `updated_unix` are stored as Unix seconds; use `datetime(column, 'unixepoch')` to render UTC values in SQLite.
- Final dependency compatibility checks should be done in application code using semantic version rules; SQL queries are best used for inspection and candidate filtering.

## Local debugging

Use one of the two workflows below.

### Workflow A: Step-by-step debug with one-shot execution (recommended)

This is the most reliable local debugging path because it runs the same DI container and sync pipeline as the Function trigger, but executes once and exits.

1. Open the repo root in VS Code.
2. Verify prerequisites from an integrated terminal:

```powershell
dotnet --version
```

3. Confirm local settings exist at [src/LocalDependencyCache.FunctionApp/local.settings.json](src/LocalDependencyCache.FunctionApp/local.settings.json).
4. For local debugging, keep these values in that file:
  - `PackageCache__StorageMode`: `FileSystem`
  - `PackageCache__OutputDirectory`: `.artifacts`
  - `PackageCache__GalleryEndpoint`: a reachable OData endpoint
5. Set breakpoints in these files:
  - [src/LocalDependencyCache.FunctionApp/Program.cs](src/LocalDependencyCache.FunctionApp/Program.cs)
  - [src/LocalDependencyCache.Core/PackageCacheSyncService.cs](src/LocalDependencyCache.Core/PackageCacheSyncService.cs)
  - [src/LocalDependencyCache.Core/GalleryClient.cs](src/LocalDependencyCache.Core/GalleryClient.cs)
  - [src/LocalDependencyCache.Core/SqlitePackageCache.cs](src/LocalDependencyCache.Core/SqlitePackageCache.cs)
6. Start debugging from VS Code as a .NET launch and pass this argument:

```text
--run-once
```

7. If you prefer terminal-first debugging, run exactly this command from repo root:

```powershell
dotnet run --project .\src\LocalDependencyCache.FunctionApp\LocalDependencyCache.FunctionApp.csproj -- --run-once
```

8. After completion, verify outputs:

```powershell
Get-ChildItem .\.artifacts\latest
Get-ChildItem .\.artifacts\versions -Recurse
Get-Content .\.artifacts\latest\psgallery-cache.manifest.json
```

Expected result:
- A ZIP artifact at `latest/psgallery-cache.zip`
- A manifest at `latest/psgallery-cache.manifest.json`
- A working SQLite file at `.artifacts\psgallery-cache.sqlite`
- A versioned ZIP artifact under `versions/<checkpoint>-<hash>/`

### Workflow B: Step-by-step debug with Azure Functions host

Use this when you want to debug timer-trigger behavior specifically.

1. Install Azure Functions Core Tools v4.
2. Open the repo root in VS Code.
3. Ensure [src/LocalDependencyCache.FunctionApp/local.settings.json](src/LocalDependencyCache.FunctionApp/local.settings.json) includes:
  - `FUNCTIONS_WORKER_RUNTIME=dotnet-isolated`
  - `TimerSchedule` with a short interval for testing (example every minute)
4. Start the host:

```powershell
Set-Location .\src\LocalDependencyCache.FunctionApp
func start
```

5. In VS Code, attach to the running .NET worker process.
6. Wait for the timer to fire, then step through breakpoints in:
  - [src/LocalDependencyCache.FunctionApp/Functions/SyncCacheTimerFunction.cs](src/LocalDependencyCache.FunctionApp/Functions/SyncCacheTimerFunction.cs)
  - [src/LocalDependencyCache.Core/PackageCacheSyncService.cs](src/LocalDependencyCache.Core/PackageCacheSyncService.cs)

Notes:
- `RunOnStartup` is `false`, so timer execution starts on schedule.
- Keep the interval short during debugging, then restore it after debugging.

### Troubleshooting local debug failures

If debugging or local runs fail, check these in order:

1. Incorrect command syntax for one-shot mode

Use:

```powershell
dotnet run --project .\src\LocalDependencyCache.FunctionApp\LocalDependencyCache.FunctionApp.csproj -- --run-once
```

Do not use `dotnet run --run-once` (the argument is not forwarded correctly).

2. Endpoint not reachable

- Verify `PackageCache__GalleryEndpoint` in [src/LocalDependencyCache.FunctionApp/local.settings.json](src/LocalDependencyCache.FunctionApp/local.settings.json).
- If you use a test feed endpoint, confirm it is reachable from your machine.

3. No artifacts generated

- Confirm `PackageCache__StorageMode` is `FileSystem` for local runs.
- Confirm `PackageCache__OutputDirectory` points to a writable path.

4. Timer does not fire while host is running

- Set `TimerSchedule` to a short interval.
- Restart `func start` after changing local settings.

5. Build/runtime mismatch

- Run `dotnet test .\LocalDependencyCache.slnx` first.
- If tests pass but host fails, review host startup logs in terminal output.

## Using Blob Storage locally or in Azure

To test Blob publishing instead of filesystem output, update local settings:

- Set `PackageCache__StorageMode` to `Blob`
- Set either:
  - `PackageCache__BlobConnectionString` and `PackageCache__BlobContainerName`, or
  - `PackageCache__BlobContainerUrl` for managed identity
- Set `PackageCache__BlobBaseUrl` to the public CDN or static website base URL you expect PSResourceGet to use

The manifest will then point to the published versioned SQLite file under that base URL.

## Useful files

- [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md)
- [SQLITE_SCHEMA.md](SQLITE_SCHEMA.md)
- [instructions.md](instructions.md)

## Current validation status

The current implementation has been validated with:

```powershell
dotnet test .\LocalDependencyCache.slnx
dotnet run --project .\src\LocalDependencyCache.FunctionApp\LocalDependencyCache.FunctionApp.csproj -- --run-once
```

The first command verifies the parser and local artifact pipeline. The second command exercises the real host configuration and generates a local SQLite artifact and manifest.