# ShizuAppStoreServer — Implementation & Design Spec

Server backend for a Shizuku-app store. It syncs the `awesome-shizuku`
awesome-list (README Apps section) into a Postgres catalog,
enriches each entry from its upstream release source (forge releases,
F-Droid/Izzy indexes), and serves the catalog through a versioned JSON
API plus content-addressed icons. Single .NET 10 binary; enrichment and
sync run in-process as hosted services.

This spec describes implementation and behavior. Deployment, secrets,
and server provisioning live in `server-setup.md`. The build roadmap
lives in `PLAN.md`.

## 1. Project layout

```
src/ShizuAppStoreServer/          Web host: controllers, Api/, Sync/, Program.cs
src/ShizuAppStoreServer.Core/     All domain logic (no ASP.NET references)
  Parsing/      Markdig awesome-list parser
  Data/         EF Core entities + ShizuDbContext + Migrations/
  History/      git-history backfill (added_at/updated_at)
  Sources/      URL classifier, GitHub/GitLab clients, APK picker, F-Droid index
  Enrichment/   aapt2/apksigner runners + parsers, DrawableStager,
              Paparazzi renderer client, icons, avatars, AppEnricher
  Sync/         SyncService, SyncOptions, IEnrichmentRunner
tools/icon-render/   Gradle + Paparazzi tool that renders staged XML
              drawables through LayoutLib (same engine as Android Studio)
tests/ShizuAppStoreServer.Core.Tests/   Hermetic unit/integration tests (SQLite)
tests/ShizuAppStoreServer.Web.Tests/    Endpoint tests (full host, SQLite-swapped)
```

Core never references ASP.NET: everything the host needs crosses the
boundary through constructor injection (`IAapt2Runner`,
`IApkSignerRunner`, `IGitHubReleaseClient`, `IGitLabReleaseClient`,
`FdroidIndexProvider`, `IEnrichmentRunner`, `IPaparazziRenderer`).

## 2. Runtime architecture

`Program.cs` wires, in order: controllers, OpenAPI document (all
environments) + Scalar UI (development only), Npgsql `DbContext`,
option singletons (`ApiOptions`, `EnrichmentOptions`, `AdminOptions`,
`SyncOptions`), typed HTTP clients (GitHub/GitLab 30s timeouts,
F-Droid index 60s, `apk-download` uses the configured download
timeout), singleton runners (`Aapt2Runner`, `ApkSignerRunner`,
`PaparazziRenderer`), scoped `AppEnricher` factory, singleton `FdroidIndexProvider`,
sync services (`GitHistoryService`, `CatalogUpserter`, `SyncService`,
`IEnrichmentRunner`), single-flight `SyncGate` + `SyncPassRunner`,
and two hosted workers (fast loop + nightly).

Request pipeline order matters: `UseOutputCache` runs **before**
`UseRateLimiter`, so cache hits don't consume rate-limit permits.
All `/v1/*` controllers carry `[EnableRateLimiting("api")]` except
`/healthz`, which is unlimited and uncached. The `api` policy is a
per-IP (fallback `"unknown"`) fixed window: 100 req/min by default, no queue —
excess gets 429. `ApiOptions` is resolved per request (not captured),
so tests can swap the registration per suite.

Startup gate: after `builder.Build()`, the host probes
`aapt2 version`, `apksigner --version` (30s/60s timeouts), and
`gradle --version` (2min) and **refuses to boot**
(`InvalidOperationException`) when any is missing or exits
non-zero — a server without its toolchain would serve a catalog
that never enriches. Skipped only when the host
environment is `Testing` (the integration-test host sets it).

## 3. Data model (`Core/Data/`)

Tables are snake_case, `bigint` identity PKs, enums stored as
strings. Migrations live next to the context; `dotnet ef migrations
bundle` is rebuilt per deploy, never committed.

- **categories** — `slug` unique; self-referencing `parent_id`
  (max depth 2 in real data); `section` (`apps|libraries|misc`).
