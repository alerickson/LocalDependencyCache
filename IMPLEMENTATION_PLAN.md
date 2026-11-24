# LocalDependencyCache Implementation Plan

## 1. Objective
Build a C# Azure Functions app that reads package metadata from PowerShell Gallery OData APIs and writes a compact SQLite cache file that PSResourceGet can use for:
- Fast package search by `Id`, `Version`, and `Tags`
- Command and DSC-resource search via tags prefixed with `PSCommand_` and `PSDscResource_`
- Dependency lookup with NuGet-style version range matching
- A small download: only the most popular packages (top 100 by downloads, latest stable version) and their dependencies
- Artifact distribution through a dedicated Azure Blob Storage account exposed via a dedicated Azure Front Door Standard CDN

## 2. Scope
In scope:
- Selecting the top N packages by download count and resolving their dependencies from the `Search()` endpoint
- Parsing OData `properties` payload
- SQLite database creation, rebuilt each run
- Storage for package identity, tags, and dependencies with parsed version constraints
- Query patterns needed by PSResourceGet, in a `netstandard2.0` library PSResourceGet (net472) can reference
- Azure Functions hosting, scheduling, configuration, and observability
- Publishing generated SQLite cache artifact to Azure Blob Storage
- Consumer download pattern where PSResourceGet fetches SQLite through the Front Door CDN and performs local queries, falling back to the Gallery for packages not in the cache

Out of scope (initial version):
- Caching every version of every package (estimated 1–2 GB uncompressed; see section 6)
- Downloading package contents (.nupkg)
- Signature verification of packages
- Hosting API service on top of the DB
- Querying package data from Azure Function endpoints

## 3. Key Requirements Mapped to Design
- Compact DB file:
  - Normalized schema
  - Integer surrogate keys internally
  - `WITHOUT ROWID` where useful
  - Limited text duplication in child tables
- Searchable metadata:
  - Package identity (`Id`, `Version`)
  - Tags (including `PSCommand_` command tags)
- Dependency resolution:
  - Preserve original dependency range text
  - Parse and store normalized bounds (`min_version`, `max_version`, inclusive flags)

## 4. High-Level Architecture
1. Azure Functions host (isolated worker)
- `TimerTrigger` function runs periodic sync jobs
- Dependency injection composes client, parser, repository, and storage services

2. Gallery client
- Calls `https://www.powershellgallery.com/api/v2/Search()`, always sending `$filter`, `$orderby`, `$skip`, and `$top` together (the Gallery rejects some other combinations):
  - Top packages: `$filter=IsLatestVersion`, `$orderby=DownloadCount desc`, `includePrerelease=false`
  - Latest version of a dependency: `$filter=Id eq '<id>' and IsLatestVersion`, `$top=1`
  - All versions of a dependency (only when the latest does not satisfy a range): `$filter=Id eq '<id>'`, paged
- Retries transient failures with exponential backoff

3. Metadata parser
- Extracts `properties`
- Maps package fields, splits `Tags`, parses `Dependencies`
- Produces normalized model objects

4. Package selection
- Latest stable version of the top N packages
- For each dependency, transitively: reuse a selected version that satisfies the range, else the latest stable, else the highest satisfying stable, else the highest satisfying prerelease
- Unresolvable dependencies are logged and reported

5. SQLite writer
- Builds a fresh SQLite file in function temp storage each run
- Writes packages, tags, dependencies, and a content fingerprint in one transaction

6. Artifact publisher
- Skips publishing when the content fingerprint matches the published cache
- Uploads the versioned artifact (never overwritten), then the latest alias, then the manifest
- Produces a small manifest file with `etag`, `last_modified_utc`, and content hash for client freshness checks