- **apps** — one awesome-list entry. `slug` globally unique and
  **stable across renames**. `url` is deliberately **non-unique**:
  real data lists the same URL in several categories, so identity is
  `(listing, url, category)`, never the URL alone.
  - List fields: `name`, `description`, `license`, `listing`
    (`main|closed_source`), `type` (`app|library|flow`), flags
    (`is_recommended`, `has_paid`, `has_iap`, `has_ads`,
    `trial_days`, `requires_root`), `parent_id` (nested entries),
    `url`, `source_url`, `source_kind`, `availability`, `store_url`.
  - Enrichment fields: `package_name`, `version_code`,
    `version_name`, `apk_url`, `apk_size`, `apk_sha256`,
    `apk_archive_entry` (APK path inside `apk_url` when that is a
    zip; §5), `min_sdk`,
    `icon_hash`, `icon_adaptive`, `apk_source` / `apk_source_ref`
    (APK source lock, §5), `enrich_etag` (conditional-request ETag
    reuse), `last_checked_at`, `last_error` (trimmed to 500 chars).
  - Signatures + F-Droid variant (§6): `sig_sha256`, `sig_md5`,
    `fdroid_apk_url`, `fdroid_version_code`,
    `fdroid_version_name`, `fdroid_apk_size`, `fdroid_apk_sha256`,
    `fdroid_sig_sha256`, `fdroid_sig_md5` (all nullable).
  - `exclude_override` (operator flag, never touched by the
    upserter), `excluded_reason`, `added_at`/`updated_at` (git
    history, §4).
  - Indexes on `url`, `updated_at`, `availability`, `category_id`.
- **app_versions** — (`app_id`, `version_code`, `version_name`,
  `apk_url`, `detected_at`); a row is appended only when the
  (code, name) pair is unseen for the app.
- **sync_runs** — audit log of passes: `trigger`, `head_commit`,
  per-bucket counts (added/updated/removed/enriched/up-to-date/
  failed, drained requests, parse warnings, archived changes),
  `skipped` flag, `error`. A `Skipped` pass
  writes **no row** (clean audit log, not a heartbeat table).
- **sync_requests** — webhook queue (`reason`, `processed`); rows
  are marked processed only on pass success, so a failed pass never
  loses its trigger.
- **removed_apps** — tombstones closing the delete path:
  `slug` unique + `removed_at` index. Written (add-or-refresh) on
  stale delete, cleared on re-add (resurrection).

## 4. List ingestion

- **Parser** (`Parsing/`, Markdig): reads `README.md` (listing
  `main`) only, and only its `## Apps` section: Development
  libraries and Miscellaneous content stay out of the catalog.
  `pages/CLOSED_SOURCE.md` is intentionally ignored (Play-only
  proprietary entries, user call); sync still passes an empty
  `closed-source` document so pre-decision rows sweep out as stale.
  Hierarchy (categories, subcategories, nested child entries) and
  per-entry flags/license/source links are preserved.
- **History** (`History/GitHistoryService`): one
  `git log --reverse -p` pass over `README.md`; the first
  `+* [Name](url)` sighting of a URL is `added_at`, the last is
  `updated_at`.
- **Upserter** (`Sync/CatalogUpserter`): matches rows by
  `(listing, url, category)`. Rows from sections no longer ingested
  (libraries, misc, closed-source) sweep out as stale. Same-URL
  rename keeps id + slug.
  Move-vs-duplicate is resolved in two phases against precomputed
  logical locations: vanished old location = move (row reused),
  still-parsed old location = genuine duplicate (new row). Stale
  `(url, category)` pairs are hard-deleted and reported by slug.
- **ARCHIVED.md** (`pages/ARCHIVED.md`, applied after upsert):
  listed URLs (entry + source links, recursive incl. children) are
  marked `Excluded` with a fixed reason; un-listed apps are
  un-marked (reason cleared, check fields nulled). An emptied file
  still clears — emptiness is meaningful, there is no early return.

## 5. Enrichment pipeline (`Enrichment/`, `Sources/`)

Per-app, no `SaveChanges` (callers batch). A freshness gate runs
first: unless `force`, apps checked inside the success window (24h)
or failure backoff (12h) return `SkippedFresh` with no work.

Resolution is **forge-first, always**: GitHub → GitLab →
F-Droid/Izzy → fallback. A forge link anywhere in the entry (primary
or source URL) wins the primary APK; F-Droid is never primary when a
forge source exists. Play + forge combos keep Play as `store_url`.

When a forge fails with no usable APK (no release, no `.apk` asset
and no archive carrying one), the app falls back to the F-Droid main
index: the `<application>` whose `<source>` URL matches the entry's
forge repo. The source that supplied an APK is recorded (`apk_source`,
`apk_source_ref`) and **locked**: enrichment never switches an app
between forge and F-Droid afterwards, because the signing keys differ
and installed clients would reject the update. A locked F-Droid app
skips the forge entirely and re-resolves through the index; a locked
forge app never falls back. Apps without any served APK are not
locked, so later enrichment can still rescue them.

- **GitHub** (`GitHubReleaseClient`, raw HttpClient + PAT from
  config or `SHIZU_GITHUB_TOKEN`): newest non-draft of the first
  100 releases (prereleases count: many Shizuku apps ship only
  prereleases); `EnrichEtag` drives `If-None-Match`,
  304 → `UpToDate` (only `last_checked_at` touched).
  `ApkAssetSelector` prefers a `release`-named `.apk`, else the
  largest `.apk`. If the release ships no `.apk`, a `.zip` asset is
  downloaded and the APK inside it is analyzed (some projects attach
  only a release bundle): `apk_url` stays the archive download,
  `apk_size`/`apk_sha256` describe the archive, and
  `apk_archive_entry` names the APK inside. An unusable archive with
  no APK falls through to the F-Droid fallback, else `Failed`.
- **GitLab** (`GitLabReleaseClient`, `PRIVATE-TOKEN` from config or
  `SHIZU_GITLAB_TOKEN`): skips `upcoming` releases, prefers
  `direct_asset_url`. APK links embedded in the release description
  (AuroraStore style) are collected too; relative `/uploads/...`
  links resolve through
  `https://gitlab.com/api/v4/projects/{urlencoded-path}{url}` (the
  web-UI uploads routes 404 or redirect to sign-in). Links match by
  name or URL, so generic labels like `APK` still resolve (e.g.
  narektor/batt links to `Batt-1.3.apk`). GitLab largely
  ignores `If-None-Match`, so an unchanged recorded asset URL
  short-circuits to `UpToDate`. No `.apk` link → F-Droid fallback,
  else `Failed`.