7. Distribution layer
- A dedicated cache storage account (separate from the PowerShell Gallery's) holds the artifacts in a public-read container
- A new, dedicated Azure Front Door Standard profile serves that container as the CDN (Azure CDN classic tiers no longer allow new profiles)
- PSResourceGet reads the manifest and downloads the versioned SQLite artifact through Front Door
- PSResourceGet performs search and dependency queries locally against downloaded SQLite

8. Query layer
- `netstandard2.0` library consumed by PSResourceGet and the tests
- Validates command-tag and dependency matching behavior

## 5. Data Flow
1. Timer trigger starts sync run (hourly) and takes the Blob lease
2. Download the current published cache, if any, and read its content fingerprint
3. Fetch the top N packages and resolve their dependencies from the Gallery
4. Compute the content fingerprint; stop if it matches the published cache
5. Build a fresh SQLite database:
- Parse package metadata
- Parse tags into distinct rows
- Parse dependency list into rows with version range bounds
6. Compact, zip, and upload the versioned artifact, latest alias, and manifest
7. Front Door CDN serves SQLite and manifest to PSResourceGet clients
8. PSResourceGet downloads artifact and executes queries locally, falling back to the Gallery for cache misses

## 6. Scope and Size
- Each run rebuilds the cache, so there is no checkpoint, overlap window, or resume logic
- Measured: 100 top packages plus their dependencies is about 120 versions, 2.3 MB SQLite, 0.5 MB zipped
- Estimated: all 734k versions of all packages would be 1–2 GB SQLite (250–450 MB zipped), far beyond the few-MB to tens-of-MB indexes other package managers ship
- Because only popular packages are cached, name lookups that miss and all tag/command/DSC searches must also consult the Gallery for complete results

## 7. Component Plan
## 7.1 Project layout
- `src/LocalDependencyCache.FunctionApp/Program.cs`
- `src/LocalDependencyCache.FunctionApp/Functions/SyncCacheTimerFunction.cs`
- `src/LocalDependencyCache.FunctionApp/Gallery/GalleryClient.cs`
- `src/LocalDependencyCache.FunctionApp/Parsing/MetadataParser.cs`
- `src/LocalDependencyCache.FunctionApp/Storage/SchemaManager.cs`
- `src/LocalDependencyCache.FunctionApp/Storage/PackageRepository.cs`
- `src/LocalDependencyCache.FunctionApp/Storage/BlobArtifactPublisher.cs`
- `src/LocalDependencyCache.FunctionApp/Storage/ArtifactManifestWriter.cs`
- `src/LocalDependencyCache.FunctionApp/Versioning/NuGetRangeParser.cs`
- `src/LocalDependencyCache.FunctionApp/Models/*.cs`

## 7.2 Package choices
- `Microsoft.Data.Sqlite` for SQLite access
- `System.Text.Json` for parsing and serialization
- `Microsoft.Azure.Functions.Worker` and `Microsoft.Azure.Functions.Worker.Sdk`
- `Azure.Storage.Blobs` for publishing the cache artifact
- `Azure.Identity` if using managed identity for Blob access
- Optional: `Polly` for retry policies (or custom retry)

## 7.3 Configuration (Azure)
- `PowerShellGallery:Endpoint` (default `https://www.powershellgallery.com/api/v2/Search()`)
- `Sync:PageSize` (default `100`)
- `Sync:TopPackageCount` (default `100`)
- `Sync:IncludeDependencies` (default `true`)
- `Sync:Schedule` (CRON for timer trigger; hourly)
- `Storage:BlobConnection` or managed identity settings
- `Storage:ContainerName`
- `Storage:BlobName` (for example, `psgallery-cache.sqlite`)
- `Storage:ManifestBlobName` (for example, `psgallery-cache.manifest.json`)
- `Storage:LeaseBlobName` (for single-run lock)
- `Distribution:CdnBaseUrl` or `Distribution:StaticWebsiteBaseUrl`

## 8. Reliability and Performance
- HTTP retry with exponential backoff on transient errors
- Per-page processing with bounded memory
- Batched inserts in transactions (for example, 500 to 2000 rows per commit)
- SQLite pragmas tuned for build/update job:
  - `journal_mode=WAL`
  - `synchronous=NORMAL`
  - `temp_store=MEMORY`
- Final compaction step after full sync:
  - `ANALYZE`
  - `VACUUM` (optional for scheduled maintenance)
- Acquire Blob lease or distributed lock to prevent overlapping timer runs
- Emit structured logs and custom metrics to Application Insights
- Use cancellation tokens; a failed or cancelled run publishes nothing and leaves the previous cache in place
- Publish immutable versioned blobs plus stable latest alias to reduce CDN stale-content risk

## 9. Validation and Tests
Unit tests:
- Tag parsing and normalization
- Dependency string parser (NuGet range variants)
- Version bound comparison logic

Integration tests:
- Seed mini dataset and validate SQL lookup behavior
- Verify top-package selection and transitive dependency resolution
- Verify unchanged content is not republished
- Verify command search finds packages via `PSCommand_*` tags
- Verify Blob download/update/upload flow and atomic publish behavior
- Verify timer trigger idempotency with concurrent invocation attempts
- Verify manifest correctness and client download URL compatibility through CDN/static endpoint

Data-quality checks:
- Missing `Id`/`Version` rejection path
- Duplicate rows prevented by constraints

## 10. Azure Function Contract
Timer trigger:
- Function name: `SyncCacheTimerFunction`
- Trigger schedule: configurable CRON (`Sync:Schedule`)
- Behavior: select top packages and dependencies, rebuild the DB, and upload it and the manifest if the content changed

No consumer query endpoint:
- Azure Function does not provide package search APIs
- Consumption path is SQLite file download via CDN/static endpoint

## 11. Milestones
1. Schema and repository layer complete
2. Function host and timer trigger complete
3. OData ingestion and parser complete
4. Top-package selection, dependency resolution, and blob publish complete
5. CDN/static distribution validation, query verification, observability, and performance pass

## 12. Risks and Mitigations
- OData payload variation:
  - Keep parser defensive and nullable-friendly
- Dependency format edge cases:
  - Preserve original range text and fail-soft parsing
- Large gallery volume:
  - Stream pages and avoid loading all rows in memory
- Concurrent function executions:
  - Use Blob lease/distributed lock and idempotent upserts
- Partial artifact update risk:
  - Publish via staging blob then swap/copy on success
- CDN propagation delay or stale edge cache:
  - Version blob names and publish manifest containing current version and hash

## 13. Deliverables
- C# Azure Functions app for scheduled SQLite cache generation and blob publish
- SQLite schema and migration scripts
- Test suite covering parser and search/dependency behavior
- Operational notes for Azure deployment, scheduling, blob/CDN/static distribution, and monitoring