- **F-Droid/Izzy** (`FdroidRepoClient` + singleton
  `FdroidIndexProvider`): conditional GET of `{base}/index.xml`
  (cached or seed ETag; 304 without cache → `UpToDate`; one
  in-flight fetch per repo so parallel enrichments don't stampede).
  The streaming parser takes the first `<package>` per
  `<application>` (the newest); version/versioncode/sig are child
  **elements** (package attributes accepted as fallback), `<sig>`
  is the 32-hex signing-cert MD5. Same apk URL + version code →
  `UpToDate` with zero downloads. Otherwise the APK is downloaded
  and fully analyzed like a forge build (§5.1); unparseable files
  fall back to the index-only record, a package-name mismatch
  fails the pass (index row and file disagree), a package missing
  from the index fails with `not in`. A hit locks the app to this
  repo (`apk_source` = FDroid/Izzy, `apk_source_ref` = package id)
  so updates keep coming from the same signing source. Index
  lookups by source URL (`FindPackageBySourceAsync`) power the
  forge fallback.
- **Fallback** (no forge/F-Droid source): letter-avatar icon,
  `LinkOnly` - or `PlayRedirect` with `StoreUrl` for Play entries.
  When a Play listing is linked (including on apps whose forge has no
  APK at all), the real icon is scraped from the listing's `og:image`
  and used instead of the avatar; `apk_url` stays null and the client
  shows an open-in-Play-store / open-externally button based on the
  entry URL.
  Play-sole-source apps (no usable source link, no override) are
  `Excluded` with a reason and hidden from every endpoint.

### 5.1 APK analysis (forge + changed F-Droid builds)

Temp download → `aapt2 dump badging` (package, versionCode(long),
versionName, `minSdkVersion`/`sdkVersion`, best-density
`application-icon` lines) → file SHA-256/size → `apksigner verify
--print-certs` → icon chain, XML first at every level (a real vector
render stays sharp while a stale PNG would fossilize): manifest
`android:icon` XML staged for Paparazzi, then the manifest raster
decoded in-house, then a badging XML path, then badging rasters
(PNG and WebP decoded in-house). XML drawables render through Google
LayoutLib via the Paparazzi Gradle tool (`tools/icon-render`):
`DrawableStager` turns binary AXML into text XML under a flat
generated namespace (`shizu_N.xml`, referenced rasters copied
 alongside, literals inlined, framework non-color refs and theme
 attrs abort the stage; adaptive-icon roots are staged beside `res/` because
 Paparazzi pre-parses every res XML and chokes on the adaptive root).
 Adaptive roots are normalized before staging: a missing
 background inserts white (transparent would show the app list
 through), and single-child non-`<inset>` wrappers are lifted.
 `<inset>` wrappers are kept intact with their padding: the
 renderer draws the layer into the inset rect (dp/px/sp/unadorned
 over the 108dp viewport, `%` literal), because unwrapping them
 to full-bleed threw the author's padding away and rendered less
 padding than a phone shows. The toplevel manifest `android:icon`
 lookup stays XML-first (any staged XML beats the raster). Nested
 references inside staged drawables resolve device-faithfully through
 `resources.arsc`: versioned XML, then unversioned anydpi XML, then
 best-density raster, then fallback XML (default-config XML still
 loses to rasters: it is the dead fallback vector where real devices
 show the density PNG; SDK version breaks density ties toward the
 modern entry). Nested `@color` refs that the app table cannot answer
 fall back to a generated `android.R.color` literal table
 (`FrameworkColors`, API 35), so foreground vectors tinted with
 framework or app-chained-to-framework colors stage instead of
 aborting.
the tool composites adaptive-icon layers through real Android
drawables (AdaptiveIconDrawable itself cannot inflate under
LayoutLib, so background/foreground resolve and draw full-bleed
in a fixed-size view) and writes an exact-size PNG per icon, which
the service normalizes (a defensive top-left crop only guards
against a misbehaving renderer). Renders are serialized through one
Gradle invocation per pass (`renderIconBatch` over a manifest of
`name|root|out` lines with per-app filename prefixes; a single
`renderIcon` remains for one-offs), because each invocation pays a
full task graph + test-JVM + LayoutLib boot. F-Droid primaries fall back to
the mirrored repo icon before the avatar. A null renderer
(unit tests) disables the XML path. → fill row → append
`app_versions` row when new → delete orphaned old icon file
(change-tracker-local references count, so batched passes are
safe) → delete APK. File bytes win over index values on any
disagreement. `apksigner` failure (missing JRE/binary, unsigned
APK) leaves fingerprints null and never fails enrichment.

Upstream timeouts (slow downloads, stalled feeds) record `last_error`
like any failure and retry on backoff, as do transport failures
(DNS, TLS, reset, with the cause attached); they never fail silently.
Genuine shutdown cancellation still aborts the pass.

Outcomes: `Enriched`, `UpToDate`, `AvatarFallback`, `Excluded`,
`Failed` (`last_error` set, previous good values kept),
`SkippedFresh`. `BulkEnricher` fans out over app ids with a
`SemaphoreSlim` (default 4), preserving order, isolating faults;
cancellation propagates.

`--refresh-icons [--force]` is a one-shot that re-resolves every
`DirectApk` icon without touching versions: byte-identical renders
stay `UpToDate` (missing files are still rewritten), changed
renders adopt the new hash. `--force` additionally recounts
identical renders as refreshed, which surfaces self-consistent
wrong files (e.g. two swapped icons whose hashes matched their
rows). It writes no `sync_runs` row.

Icons are normalized to ≤192px PNGs, stored content-addressed as
`{sha256}.png`, and served immutable. Letter-avatars are
deterministic 192px PNGs (name-hashed background, embedded glyph).
Every icon carries an `icon_adaptive` flag (`iconAdaptive` in the
app DTOs): true only when the served file renders an
`<adaptive-icon>` root (full-bleed, safe for rounded-square
framing); plain vectors render full-bleed too but stay false, as do
decoded rasters and avatars (framed in a squircle box). Refresh
passes re-sync the flag without icon churn.

## 6. Signatures & F-Droid alternate variant

F-Droid builds are delayed and (unless reproducible) signed with a
different key, so both signers are recorded:

- `sig_sha256` / `sig_md5`: primary-APK fingerprints from
  `apksigner` (every `Signer #N certificate … digest` line is
  collected — key rotation yields space-joined sets matched by
  membership; MD5 is optional for old build-tools), or the index
  `<sig>` MD5 for index-only F-Droid/Izzy primaries.
- `fdroid_*`: the same package's F-Droid main-repo build, resolved
  by package name after every fresh forge enrich (never on
  `SkippedFresh`, and skipped when the primary is already
  F-Droid-locked: a variant would duplicate it). Downloaded +
  analyzed on change, recorded
  index-only when the file can't be fetched, refreshed MD5 on the
  up-to-date short-circuit, cleared when F-Droid drops the package.
  The sidecar is strictly additive and broadly caught — it never
  changes the primary outcome.

Client contract: hash the installed app's signing cert (SHA-256 and
MD5) and compare against `sig_*` + `fdroid_variant` fingerprints;
when a variant fingerprint matches, offer
`fdroid_variant.apk_url` instead of the primary `apk_url`.

## 7. Sync engine

`SyncService.RunAsync` wraps everything: any non-cancellation
exception clears the change tracker and lands as an error `SyncRun`
row. A pass:

1. Drains unprocessed `sync_requests` (any rows → trigger
   `"webhook"`; marked processed only on success).
2. Best-effort `git fetch` (5-min timeout; offline/timeout →
   continue off the local clone).
3. Compares HEAD against the latest run's commit: unchanged HEAD +
   no requests + no `force` → enrich due-only apps and write the
   row, or return `Skipped` (no row) when nothing is due.
4. Else full pass: read + parse `README.md` (Apps section) →
   history → upsert (the empty closed doc sweeps stale listings) →
   `ARCHIVED.md` → enrich selection (full re-check: all
   non-excluded with `force=true`; else the client-side due
   window) fanned out per-app through `BulkEnricher` with
   `IEnrichmentRunner` (fresh scope per app, persists its own
   save; vanished rows count `Failed`) → mark requests processed +
   write the run row. No app mutations happen after the upsert
   save, so the final save writes only requests + run.

Workers (`Web/Sync/`): `SyncWorker` runs one pass at startup
(`RunOnStartup`, the first-boot backfill) then ticks on a
`PeriodicTimer` (`FastLoopMinutes`, floored at 1); `NightlyWorker`
sleeps until the next strictly-future `NightlyTimeUtc` (HH:mm UTC,
default 03:00; invalid values disable it with a log) and triggers
full re-checks. `SyncGate` single-flights passes — a contested tick
skips.

## 8. API behavior (`/v1/*`)

Snake_case wire format (`main|closed_source`, `app|library|flow`,
`apps|libraries|misc`, `github|gitlab|codeberg|fdroid|izzy|play|
other`, `direct_apk|play_redirect|link_only|excluded`). `excluded`
rows are never returned (detail reads them as 404).

| Endpoint | Behavior |
|---|---|
| `GET /v1/apps` | Filters: `category` (subtree incl. subcategories, unknown → 400), `q` (case-insensitive contains over name/description/package), `license` (case-insensitive exact), `listing`/`availability`/`type` (parse or 400), `recommended` (`true|false` or 400). `page` ≥ 1 else 400; `pageSize` clamped 1–200, default 50. `sort` ∈ `updated|added|name` (default `updated`, else 400); `order` ∈ `asc|desc`, default desc except `name` → asc. Ordering + paging run in memory (identical semantics on both DB providers). Output-cached 60s, `VaryByQuery(*)`. |
| `GET /v1/apps/{slug}` | Full detail: summary fields + URLs, `source_kind`, version, `category_path` (root→leaf) + `parent_slug`, `added_at`, `last_checked_at`, sigs + nullable `fdroid_variant`. When `apkUrl` is a zip, `apkArchiveEntry` names the APK inside (clients must extract it). ETag `"{ticks}-{id}"`; `If-None-Match` → 304. Output-cached 60s. |
| `GET /v1/categories` | Tree with per-node subtree app counts (excluded omitted). ETag from count + id-sum + max `updated_at`; `If-None-Match` → 304. Output-cached 5min. |
| `GET /v1/changes?since=` | `since` required ISO-8601 else 400. `added` (`added_at` ≥ since), `updated` (`updated_at` ≥ since but added before), `removed` (tombstones ≥ since) — all oldest-first, excluded hidden. Output-cached 30s, `VaryByQuery(*)`. |
| `GET /v1/meta` | `generated_at`, latest run's `list_commit` (null before the first pass), counts (non-excluded apps, categories). Output-cached 60s. |
| `GET /healthz` | `{"status":"ok"}`. No rate limit, no cache. |
| `GET /icons/{sha}.png` | 64-hex sha else 400; missing file → 404; served as a physical file with manual immutable 1-year `Cache-Control` (no output-cache attribute — its filter would overwrite the header). |
| `POST /v1/admin/sync` | Webhook: secret from `Admin:HmacSecret` or `SHIZU_ADMIN_SECRET`, else fail-closed 503. `X-Shizu-Signature` must be hex `HMAC-SHA256(raw body)` (constant-time compare, bodies > 4KB rejected) else 401. Inserts a `sync_requests` row → 202 `{queued:true}`. |

Rate limit: fixed window, 100 req/min/IP, no queue (→ 429).
Caching: server-side output cache per the table above plus
`ResponseCache` client headers on GET endpoints. OpenAPI document
is served in all environments; Scalar UI is development-only.

## 9. Behavior knobs (code-level defaults)

| Option | Default | Effect |
|---|---|---|
| `Api:RateLimitPerMinute` | `100` | Fixed-window limit per client IP |
| `Api:EnableOutputCache` | `true` | Server-side GET caching (tests disable it) |
| `Enrichment:Aapt2Path` / `ApksignerPath` | `aapt2` / `apksigner` | Binaries, verified at startup |
| `Enrichment:GradlePath` | `gradle` | Gradle binary for icon renders, verified at startup |
| `Enrichment:IconToolDir` | `tools/icon-render` | Paparazzi tool checkout |
| `Enrichment:PaparazziTimeout` | `15min` | Per-icon render timeout |
| `Enrichment:IconStorePath` | `icons` | `{sha256}.png` icon store |
| `Enrichment:MaxParallelism` | `4` | Concurrent enrichments |
| `Enrichment:SuccessRecheckInterval` | `24h` | Healthy-app re-check window |
| `Enrichment:FailedRecheckInterval` | `12h` | Backoff after `last_error` |
| `Enrichment:DownloadTimeout` | `10min` | APK download HTTP timeout |
| `Enrichment:GitHubToken` / `GitLabToken` | `null` (+ `SHIZU_GITHUB_TOKEN` / `SHIZU_GITLAB_TOKEN` env fallback) | Release-API auth/rate limits |
| `Sync:ListPath` | `/opt/shizuappstore/list` | Local list clone |
| `Sync:FastLoopMinutes` | `15` | Fast-loop period (≥ 1) |
| `Sync:NightlyTimeUtc` | `03:00` | Full re-check time (UTC) |
| `Sync:RunOnStartup` | `true` | Boot pass (first boot = backfill) |

## 10. Invariants & gotchas (do not break)

- Forge-first is policy: no change may make F-Droid primary while
  a forge link exists; the variant sidecar may only add data, never
  alter the primary outcome.
- Source lock is the exception to forge-first: an app already
  served from F-Droid/Izzy stays there even when a forge APK
  appears later (and vice versa), or clients fail the update
  signature check. `apk_source` is written on every APK lock.
- `apps.url` is not an identity — always key on
  `(listing, url, category)`.
- Tombstone closure: every stale-delete path must write, every
  re-add path must clear, or `/v1/changes removed[]` drifts.
- The sync worker scope makes no app mutations after the upsert
  save (final save = requests + run row only).
- File bytes win over index values; package-name mismatch is the
  only file-content hard failure.
- `Testing` is the only environment that skips the startup tool
  probe; production boot without the toolchain must fail.
- Test-host rules learned the hard way: swap option singletons via
  DI (minimal-hosting `ConfigureAppConfiguration` overrides never
  reach `Program.cs`); output cache has no request-driven bypass
  (tests re-register no-op policies); never put `[ResponseCache]`
  on the icons action; no `ORDER BY`/`Max`/`Where` over
  `DateTimeOffset` in LINQ shared with SQLite tests — sort and
  filter those in memory (Npgsql translates the same LINQ fine).
- Npgsql writes `DateTimeOffset` to timestamptz only with Offset=0
  (Postgres stores UTC instants, no offsets) while SQLite accepts
  anything: history dates are parsed with `AdjustToUniversal` and
  `SaveChanges[Async]` normalizes the rest, so the divergence
  cannot recur.
- Real upstream formats, locked by verbatim tests: `dump badging`
  emits `minSdkVersion` (not `sdkVersion`); F-Droid
  version/versioncode/sig are elements with 32-hex MD5 sigs;
  modern `apksigner --print-certs` prints SHA-256 + SHA-1 + MD5
  per signer; compiled XML/arsc layouts follow AOSP
  ResourceTypes with density selection preferring rasters.
