# ShizuAppStoreServer - Implementation & Design Spec

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
  Sources/      URL classifier, IAppSource clients (GitHub/GitLab/GitCode/
              F-Droid), APK picker, shared release pipeline
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
`IApkSignerRunner`, `IAppSource` source clients,
`FdroidIndexProvider`, `IEnrichmentRunner`, `IPaparazziRenderer`).

## 2. Runtime architecture

`Program.cs` wires, in order: controllers, OpenAPI document (all
environments) + Scalar UI (development only), Npgsql `DbContext`,
option singletons (`ApiOptions`, `EnrichmentOptions`, `AdminOptions`,
`SyncOptions`, `UserAgentTrackingOptions`, `RequestLogOptions`), typed HTTP clients (GitHub/GitLab 30s timeouts,
F-Droid index 60s, `apk-download` uses the configured download
timeout), singleton runners (`Aapt2Runner`, `ApkSignerRunner`,
`PaparazziRenderer`), scoped `AppEnricher` factory, singleton `FdroidIndexProvider`,
sync services (`GitHistoryService`, `CatalogUpserter`, `SyncService`,
`IEnrichmentRunner`), single-flight `SyncGate` + `SyncPassRunner`,
and four hosted workers (fast loop, nightly, user-agent flush, non-client request log).

Request pipeline order matters: `UseOutputCache` runs **before**
`UseRateLimiter`, so cache hits don't consume rate-limit permits.
All `/v1/*` controllers carry `[EnableRateLimiting("api")]`. `/healthz` and
`/icons/*` are exempt: `/healthz` is unlimited and uncached, and icons are
immutable static assets requested in bulk by list screens, so the edge caches
them instead of the origin enforcing a limit. The `api` policy is a
per-IP (fallback `"unknown"`) fixed window: 100 req/min by default, no queue -
excess gets 429. `ApiOptions` is resolved per request (not captured),
so tests can swap the registration per suite.

Anonymous usage stats: `UserAgentTrackingMiddleware` sits right after
response compression and **before** `UseOutputCache`, so it sees every
`/v1/*` request including cache hits. It records after the response,
skipping `/v1/admin/*`, `/healthz`, `/icons/*`, requests without a
`User-Agent` header, and 429 responses; UA and path are truncated to 512
chars. Hits go to a bounded in-memory channel (drop-on-full, never blocks
the request). `UserAgentTrackingWorker` flushes the buffer into
`client_user_agents` + `client_user_agent_days` every
`UserAgentTracking:FlushInterval` and on shutdown; flush failures are
logged and never kill the host. DB-only: no endpoint exposes these tables.
The separate `ShizuAppStoreStats` app reads both tables read-only for its HTML
dashboard, and the same app also reads `app_install_days`,
`app_version_install_days` and `apps.install_count`; the API surface is
unchanged.

Non-client traffic: `NonClientRequestLoggingMiddleware` sits next to the UA
middleware (after response compression, before `UseOutputCache`) and records
one row per request whose `User-Agent` is not a
`ShizuStore/<major>.<minor>.<patch>` client (debug builds append
`-<commit>`, which still matches); requests without the header count as
non-client. Scope is **every** path: `/v1/*`, `/icons/*`, `/healthz`,
404s, cache hits and 429s. Two exceptions are never logged: `GET /`, the
browser-facing redirect to the project repo (browsers, bots and scanners hit
it constantly and it says nothing about API use), and operator `/v1/admin/*`
traffic. The row stores the request line, all
request headers as `jsonb` and the raw reconstructed request (both verbatim,
`Authorization` included, nothing truncated), plus transport fields: socket
peer, `CF-Connecting-IP` (else the first `X-Forwarded-For` hop), raw
`X-Forwarded-For`, `CF-Ray`, `CF-IPCountry`, status and duration. Hits go to
the same kind of bounded drop-on-full channel and `RequestLogWorker` writes
them to `request_logs` every `RequestLog:FlushInterval` and on shutdown;
flush failures are logged and never kill the host. There is no body to store
(no endpoint accepts one). DB-only, no endpoint, no pruning: rows are kept
until an operator deletes them.

Scraper poisoning: `ResponsePoisonFilter` (global MVC action filter) gives
requests whose `User-Agent` matches a configured prefix (`Poison:UserAgents`,
case-insensitive) doctored `GET /v1/apps` and `GET /v1/apps/{slug}` payloads:
list rows are cross-pollinated or replaced with plausible random values while
identity fields (slug, name, listing) stay intact; detail requests serve a
random other served row with the same availability as their data source
(identity fields and URLs still answer for the requested slug, category and
package data stay the donor's so they remain coherent), download URLs/hashes
are broken, and permission/screenshot/download lists lose entries. The filter
also strips `If-None-Match` for those callers, so they
can never revalidate a clean ETag into a 304. `SkipPoisonedRequestsPolicy`
is attached to the `apps-list` and `app-detail` output-cache policies only:
matching requests bypass both cache lookup and storage, so a poisoned body
is never served from the cache and a poisoned request never replaces the
cached clean entry. All other endpoints and User-Agents are untouched.

Startup gate: after `builder.Build()`, the host probes
`aapt2 version`, `apksigner --version` (30s/60s timeouts),
`gradle --version` (2min), and `git --version` (30s) and **refuses to boot**
(`InvalidOperationException`) when any is missing or exits
non-zero - a server without its toolchain would serve a catalog
that never enriches. Skipped only when the host
environment is `Testing` (the integration-test host sets it).

## 3. Data model (`Core/Data/`)

Tables are snake_case, `bigint` identity PKs, enums stored as
strings. Migrations live next to the context; `dotnet ef migrations
bundle` is rebuilt per deploy, never committed.

- **categories** - `slug` unique; self-referencing `parent_id`
  (max depth 2 in real data); `section` (`apps|libraries|misc`).
- **apps** - one awesome-list entry. `slug` globally unique and
  **stable across renames**. `url` is deliberately **non-unique**
  across listings, but within one listing a URL maps to a single row:
  identity is `(listing, url)`. When the source lists the same URL under
  several categories, the first occurrence owns the row and the rest are
  ignored, so the catalog never shows the same app twice.
  - Multi-app repos: enrichment creates one extra `apps` row per
    distinct APK label found among the release's assets (§5.2). A
    variant row copies the root's list-facing fields, points
    `root_app_id` at the list row, and carries its own `package_name`,
    downloads and icon. Builds that share the root's label but not its
    package (FOSS, Play, debug or spoofed flavors) stay on the root as
    extra `app_downloads` rows whose `package_name` distinguishes them,
    so one entry offers every flavor as a source. Only root rows
    (`root_app_id` null) take part in list matching and staling;
    variants cascade-delete with their root.
  - `display_name` is what clients show (`name` stays the awesome-list
    name): the analyzed APK's `application-label`, or for a
    multi-package root `label (root list name)` unless the label already
    equals the list name. `apk_label` keeps the
    raw label.
  - List fields: `name`, `description`, `license`, `listing`
    (`main|closed_source`), `type` (`app|library|flow`), flags
    (`is_recommended`, `has_paid`, `has_iap`, `has_ads`,
    `trial_days`, `requires_root`), `parent_id` (nested entries),
    `url`, `source_url`, `source_kind`, `availability`, `store_url`.
    `trial_days` is the day count of an `n-day trial` tag; a trial with
    another unit (for example `15-minute trial`) is stored as `0`, and an
    entry without a trial tag leaves it null.
  - Enrichment fields: `package_name`, `icon_hash`, `icon_adaptive`,
    `author_key`/`author_name`/`author_url` (stable developer identity:
    `github:<owner>` or `gitlab:<group>`; summary-visible so clients can
    group by developer),     `permissions` (the analyzed APK's `uses-permission` list, aapt2
    analysis only, never F-Droid index metadata; recorded for every
    analyzed build whatever its source),
    `full_description` (GitHub/GitLab README markdown, or scraped plain-text Play
    description, sent only on the detail endpoint), `changelog` (latest release
    notes: GitHub release `body` or GitLab release `description` markdown, else
    the F-Droid/Izzy index application `<desc>` long description; empty after a
    checked pass with none; sent only on the detail endpoint), `changelog_url`
    (browser page of the release the notes came from; null for index-sourced
    notes; sent only on the detail endpoint), `screenshots`
    (screenshot URLs: the F-Droid/Izzy `index-v2.json` result for every
    package name the app publishes, else raw URLs lifted from the app's own
    GitHub/GitLab tree when the indexes carry none; every rescan rebuilds
    the list from what the sources return, capped at 12, sent only
    on the detail endpoint), `screenshots_checked_at` (last repo lookup,
    throttles re-verification to
    `Enrichment:RepoScreenshotsRecheckInterval`), `enrich_etag` (conditional-request ETag
    reuse), `stars` (GitHub stargazers or GitLab star count), `download_total` (popularity, §8), `install_count`
    (successful installs reported by clients via
    `POST /v1/apps/{slug}/installs`; monotonic, never bumps `updated_at`), `version_updated_at`
    (release date of the currently served APK version; null when the
    source publishes none, and nulls sort last), `list_updated_at`
    (when the awesome-list entry was last added or edited, `[silent]`
    commits excluded; drives "recently added", nulls sort last),
    `last_checked_at`,
    `last_error` (trimmed to 500 chars).
  - `exclude_override` (operator flag, never touched by the
    upserter), `excluded_reason`, `added_at`/`updated_at` (git
    history, §4; `updated_at` is the change clock, not the release
    date).
  - Indexes on `url`, `updated_at`, `availability`, `category_id`.
- **app_downloads** - one row per (signing identity, ABI) of an app's
  installable builds. Replaces the removed per-candidate `apps`
  columns (`version_code`, `version_name`, `apk_url`, `apk_size`,
  `apk_sha256`, `apk_archive_entry`, `min_sdk`, `sig_sha256`,
  `sig_md5`, `apk_source`, `apk_source_ref`, and the 7 `fdroid_*`
  columns). Columns: `id`, `app_id` (FK cascade), `source`
  (`GitHub|GitLab|Codeberg|FDroid|Izzy|Play|Other`), `source_ref`,
  `package_name` (the package this build installs; differs per flavor),
  `apk_url`, `archive_entry`, `version_code`, `version_name`,
  `size_bytes`, `sha256`, `sig_sha256`, `sig_md5`, `analyzed`, `min_sdk`,
  `target_sdk`, `compile_sdk`, `locales`, `dhizuku_declared`, `trackers`,
  `tracker_signatures`, `abi`, `sig_key`, `is_primary`, `resolved_at`.
  - `sig_key` = lowercased first space-token of `sig_sha256`, else of
    `sig_md5`, else `url:<apk_url>`; `abi` = the analyzed APK's
    `native-code` ABI (null for fat/universal builds, or the F-Droid
    `nativecode` element on index-only rows). Unique
    `(app_id, package_name, sig_key, abi)` with NULLS NOT DISTINCT, so
    same-signature same-ABI candidates (reproducible F-Droid builds,
    Izzy mirrors of forge builds) collapse into one row while the
    per-architecture APKs of one release stay separate, and two flavors
    that share a signer and version never overwrite each other.
  - Fingerprints: analyzed rows record the `apksigner` digests
    (space-joined sets, matched by membership). Index-only F-Droid/Izzy
    rows take the signing-cert SHA-256 from the repo's `index-v2.json`
    (`packages.<id>.versions.<fileSha>.manifest.signer.sha256`); the
    legacy v1 `index.xml` `<sig>` is an F-Droid-specific fingerprint
    (MD5 over the certificate hex), not a certificate digest, so it is
    never recorded.
  - `analyzed` = true once the recorded build was downloaded and
    inspected (badging plus signer extraction), false for index-only
    rows; never downgraded. Only analyzed rows re-run the permission
    heal.
  - `target_sdk`/`compile_sdk`/`locales` come from badging on analyzed
    rows only; `locales` is newline-joined (the `--_--` pseudo-locale is
    dropped) and stays empty on index-only rows.
  - Analysis signals (analyzed rows only, empty elsewhere):
    `dhizuku_declared` = the build declares a
    `com.rosan.dhizuku.permission.*` permission; `trackers` = names and
    `tracker_signatures` = the matched code signatures of the Exodus
    trackers found in the DEX. Only code signatures are matched (the
    server does no network analysis), so an empty tracker list means
    "not detected by code signature", never "tracker-free". The Shizuku
    permission is intentionally not tracked: nearly every app in the
    catalog declares it, so it separates nothing.
  - Upsert: same `(package_name, sig_key, abi)` updates in place only
    when the new `version_code` is higher; on an equal `version_code`
    the preferred source's URL is kept; lower versions are ignored. A
    legacy row with a null `package_name` adopts the package of the
    candidate that claims it. A candidate whose APK `sha256` matches an
    existing row of the same package and ABI claims that row too, so
    index-only and analyzed twins created before the signer map merge in
    place. Non-null fingerprints are never cleared by an index-only
    update.
  - `is_primary` = fresh-install/no-match default. Selection prefers
    non-F-Droid sources (GitHub/GitLab/Izzy/Codeberg/Other count as
    forge-like), then higher `version_code`, then ABI (`null` universal
    first, then `arm64-v8a`, `armeabi-v7a`, `x86_64`, `x86`, then
    others), then a non-null `sig_sha256` (an analyzed identity beats an
    index-only twin), then a fixed source order. Exactly one primary per
    app (partial unique index on `app_id` where `is_primary`).
- **app_versions** - (`app_id`, `version_code`, `version_name`,
  `apk_url`, `detected_at`); a row is appended only when the
  `version_code` is unseen for the app (append-only history). The
  enricher must consult pending tracked rows, not only the database:
  a multi-ABI release applies one version code once per ABI within a
  single pass, and a duplicate insert would violate
  `IX_app_versions_app_id_version_code` and roll back the whole
  enrich.
- **sync_runs** - audit log of passes: `trigger`, `head_commit`,
  per-bucket counts (added/updated/removed/enriched/up-to-date/
  failed, drained requests, parse warnings, archived changes),
  `issue_count` (rows in the health snapshot this pass wrote),
  `skipped` flag, `error`. A `Skipped` pass
  writes **no row** (clean audit log, not a heartbeat table).
- **sync_issues** - current catalog health snapshot: `sync_run_id`
  (FK cascade), `kind` (`parse|enrich|quality`), `rule`, `app_id`
  (nullable, set null on delete), `slug`, `message`, `location`
  (parser context, parse rows only), `created_at`. Holds only the
  latest completed run: successful passes replace all rows, skipped
  and failed passes leave the previous good snapshot in place.
- **sync_requests** - webhook queue (`reason`, `processed`); rows
  are marked processed only on pass success, so a failed pass never
  loses its trigger.
- **removed_apps** - tombstones closing the delete path:
  `slug` unique + `removed_at` index. Written (add-or-refresh) on
  stale delete and when the Shizuku-permission gate (§5.3) excludes a
  row, cleared on re-add (resurrection) or when the gate heals a row.
- **config_flags** - operator-controlled flags (`key` PK, `value`,
  `updated_at`), read live on every use so flips need no restart. A
  missing row reads as its documented default; `GET`s never insert.
  Known keys: `use_install_counts_for_popularity` (`true` makes
  clients sort popularity by `install_count` and show it in list
  subtitles; default false), `catalog_purge_requested_at` (ISO-8601
  high-water mark for remote catalog purges; `/v1/changes` surfaces it
  as `catalogPurgeRequestedAt` and a client that recorded an older
  value wipes its cached app list and downloads, never favourites or
  blocklist, then re-bootstraps; missing or unparsable reads as null).
  Set it with SQL and leave the row in place so offline clients still
  catch it:
  `INSERT INTO config_flags (key, value, updated_at) VALUES
  ('catalog_purge_requested_at', now()::text, now())
  ON CONFLICT (key) DO UPDATE SET value = now()::text, updated_at = now();`
- **package_exceptions** - operator overrides for the Shizuku-permission
  gate (§5.3): `package_name` unique, `action` (`allow` or `dontaudit`),
  optional `note`, timestamps. Seeded by migration (`rish-mcp` is
  `allow`), edited with SQL; no admin endpoint.
- **app_download_exclusions** - operator overrides that hide one APK
  package from one list entry: unique `(app_slug, package_name)`,
  optional `note`, timestamps. Applied during enrichment (§5.1), seeded
  with SQL; no admin endpoint. Scoping by `app_slug` keeps the same
  package available on other entries that legitimately ship it.
- **client_user_agents** - anonymous usage aggregate per `User-Agent`
  string (`user_agent` unique, truncated to 512 chars): `request_count`,
  `first_seen_at`, `last_seen_at`, `last_path` (512 chars, nullable).
  `/v1/admin/*`, `/healthz`, `/icons/*`, UA-less and rate-limited
  requests are never recorded (see §2). Read with SQL; no endpoint.
- **client_user_agent_days** - per-UTC-day split of the same hits:
  unique `(user_agent, day)`, `request_count`. The day comes from the
  server's UTC clock, so day boundaries follow UTC regardless of client
  timezone.
- **app_install_days** - per-app per-UTC-day install counts: PK
  `(app_id, day)` (FK to `apps`, cascade delete), `install_count`.
  Upserted by `POST /v1/apps/{slug}/installs` in the same transaction
  as the total counter; rejected (unknown or `excluded`) reports write
  nothing. The day comes from the server's UTC clock. Read with SQL; no
  endpoint.
- **app_version_install_days** - per-app per-version per-type install
  counts: PK `(app_id, version_code, install_type, day)` (FK to `apps`,
  cascade delete), `install_count`. `version_code` 0 and `install_type`
  `unknown` stand in for reports that omit the optional body fields;
  `fresh` and `update` are the accepted report types. Upserted by
  `POST /v1/apps/{slug}/installs` in the same transaction as
  `app_install_days`. Read with SQL; no endpoint.
- **request_logs** - append-only log of non-client requests (see §2):
  `seen_at` (indexed), the request line (`method`, `path`,
  `query_string`, `raw_target`, `protocol`, `scheme`, `host`), the
  reconstructed `raw_request` (request line + headers, no body),
  `headers` as `jsonb` (verbatim, `Authorization`/`Cookie` included),
  `status_code` (smallint), `duration_ms`, `user_agent`, `origin`,
  `remote_ip` (socket peer; loopback behind the tunnel),
  `client_ip` (`CF-Connecting-IP`, else first `X-Forwarded-For` hop),
  `forwarded_for`, `cf_ray`, `country` (`CF-IPCountry`) and `trace_id`.
  All string columns are uncapped `text`: Kestrel's request-line (8 KiB)
  and header-total (32 KiB) limits are the only bound. No pruning;
  delete rows with SQL. Read with SQL; no endpoint.

## 4. List ingestion

- **Parser** (`Parsing/`, Markdig): reads `README.md` (listing
  `main`) and `pages/CLOSED_SOURCE.md` (listing `closed_source`),
  and only their `## Apps` sections: Development libraries and
  Miscellaneous content stay out of the catalog. A missing closed
  list parses as empty, so a mirror without it serves main only and
  old closed rows sweep out as stale. Hierarchy (categories,
  subcategories, nested child entries) and per-entry
  flags/license/source links are preserved.
- **History** (`History/GitHistoryService`): one
  `git log --reverse -p` pass per list file (`README.md` and
  `pages/CLOSED_SOURCE.md`); the first
  `+* [Name](url)` sighting of a URL is `added_at`, the last is
  `updated_at`. Commits flagged `[silent]` are housekeeping and are
  skipped, matching the published changelog. A URL listed in both
  files keeps its main-list dates. The upserter seeds
  `list_updated_at` from that last non-silent sighting (the changelog's
  "recently changed" clock) and re-seeds `added_at`/`list_updated_at` on
  every pass; afterwards
  `updated_at` is the change clock (any summary change bumps it, §3/§8)
  while `version_updated_at` tracks the served APK version.
- **Upserter** (`Sync/CatalogUpserter`): matches rows by
  `(listing, url)`. Rows from sections no longer ingested
  (libraries, misc) sweep out as stale, and closed rows sweep when the
  closed list drops them. Same-URL
  rename keeps id + slug and moves the row to the new category; a URL
  repeated in another category is a duplicate and is skipped. Stale
  `(url, category)` pairs, including duplicates created by earlier
  syncs, are hard-deleted and reported by slug. Only root rows
  (`root_app_id` null) are matched or swept: a variant shares its
  root's URL, so it must never look like a duplicate or go stale on
  its own. First-seen rows and stale-row tombstones are stamped with
  the write clock (`DateTimeOffset.UtcNow` inside the upsert), not the
  pass clock: `now` is captured before fetch + history parsing, so a
  client that syncs while the pass runs would hold a cursor past it and
  the new rows or removals would never reach `/v1/changes`.
- **ARCHIVED.md** (`pages/ARCHIVED.md`, applied after upsert):
  listed URLs (entry + source links, recursive incl. children) are
  marked `Excluded` with a fixed reason; un-listed apps are
  un-marked (reason cleared, check fields nulled). An emptied file
  still clears - emptiness is meaningful, there is no early return.
  The page is a flat list whose entries sit directly under its `##`
  section, so the parser treats that section as an implicit category
  for this file (no category headings are required, and none of the
  flat-list entries raise the uncategorized warning).

## 5. Enrichment pipeline (`Enrichment/`, `Sources/`)

Per-app, no `SaveChanges` (callers batch). A freshness gate runs
first: unless `force`, apps checked inside the success window (24h)
or failure backoff (12h) return `SkippedFresh` with no work.

Resolution is **forge-first, always**: GitHub → GitLab →
F-Droid/Izzy → fallback. A forge link anywhere in the entry (primary
or source URL) wins the primary APK; F-Droid is never primary while a
forge source exists. Play + forge combos keep Play as `store_url`.

Conditional source requests replay the stored `enrich_etag` via
`If-None-Match` through `ConditionalRequest.ApplyIfNoneMatch`, which
parses with `EntityTagHeaderValue.TryParse`: GitHub weak validators
(`W/"..."`) are replayed and malformed tags are skipped instead of
throwing (one bad tag previously failed that app's pass forever). A 304
reuses the prior result; forge 304s still refresh `stars`, the GitLab
README while missing, and a list-linked README on every pass). Rows with
a fully analyzed build on
record (SHA-256 identity) but blank permissions re-analyze once: the
F-Droid path never persisted them before, and the same-asset and 304
short-circuits would otherwise keep them blank forever (live:
FindMyDevice). A 304 heal refetches once without the validator (the
release list for GitHub/GitLab, the index for F-Droid), so steady
state stays validator-gated.

When a forge fails with no usable APK (no release, no `.apk` asset
and no archive carrying one), the app falls back to the F-Droid main
index: the `<application>` whose `<source>` URL matches the entry's
forge repo. There is **no source lock**: candidates resolve
symmetrically. A forge primary also records the f-droid.org candidate
(best-effort), and an F-Droid/Izzy primary also tries a forge
candidate. Every resolved build lands as a signature-keyed
`app_downloads` row (§6); failure of the candidate side never changes
the primary outcome.

f-droid.org hosts F-Droid community **rebuilds** (a different signing
key than the developer unless the build is reproducible).
IzzyOnDroid (`apt.izzysoft.de`) hosts the **developers' own upstream
builds** (normally crawled from GitHub/GitLab), so Izzy builds are
signature-compatible with forge releases; `is_primary` preference
therefore treats Izzy as forge-like.

- **GitHub** (`GitHubReleaseClient`, raw HttpClient + PAT from
  config or `SHIZU_GITHUB_TOKEN`): newest non-draft of the first
  100 releases (prereleases count: many Shizuku apps ship only
  prereleases); `EnrichEtag` drives `If-None-Match`,
  304 → `UpToDate` (only `last_checked_at` touched).
  `ApkAssetSelector` prefers a `release`-named `.apk`, else the
  largest `.apk`. If the release ships no `.apk`, a `.zip` asset is
  downloaded and the APK inside it is analyzed (some projects attach
  only a release bundle): `apk_url` stays the archive download,
  `size_bytes`/`sha256` describe the archive, and `archive_entry`
  names the APK inside. An unusable archive with
  no APK falls through to the F-Droid fallback, else `Failed`.
  Every other `.apk` asset in the release is analyzed as its own
  candidate (non-primary), so one-APK-per-architecture releases
  expose every ABI (§6); assets naming different packages from a
  multi-app repo become variant rows (§5.2). Each asset's `digest`
  is mapped to `SourceAsset.Sha256` when it is a `sha256:` value
  (bare lowercase hex), which lets an unchanged asset skip its
  download entirely (checksum short-circuit, §5.1). Asset URLs embed
  the release tag, so an unchanged recorded URL with every sibling
  already recorded and a complete stored row short-circuits to
  `UpToDate` (`asset URL unchanged`) without downloading; the digest is
  verified whenever the source declares one. GitHub allows replacing an
  asset under the same tag: a moved URL still downloads the bytes but
  verifies them against the recorded primary hash, so identical content
  skips re-analysis (the rare undetected replacement is accepted when no
  digest is declared). The release markdown
  `body` is captured as the app's `changelog`, and the release's
  `html_url` as `changelog_url`.
- **GitLab** (`GitLabReleaseClient`, `PRIVATE-TOKEN` from config or
  `SHIZU_GITLAB_TOKEN`): skips `upcoming` releases, prefers
  `direct_asset_url`. The release markdown `description` is captured as
  the app's `changelog` (the web release page `_links.self` as
  `changelog_url`); APK links embedded in that description
  (AuroraStore style) are collected too; relative `/uploads/...`
  links resolve through
  `https://gitlab.com/api/v4/projects/{urlencoded-path}{url}` (the
  web-UI uploads routes 404 or redirect to sign-in). Links match by
  name or URL, so generic labels like `APK` still resolve (e.g.
  narektor/batt links to `Batt-1.3.apk`). GitLab largely
  ignores `If-None-Match`, so an unchanged recorded asset URL
  short-circuits to `UpToDate`. Project metadata (`GET
  /projects/{urlencoded-path}`) refreshes `stars` from `star_count` on
  every pass, even when the release list fails; the README comes from
  the project's `readme_url` through `/repository/files/…/raw`
  (markdown source, like GitHub). Every `.apk` link becomes a
  candidate (primary plus per-architecture siblings, analyzed through
  the shared release pipeline). No `.apk` link → F-Droid fallback,
  else `Failed`.
- **F-Droid/Izzy** (`FdroidRepoClient` + singleton
  `FdroidIndexProvider`): conditional GET of `{base}/index.xml`
  (cached or seed ETag; 304 without cache → `UpToDate`). Each repo is
  fetched at most once per pass: `BeginRun` resets the run memo, a
  failed fetch is remembered so a refusing host is not hammered by
  every app, and one in-flight fetch per repo keeps parallel
  enrichments from stampeding. Both bases are overridable
  (`Enrichment:FdroidRepoBase`, `Enrichment:IzzyRepoBase`) and the
  Izzy base has one fallback mirror
  (`Enrichment:IzzyRepoBaseFallback`) because the official host
  refuses datacenter IPs.
  The streaming parser collects every `<package>` per
  `<application>` (document order, newest first); version/versioncode/sig are child
  **elements** (package attributes accepted as fallback), `<sig>`
  is the 32-hex signing-cert MD5. The application-level `<desc>` (long
  description, HTML) is captured as the app's `changelog` (index-sourced
  notes have no release page, so `changelog_url` stays null). A second
  conditional GET of `{base}/index-v2.json` (its own per-repo ETag cache)
  supplies `screenshots`: the preferred `phone` form factor and `en-US`
  locale set becomes absolute upstream URLs for every package name the
  app publishes (capped at 12); both repos are checked whatever the app's
  primary source, so forge apps also gain shots when published on F-Droid
  or Izzy. The repos fail independently: an unreachable repo keeps the
  URLs it contributed earlier and never discards the other repo's fresh
  hits.
  When both indexes end up with nothing, the app's own repo is the last
  resort: a blobless, no-checkout shallow clone
  (`git clone --depth 1 --filter=blob:none --no-checkout`) lists the tree
  and keeps image paths (png/jpg/jpeg/webp/gif/bmp) whose file name starts
  with `screenshot` or that live under a directory containing `screenshot`
  (fastlane `phoneScreenshots`, `docs/screenshots`, ...); a source package
  merely named `screenshot` with no image does not count. Raw URLs are
  pinned to the fetched commit (GitHub
  `raw.githubusercontent.com/{owner}/{repo}/{sha}/{path}`, GitLab
  `gitlab.com/{project}/-/raw/{sha}/{path}`), capped at 12.
  Resolution rebuilds the list instead of only filling holes: an index
  that answers replaces the index-sourced URLs wholesale (an empty answer
  clears them) and repo leftovers are dropped, so one source can never pin
  URLs the other source does not serve. Repo-sourced URLs stay until the
  recheck window elapses, then the repo tree decides their fate: a listed
  tree replaces or clears them, a failed clone keeps them. A lookup stamps
  `screenshots_checked_at`, so repo URLs are re-verified every
  `Enrichment:RepoScreenshotsRecheckInterval` (7 days);
  `POST /v1/admin/refresh-screenshots` forces a lookup for every served
  app regardless of the window. Same apk URL + version code →
  `UpToDate` with zero downloads (tightened to also require the
  recorded SHA-256 to match the index `sha256` when the index
  declares one, and a complete row: `package_name` and `icon_hash`
  set, the `{icon_hash}.png` file present, no pending permission
  heal). Otherwise the APK is downloaded
  and fully analyzed like a forge build (§5.1); unparseable files
  fall back to the index-only record, a package-name mismatch
  fails the pass (index row and file disagree), a package missing
  from the index fails with `not in`. A hit upserts a signature-keyed
  `app_downloads` row (`source` = FDroid/Izzy, `source_ref` = package
  id, §6). Siblings that share the primary's version name and carry a
  distinct `nativecode` ABI are recorded as index-only candidates (no
  download); stale sibling rows are pruned. Index lookups by source URL
  (`FindPackageBySourceAsync`) power the forge fallback.
- **Fallback** (no forge/F-Droid APK available): letter-avatar icon,
  `LinkOnly` - or `PlayRedirect` with `StoreUrl` for Play entries.
  When a Play listing is linked (including on apps whose forge has no
  APK at all), the page is scraped once for the icon (`og:image`), the
  package id (from the listing URL), the developer name/profile
  (`author_key` = `play:<devId>`), the version name, the full
  description and the last-update date (`VersionUpdatedAt`), so
  external-only entries still carry developer, version and details; no
  download row is recorded and the client shows an open-in-Play-store /
  open-externally button based on the entry URL. The fallback also runs when a forge source exists but
  yields no release or no APK asset (for example a GitHub repo with no
  release), so those entries become a Play redirect instead of failing
  the pass.
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
LayoutLib via the Paparazzi Gradle tool (`tools/icon-render`): a single
render writes the exact-size bitmap directly (no screen-sized snapshot),
a failed or timed-out build still contributes a complete PNG when one
was written, artifacts of the same package share one render per app pass,
and a full pass with `Enrichment:BatchIconsOnFullPass` defers the XML
levels to the batched icon phase after enrichment (while deferred, the inline
analysis resolves rasters but never writes an icon, so a fallback raster
cannot downgrade an existing adaptive icon; the batch phase owns icon
commits). With `Enrichment:IconRenderScope` the Gradle call runs in a
transient systemd user scope (`systemd-run --user --scope`) with its own
memory/CPU boundaries, so render JVMs are never charged to the API unit's
cgroup (an over-budget render is throttled or killed inside the scope
instead of stalling the API in cgroup reclaim). Batched rendering is chunked
at `Enrichment:IconBatchChunkSize` icons per Gradle invocation; each chunk
runs with `--no-daemon`, so no render JVM survives into the next chunk and
memory cannot accumulate across a backfill. A failed chunk fails only its
own apps and the refresh continues with the next chunk; the warm daemon left
by single renders is stopped once, after the last chunk (`gradle --stop`,
also after a cancelled refresh).
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
LayoutLib, so background/foreground resolve in a fixed-size view)
and writes an exact-size PNG per icon. A launcher reveals only the
central 72dp of the 108dp adaptive viewport, so an adaptive root is
drawn scaled 108/72 about the view center, which clips the 18dp
bleed per side and leaves the masked art filling the frame; plain
vectors and rasters draw full-bleed. The service then normalizes
(a defensive top-left crop only guards against a misbehaving
renderer). The zoom changes adaptive renders byte-for-byte, so
existing adaptive rows adopt new hashes on the next refresh. Renders are serialized through
chunked Gradle invocations (`renderIconBatch` over a manifest of
`name|root|out` lines with per-app filename prefixes, at most
`Enrichment:IconBatchChunkSize` icons per chunk; a single
`renderIcon` remains for one-offs), because each invocation pays a
full task graph + test-JVM + LayoutLib boot; chunks run with
`--no-daemon` so their JVMs exit between chunks and one failed chunk
cannot strand the rest. When
`Enrichment:IconRenderCpuAffinity` is set, the renderer launches
Gradle through `taskset -c <list>`, so the warm daemon and its
forked test JVMs stay within the allowed cores. F-Droid primaries fall back to
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

Checksum short-circuit: a full re-check must not redo APK work when
the bytes are unchanged. When the source declares the artifact
checksum (GitHub assets expose `digest`, `sha256:<hex>`, normalized
to bare lowercase hex on `SourceAsset.Sha256`; F-Droid exposes the
index `sha256`), the download is skipped entirely when the recorded
download for that exact asset URL carries the same `sha256` and its
owning row is complete: `package_name` and `icon_hash` set, the
`{icon_hash}.png` file present on disk, no pending permission heal,
and the scanned release is not newer than the row's
`version_updated_at` (`ReleasedNewerThan`). When the source declares
no checksum, the artifact is downloaded anyway and its SHA-256 is
computed first: a match against the recorded or source-declared hash
returns `Unchanged`, so aapt2, apksigner and the icon render are all
skipped. A skipped artifact still stamps `last_checked_at`, and the
apply pass still runs so vanished variants prune and display names
recompute. A newer release date or a changed checksum always
re-analyzes; any gap (missing package, icon hash, or icon file)
heals on the next pass.

Outcomes: `Enriched`, `UpToDate`, `AvatarFallback`, `Excluded`,
`Failed` (`last_error` set, previous good values kept),
`SkippedFresh`. Every completed check stamps `last_checked_at` (and
clears `last_error`), including analyses whose build does not become
primary: an unstamped success stays due forever and is re-downloaded
every pass. `BulkEnricher` fans out over app ids with a
`SemaphoreSlim` (default 4), preserving order, isolating faults;
cancellation propagates.

`--refresh-icons [--force]` is a one-shot that re-resolves every
`DirectApk` icon from the primary download's URL without touching
versions: byte-identical renders
stay `UpToDate` (missing files are still rewritten), changed
renders adopt the new hash. `--force` additionally recounts
identical renders as refreshed, which surfaces self-consistent
wrong files (e.g. two swapped icons whose hashes matched their
rows). It writes no `sync_runs` row. The CLI form runs as a second process
and must not overlap a live server (both write `apps` and share the Gradle
tool dir); to heal a running deployment use
`POST /v1/admin/refresh-icons`, which runs the same work inside the server
under the sync gate.

`--sync-once [--full] [--skip-apk]` runs one sync pass in-process and
exits (run with the server stopped, like `--refresh-icons`). `--full`
selects every app like the nightly; without it the fast pass runs.
`--skip-apk` forces `Enrichment:SkipApkAnalysis`, a metadata-only mode:
release feeds, changelogs, screenshots and stars refresh, but every APK
download/analysis reports `Unchanged` (recorded downloads and icons stay
as they are) and F-Droid index rows fall back to index-only metadata, so
a full pass backfills metadata without spending APK bandwidth or CPU.
`--refresh-icons` refuses to run while the switch is set, and the switch
is meant for one run (`--skip-apk` or an env override), not for
appsettings.

Icons are normalized to ≤192px PNGs, stored content-addressed as
`{sha256}.png`, and served immutable. Letter-avatars are
deterministic 192px PNGs (name-hashed background, embedded glyph).
Every icon carries an `icon_adaptive` flag (`iconAdaptive` in the
app DTOs): true only when the served file renders an
`<adaptive-icon>` root (the central 72dp masked view, safe for
rounded-square framing); plain vectors render full-bleed too but
stay false, as do decoded rasters and avatars (framed in a squircle
box). Refresh passes re-sync the flag without icon churn.

### 5.2 Multi-app repos (one list entry, several packages)

A repo may ship several distinct applications. Every `.apk` asset of the
scanned release is analyzed (not only the primary), and the analyses are
grouped by the APK's `application-label` (trimmed, case-insensitive;
blank labels stay separate). One label is the root's: the group matching
the root's stored `apk_label` when it has one, else the group carrying
the list URL's package, the stored `package_name`, or the dot-prefix
base package. Within that group the root takes the canonical package:
the list endpoint's package (`f-droid.org`, Izzy, Play) when the list
URL names one, else the dot-prefix base of the group (the shortest
package that every other package extends), else the package containing
the label's or repo's identity token (for example `com.dergoogler.mmrl`
for label `MMRL`), else the primary asset's package. The canonical build
is the representative one: it drives the root's `package_name`,
`apk_label`, permissions and icon, and is the only package the primary
selection may choose. The other same-label packages become extra
candidates on the root, each `app_downloads` row carrying its own
`package_name`, so one entry lists FOSS, Play, debug or spoofed flavors
as separate sources.

Every other label becomes a variant `apps` row (§3) with its own slug
(the slugified package, deduped), downloads and icon. A completed root
check stamps every live variant's `last_checked_at` too: variants are
only ever enriched through the root's pass, and the skip paths that
touch just the root (unchanged release, 304, all-assets-known) would
otherwise freeze their timestamps until the health snapshot flags them
stale after two windows. The root's
`display_name` and every variant's is the APK's `application-label`;
when a root has more than one label the label is qualified as
`label (root list name)` so the extra apps stay traceable to their list
entry, except when the label already equals the list name (then the
parenthetical is dropped, never `Name (Name)`). A label that disappears
from the release is pruned with its downloads, but only when no sibling
analysis failed (a partial fetch must not delete rows); the pruned
variant's slug is written as a change-feed `removed[]` tombstone so
cached clients drop the child row. Candidate-only resolution (the
opposite-signature candidate side, §5) never creates variants.

Legacy variant rows whose stored `apk_label` matches the root's and
whose package is now a same-label flavor are folded back into the root:
their downloads move onto the root candidate set (keeping their
package), the row is deleted and tombstoned, and the root's `updated_at`
bumps so cached clients refetch the merged entry.

Operator exclusions (`app_download_exclusions`, §3) are applied before
grouping: a release artifact whose package is excluded for the entry is
dropped, and any stored candidate for it is deleted. When the root's
stored `package_name` is the excluded one it is cleared, so the
root-group selector falls back to a surviving group and the root rebinds
to the remaining package; a same-label legacy variant then folds in as
above. A variant whose only package was excluded is pruned and
tombstoned. When a release offers only excluded packages and no
non-excluded candidate is stored, nothing is dropped, so an operator
typo cannot empty a row. But when a non-excluded candidate is already
stored and only excluded artifacts were scanned this pass (a source
without asset digests skips the stored sibling), the stored state wins
and the excluded analysis is discarded, so a later partial pass cannot
silently undo the exclusion. This is how `shizukuplus` serves only its
unique-package APK without dropping the drop-in build's package from
the unrelated Shizuku entry.

`KieronQuinn/SmartspacerPlugins` publishes one plugin per GitHub
release, so the newest-release scan cannot see them: it is a hard-coded
special case that fetches **all** non-draft releases, flattens their
assets, and feeds them through the same grouping. Because a
latest-release compare is meaningless there, `ReleasePoller` skips that
repo and it stays on the due window (§7.2).

Root presentation heal: a repo whose newest release no longer carries
the root's recorded primary artifact (for example DroidOS, whose latest
release is F-Droid-only while the Pro launcher sits on an older release)
would otherwise leave the root row with a null `apk_label` and stale
permissions, because the primary guard only writes presentation fields
for a representative analysis. When the root's `apk_label` is null and
none of the scanned analyses covers the recorded primary URL, the
enricher downloads and analyzes that recorded primary once and fills
`package_name`, `apk_label`, `permissions` and the icon. The heal never
records the download, never recomputes the primary, and never sets the
etag, so it cannot reorder candidates or rewrite a row's version; once
the label is set it is a no-op.

Form factors: a release may ship the same package as phone, TV and Wear
builds (for example universal-installer's `app`, `tv` and `wearos`
release APKs). The APK's required `uses-feature` entries identify them
(`android.software.leanback` or `android.hardware.type.television` is
TV, `android.hardware.type.watch` is Wear). When a package also ships a
phone build, its TV and Wear artifacts are dropped before the analyses
are applied, so the phone build stays the offered candidate; a package
that only ships a TV or Wear build is kept, because then that form
factor is the app. The filter is grouped by package, so one package's
phone build never suppresses another package's watch-only variant.

### 5.3 Shizuku-permission gate

A repo can also ship APKs that do not need Shizuku at all, so every
`DirectApk` row must prove it declares a Shizuku permission. After
enrichment each pass, `SyncService.ApplyShizukuFilterAsync` looks at the
rows whose analyzed `permissions` are known: a row whose permissions
contain `shizuku` (case-insensitive, covering `moe.shizuku`,
`rikka.shizuku`, `dev.rikka.shizuku`, `af.shizuku`, `moe.shizuku.api`)
stays available. AndroidX
`*.DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION` entries are artifacts and
never match.

Only `DirectApk` rows are gated: Play-redirect and link-only rows carry
no analyzed permissions. A gated row with no permission and no operator
exception is set to `Excluded` with reason
`APK does not declare a Shizuku permission.`; the same reason on an
older row is cleared (back to `DirectApk`) as soon as a later APK
declares the permission. The gate never touches another exclusion reason
(for example the ARCHIVED one), and `exclude_override` acts as an
implicit allow.

Excluding a row also writes a `removed_apps` tombstone (§3): `/v1/changes`
hides excluded rows from `added`/`updated` and reports removal only through
tombstones, so without one a client that cached the app before the gate
keeps it forever. The write is idempotent and backfills rows excluded
before tombstoning existed. A heal clears the tombstone and bumps
`updated_at` so the app reaches clients as an update rather than a stale
add. Both the tombstone `removed_at` and the heal `updated_at` carry the
commit time, not the pass start: the gate runs after enrichment, and a
client that synced mid-pass would otherwise hold a cursor past the tombstone
and never see it.

`package_exceptions` holds operator overrides keyed by `package_name`
(table §3), seeded by migration and edited with SQL. `allow` keeps the
row available and emits no issue; `dontaudit` excludes it like the
default but emits no issue. Rows excluded by this gate stay in the due
and full-recheck selection, so a new release that declares the
permission auto-heals them (the checksum short-circuit keeps the repeat
check cheap).

`GET /v1/issues` reports a `shizuku_permission_missing` quality issue
for every gated row that lacks the permission and has no
`package_exceptions` row, whichever action; the message is the exclusion
reason above. This check runs in `CollectIssuesAsync` rather than
`CatalogHealthCheck` because the latter ignores excluded rows.

## 6. Signature-keyed downloads

Each `app_downloads` row is one (package, signing identity, ABI).
`sig_key` is the lowercased first space-token of `sig_sha256`, else of
`sig_md5`, else `url:<apk_url>`; `abi` is the analyzed APK's
`native-code` ABI (null for fat/universal builds, or the F-Droid
`nativecode` element on index-only rows); `package_name` is the package
the build installs. Unique `(app_id, package_name, sig_key, abi)` with
NULLS NOT DISTINCT, so same-signature same-ABI candidates (reproducible
F-Droid builds, Izzy mirrors of forge builds) collapse into one row,
the per-architecture APKs of one release stay separate, and two flavors
that share a signer and version never overwrite each other. Fingerprints
come from `apksigner` (every `Signer #N certificate … digest` line is
collected - key rotation yields space-joined sets matched by
membership; MD5 is optional for old build-tools). Index-only F-Droid/Izzy
rows take the signing-cert SHA-256 from the repo's `index-v2.json`
(`packages.<id>.versions.<fileSha>.manifest.signer.sha256`); the legacy
v1 `index.xml` `<sig>` is an F-Droid-specific fingerprint (MD5 over the
certificate hex), not a certificate digest, and is never used for
identity or client matching.

Upsert rule: a candidate with the same `package_name`, `sig_key` and
`abi` updates the row in place only when its `version_code` is higher;
on an equal `version_code` the preferred source's URL is kept; a lower
version is ignored. A candidate whose APK `sha256` matches an existing
row of the same package and ABI claims that row, so an index-only row
(no SHA-256 identity) upgrades in place once analysis or the index-v2
signer map reveals its identity, and twins created before the signer map
merge instead of multiplying. Non-null fingerprints are never cleared
by an index-only update. A legacy row with a null `package_name` is
claimed by the matching candidate instead of spawning a twin.

`is_primary` marks the default candidate for fresh installs / clients
with no fingerprint match. Selection: non-F-Droid sources first
(GitHub/GitLab/Izzy/Codeberg/Other all count as forge-like), then
higher `version_code`, then ABI (`null` universal first, then
`arm64-v8a`, `armeabi-v7a`, `x86_64`, `x86`, then others), then a
non-null `sig_sha256` (an analyzed identity beats an index-only
twin), then a fixed source order. Exactly one primary per app
(partial unique index on `app_id` where `is_primary`). This keeps a release that ships only
per-architecture APKs (BiliDownOut-style) from defaulting to whichever
asset happens to be largest.

Client contract: hash the installed app's signing cert and filter
`downloads[]` to candidates whose `sigSha256`/`sigMd5` match
(membership match; fingerprints may be space-joined sets). Among
matches, prefer an `abi` that is null or in the device's supported ABI
list. Then compare the candidate's `versionCode` against the installed
version (do not compare against other candidates). With no match,
offer the primary for a fresh install. Never switch a user between
different signatures.

## 7. Sync engine

`SyncService.RunAsync` wraps everything: any non-cancellation
exception clears the change tracker and lands as an error `SyncRun`
row. A pass:

1. Drains unprocessed `sync_requests` (any rows → trigger
   `"webhook"`; marked processed only on success).
2. Best-effort `git fetch` (5-min timeout; offline/timeout →
   continue off the local clone) followed by a fast-forward of the
   current branch to its upstream (`git merge --ff-only`), so the
   working tree the pass reads is current. The clone is never written
   to locally, so a fast-forward that cannot apply (diverged history,
   dirty tree) deletes the clone and re-clones it from origin.
3. Compares HEAD against the latest run's commit: unchanged HEAD +
   no requests + no `force` → enrich due-only apps plus
   poll-changed apps (forced) and write the row, or return
   `Skipped` (no row) when neither has anything.
4. Else full pass: read + parse `README.md` (Apps section) →
   history → upsert (the empty closed doc sweeps stale listings) →
   `ARCHIVED.md` → enrich selection (full re-check: all
   non-excluded with `force=true`; else the client-side due
   window plus poll-changed extras, forced) fanned out per-app through `BulkEnricher` with
   `IEnrichmentRunner` (fresh scope per app, persists its own
   save; vanished rows count `Failed`) → mark requests processed +
   write the run row plus the health snapshot (§7.1). No app mutations
   happen after the upsert save, so the final save writes only
   requests + run + issues.

### 7.1 Health snapshot (`sync_issues`, served by `GET /v1/issues`)

Each successful pass replaces the snapshot: parse rows from this
pass's warnings (README plus `ARCHIVED.md`), enrich rows from every
non-excluded row currently carrying `last_error`, quality rows from
`CatalogHealthCheck` (missing license/description/icon, non-http
entry or source URL, direct-APK rows without package name or primary
download, never-checked or twice-window-stale rows; excluded rows are
never checked, and a variant shares its root's check freshness because
the root's completed pass stamps the whole variant group). Due-only
passes (HEAD unchanged) keep the previous
parse rows and refresh only enrich + quality rows. Skipped and failed
passes write no issues, so a crashed pass keeps the last good snapshot.
Staleness is evaluated against the pass clock. The run row records the
snapshot size in `issue_count`.

Workers (`Web/Sync/`): `SyncWorker` runs one pass at startup
(`RunOnStartup`, the first-boot backfill) then ticks on a
`PeriodicTimer` (`FastLoopMinutes`, floored at 1); `NightlyWorker`
sleeps until the next strictly-future `NightlyTimeUtc` (HH:mm UTC,
default 03:00; invalid values disable it with a log) and triggers
full re-checks. `SyncGate` single-flights passes - a contested tick
skips.

### 7.2 Fast-path release poll (`Core/Sync/ReleasePoller`)

Every non-nightly pass cheaply checks whether apps still inside
their re-check window released something new, and force-enriches
the changed ones. The poll is metadata-only (release feeds plus
one F-Droid/Izzy index fetch per repo, no APK download, no aapt2,
no DB writes) and compares against the recorded downloads:
GitHub/GitLab compare the picked APK/zip asset URL against the
primary download (the stored ETag rides along, so unchanged GitHub
feeds answer 304); a root with variant rows (§5.2) instead compares
the release's whole `.apk` URL set against the union of the root's
and its variants' download URLs, since no single primary represents
them; F-Droid/Izzy compare the index version code
and APK URL against the matching download row. Play, link-only,
Codeberg and the GitCode special case have no cheap signal and
stay on the due window; instafel's list entry is polled through
its real release repo (instafel/u-rel), while SmartspacerPlugins is
skipped (its apps span separate releases, §5.2). Skipped apps (excluded) and
rows without a matching download count as changed, so they enrich
on the next pass. Poll failures are soft (unchanged): a flapping
upstream never marks rows failed, and a throwing poller degrades
the pass to due-only enrichment. The poll needs a GitHub PAT
(anonymous limits cover 60 calls/hr); without one it stays off
with a startup warning and fast passes enrich due-only apps.

### 7.3 Enrichment run log (`Core/Sync/RunLog.cs`)

Opt-in append-only human-readable log (`Enrichment:RunLogPath`;
null writes nothing, which keeps tests side-effect free). Every
sync pass writes one clearly separated section: a header
(`===== run <utc> | trigger=scheduled|nightly|webhook |
mode=full|due | apps=N =====`), then one line per scanned app as it
finishes, streamed through `BulkEnricher`'s `onResult` hook, then a
totals footer (`ok`/`skip`/`fail` plus duration), then the same
catalog health snapshot that `GET /v1/issues` serves (issue counts
by kind, then one line per issue). An app line is
`[ 12/315] slug (Display Name)  OK|ok|skip|excluded|FAIL  detail`;
the detail carries the failure message or the `EnrichResult.Detail`
skip reason (for example `APK unchanged, analysis skipped`, `within
recheck window`, `release feed not modified`, `asset URL
unchanged`, `index not modified`). Between app lines the log also
carries timestamped detail lines for slow actions, so a pass that
stalls can be attributed: `download start <url>` /
`download done <bytes>B in <ms>ms`, `analyze <apk> unchanged,
hashed <bytes>B in <ms>ms` or `analyze <apk> badging Xms,
signers Yms, icon Zms, total Tms`, `github|gitlab|gitcode release
list <target> ok|304|failed in <ms>ms`, `render <drawable>
start|done|salvaged|failed in <ms>ms`, `batch render N icons
start|done x/N in <ms>ms` (once per chunk), and `gradle --stop done
after refresh (exit N)` when the post-refresh daemon stop ran. A pass that finds nothing due
writes a single `nothing due` line so the cadence stays visible,
and a crashed pass writes a `pass failed: <msg>` line. File IO
failures are warned once and never fail the pass. The log rotates
at run boundaries once the file reaches `maxBytes` (default 1 MiB):
the active file is moved to `<path>.1` (overwriting the previous
`.1`) and a fresh active file starts, so exactly two files are kept
and every section stays whole.

## 8. API behavior (`/v1/*`)

Snake_case wire format (`main|closed_source`, `app|library|flow`,
`apps|libraries|misc`, `github|gitlab|codeberg|fdroid|izzy|play|
other`, `direct_apk|play_redirect|link_only|excluded`). `excluded`
rows are never returned (detail reads them as 404). Closed-source rows
are served only when `listing` explicitly includes `closed_source`:
the default is main-only, so closed entries stay out of every list,
count and delta unless a client opts in. Summary/list DTOs
source `versionCode`/`versionName`/`minSdk`/`size`/`sigSha256`/`sigMd5`
from the primary download (`size` is the primary APK's `size_bytes`, null
when unknown).

`name` is the app's `display_name` when enrichment has learned it (the
analyzed APK's label, §5.2) and the awesome-list name otherwise; variant
rows are served the same way, so clients see the qualified label.

`stars` (GitHub stargazers or GitLab star count) and `downloadTotal` are best-effort
popularity fields on both summary and detail; null when unknown.
`downloadTotal` reflects the app's primary source: the sum of GitHub
release asset `download_count` over all non-draft releases, or the
IzzyOnDroid rolling-year download count for Izzy-primary apps.
f-droid.org publishes no per-app download counts (its API lists
versions only), so F-Droid-primary apps have none. `sort=stars`/
`sort=downloads` order by them (nulls last on descending).

`versionUpdatedAt` (summary + detail) is the release date of the
currently served APK version: GitHub `published_at` or GitLab
`released_at`; F-Droid-primary apps and sources that publish no date
stay null (they sort last). It only advances when a newer primary version
is served, so metadata-only refreshes (icon, stars, desc) never move
it. The server's `sort=updated` still orders by `updated_at`; clients
use `versionUpdatedAt` for their "Recently updated" list so metadata
churn does not surface as a new version.

`listUpdatedAt` (summary + detail) is the last add or edit of the
awesome-list entry from git history, with `[silent]` commits excluded.
It matches the published changelog ordering, so clients use it for
"Recently added" (an entry edited recently moves up, exactly like the
changelog). Rows without git history stay null and sort last. The
server's `sort=added` still orders by `added_at` (first sighting).

`authorKey`/`authorName` (GitHub owner or GitLab namespace) ride on both
summary and detail so clients can group apps by developer; `authorUrl`
(profile link) is detail-only. `permissions` (the analyzed APK's
requested permissions, aapt2-sourced only), `fullDescription` (GitHub/GitLab
README markdown, or plain-text Play description, capped at 200k chars; when
the list entry's `app.Url` is itself a markdown README, e.g. a localized
`README_EN.md` on a non-English landing page, that linked file wins over the
repo default) and
`changelog` (latest release notes: GitHub release `body` or GitLab release
`description` markdown, else the F-Droid/Izzy index `<desc>`, capped at 100k
chars), `changelogUrl` (browser release page the notes came from, null for
index-sourced notes) and `screenshots` (absolute upstream image URLs from the
F-Droid/Izzy `index-v2.json`, or pinned raw repo URLs when those indexes
carry none, capped at 12) are
detail-only: they are deliberately absent from summaries and
`/v1/changes`, and clients keep the README, changelog and screenshots in memory
rather than persisting them.

| Endpoint | Behavior |
|---|---|
| `GET /v1/apps` | Filters: `category` (subtree incl. subcategories, unknown → 400), `q` (case-insensitive contains over name/description/package), `license` (case-insensitive exact), `availability`/`type` (parse or 400), `listing` (comma-separated `main|closed_source`, default `main`, unknown → 400), `recommended` (`true|false` or 400). `page` ≥ 1 else 400; `pageSize` clamped 1–200, default 50. `sort` ∈ `updated|added|name|stars|downloads` (default `updated`, else 400); `order` ∈ `asc|desc`, default desc except `name` → asc. Ordering + paging run in memory (identical semantics on both DB providers). Output-cached 60s, `VaryByQuery(*)`. Poisoned User-Agents get doctored rows and bypass the cache (§2). |
| `GET /v1/apps/{slug}` | Full detail: summary fields + URLs, `source_kind`, version, `category_path` (root→leaf) + `parent_slug`, `added_at`, `last_checked_at`, `author_url`, `permissions[]`, `full_description`, `changelog`, `changelog_url`, `screenshots[]`, `downloads[]` (primary first, then `versionCode` desc; each entry: `source`, `packageName`, `apkUrl`, `archiveEntry`, `versionCode`, `versionName`, `size`, `sha256`, `sigSha256`, `sigMd5`, `minSdk`, `abi`, `primary`). Top-level version/sig fields come from the primary download; the old flattened `apkUrl`/`apkSize`/`apkSha256`/`apkArchiveEntry` fields and the `fdroidVariant` object are gone. When `apkUrl` is a zip, `archiveEntry` names the APK inside (clients must extract it). ETag `"{ticks}-{id}"`; `If-None-Match` → 304. Output-cached 60s. Poisoned User-Agents get a random other row's doctored downloads/lists, never a 304, and bypass the cache (§2). |
| `GET /v1/categories` | Tree with per-node subtree app counts over the requested `listing` set (comma-separated, default `main`; excluded omitted). Roots and children are name-sorted (case-insensitive, id breaks ties). ETag from count + id-sum + max `updated_at`; `If-None-Match` → 304. Output-cached 5min. |
| `GET /v1/changes?since=` | `since` required ISO-8601 else 400. Optional `listing` (comma-separated, default `main`, else 400) scopes every bucket. `added` (`added_at` ≥ since), `updated` (`updated_at` ≥ since but added before), `removed` (tombstones ≥ since) - all oldest-first, excluded hidden. `installsUpdated` maps slug → install count for rows whose count moved since `since` (`install_count_updated_at` ≥ since); it carries no summaries, so clients apply it onto stored rows without refetching. `catalogPurgeRequestedAt` is the `config_flags` high-water mark for remote catalog purges (null = never requested); a client that recorded an older value wipes its cached app list and downloads (never user data) and bootstraps. Output-cached 30s, `VaryByQuery(*)`. |
| `GET /v1/issues` | Health snapshot from the latest completed run: `runId`, `headCommit` (null before the first pass), `summary` (parse/enrich/quality/total counts over the whole snapshot), `items[]` (`kind`, `rule`, `slug`, `message`, `location`) oldest by kind/rule/slug. Filters: `kind` (`parse\|enrich\|quality`, else 400), `rule` (exact). `page` ≥ 1 else 400; `pageSize` clamped 1–200, default 50. Summary counts ignore the filters. ETag `"runId-count"`; `If-None-Match` → 304. Output-cached 30s, `VaryByQuery(*)`. |
| `GET /v1/meta` | `generated_at`, latest run's `list_commit` (null before the first pass), counts (non-excluded apps, categories), `use_install_counts_for_popularity` (the `config_flags` row below; missing row reads as false). Output-cached 60s. |
| `GET /healthz` | `{"status":"ok"}`. No rate limit, no cache. |
| `GET /` | 302 to the project repo (`https://github.com/timschneeb/ShizuStore`); the bare host is a browser entry point, not part of the API. No rate limit, no cache. |
| `GET /icons/{sha}.png` | 64-hex sha else 400; missing file → 404; served as a physical file with manual immutable 1-day `Cache-Control` (no output-cache attribute - its filter would overwrite the header). No rate limit. |
| `POST /v1/admin/sync` | Webhook: token from `Admin:Token`, `SHIZU_ADMIN_TOKEN` or the legacy `SHIZU_ADMIN_SECRET`, else fail-closed 503. Requires `Authorization: Bearer <token>` (constant-time compare, bodies > 4KB rejected) else 401. Inserts a `sync_requests` row and wakes the fast loop immediately → 202 `{queued:true}`; a request that lands while a pass is running becomes an immediate follow-up pass instead of waiting for the next tick (the row stays pending until that follow-up drains it). |
| `POST /v1/admin/refresh-icons` | Same token rules. Starts the in-process icon refresh (`RefreshIconsAsync`) and returns 202 with the running status; poll `GET` for progress. The run takes the shared sync gate, so it serializes with the fast and nightly passes (they skip and retry) while the API keeps serving reads; one run at a time, a pass already holding the gate or `Enrichment:SkipApkAnalysis` → 409. Optional body `{"force":true}` recounts byte-identical renders as refreshed (default false). Does not write a `sync_runs` row. |
| `GET /v1/admin/refresh-icons` | Same token rules. Current refresh status: `state` (`idle\|running\|completed\|failed`), `force`, `startedAt`/`finishedAt`, `checked`/`refreshed`/`alreadyCurrent`/`failed`, `errors[]`, `error`. |
| `DELETE /v1/admin/refresh-icons` | Same token rules. Cancels the running refresh → 202, or 409 when nothing is running. |
| `POST /v1/admin/refresh-screenshots` | Same token rules. Starts the in-process screenshots refresh (`RefreshScreenshotsAsync`) and returns 202 with the running status; poll `GET` for progress. Re-resolves F-Droid/Izzy for every served app and forces the repo lookup when the indexes carry nothing, even inside the per-app recheck window; stored repo URLs are dropped when an index supplies shots. Takes the shared sync gate exactly like `refresh-icons`. Does not write a `sync_runs` row. |
| `GET /v1/admin/refresh-screenshots` | Same token rules. Current status: `state` (`idle\|running\|completed\|failed`), `startedAt`/`finishedAt`, `checked`/`updated`/`current`/`failed`, `errors[]`, `error`. |
| `DELETE /v1/admin/refresh-screenshots` | Same token rules. Cancels the running pass → 202, or 409 when nothing is running. |
| `POST /v1/apps/{slug}/installs` | Records one successful client install: atomically increments the app's `installCount` and stamps `install_count_updated_at` (→ 200 `{slug, installCount}` with the new total). Unknown or `excluded` slugs → 404. In the same transaction it upserts the `app_install_days` row for the server's current UTC day (`app_id` + `day`, count +1), plus an `app_version_install_days` row keyed on the optional JSON body `{ "versionCode": 123, "installType": "fresh\|update" }`; absent or malformed bodies and out-of-range codes fall back to `versionCode` 0 / `installType` `unknown`, and anything other than `fresh`/`update` (case-insensitive) normalizes to `unknown`, so older clients stay valid. The counter bypasses `UpdatedAt`, so install reports never appear in added/updated and never invalidate detail ETags; the move surfaces only via `installsUpdated` in `/v1/changes`. |

Rate limit (`/v1/*` only): fixed window, 100 req/min/IP, no queue (→ 429).
Caching: server-side output cache per the table above. Dynamic GET
endpoints send `Cache-Control: no-store`, so clients and the edge never
reuse an API response (only `/icons/*` advertises a cache lifetime; a
cached `/v1/changes` would make a client refresh a no-op). Responses are
compressed (Brotli preferred, gzip fallback) and advertise `Vary:
Accept-Encoding`; compression sits outside the output cache so cached bodies
stay uncompressed and compress on the way out. OpenAPI document is served in
all environments; Scalar UI is development-only.

## 9. Behavior knobs (code-level defaults)

| Option | Default | Effect |
|---|---|---|
| `Api:RateLimitPerMinute` | `100` | Fixed-window limit per client IP |
| `Api:EnableOutputCache` | `true` | Server-side GET caching (tests disable it) |
| `Poison:Enabled` | `true` | Doctored list/detail payloads for scraper User-Agents (§2) |
| `Poison:UserAgents` | `["python-httpx/0.28.1"]` | User-Agent prefixes (case-insensitive) that get poisoned |
| `UserAgentTracking:Enabled` | `true` | Anonymous per-UA request stats, DB-only (§2) |
| `UserAgentTracking:FlushInterval` | `00:00:10` | Buffer flush period; also flushed on shutdown |
| `UserAgentTracking:MaxBufferedHits` | `2000` | Bounded hit buffer; overflow is dropped |
| `RequestLog:Enabled` | `true` | Log non-ShizuStore requests on all paths except `/` and `/v1/admin/*`, DB-only (§2) |
| `RequestLog:FlushInterval` | `00:00:10` | Buffer flush period; also flushed on shutdown |
| `RequestLog:MaxBufferedHits` | `2000` | Bounded hit buffer; overflow is dropped |
| `Enrichment:Aapt2Path` / `ApksignerPath` | `aapt2` / `apksigner` | Binaries, verified at startup |
| `Enrichment:GradlePath` | `gradle` | Gradle binary for icon renders, verified at startup |
| `Enrichment:IconToolDir` | `tools/icon-render` | Paparazzi tool checkout |
| `Enrichment:FdroidRepoBase` | `null` (upstream f-droid.org) | F-Droid repo base override; set a mirror such as `https://ftp.fau.de/fdroid/repo/` on hosts that f-droid.org throttles |
| `Enrichment:IzzyRepoBase` | `null` (upstream apt.izzysoft.de) | IzzyOnDroid base override; the official host refuses datacenter IPs, production uses `https://izzy.katastima.org/fdroid/repo/` |
| `Enrichment:IzzyRepoBaseFallback` | `null` | Second Izzy mirror tried once when the primary base fails; production uses `https://izzy.zw.is/fdroid/repo/` |
| `Enrichment:PaparazziTimeout` | `15min` | Per-icon render timeout |
| `Enrichment:BatchIconsOnFullPass` | `false` | Full rechecks resolve rasters only and batch-render XML icons after enrichment; fast passes render per icon |
| `Enrichment:IconBatchChunkSize` | `50` | Most icons one batch Gradle invocation renders; chunks run with `--no-daemon` and a failed chunk only fails its own apps |
| `Enrichment:IconRenderCpuAffinity` | `null` | `taskset -c` CPU list for the render JVM (for example `0` pins renders to one core); null uses every core |
| `Enrichment:IconRenderScope` | `false` | Run each Gradle render in a transient `systemd-run --user --scope` (under `nice -n 5`) with its own memory/CPU bounds, so render JVMs are not charged to the API unit's cgroup; needs linger plus `XDG_RUNTIME_DIR` and `ProtectHome=read-only` |
| `Enrichment:SystemdRunPath` | `systemd-run` | `systemd-run` binary for the render scope, verified at startup when the scope is on |
| `Enrichment:IconRenderScopeMemoryHigh` | `1G` | Render scope `MemoryHigh` |
| `Enrichment:IconRenderScopeMemoryMax` | `1400M` | Render scope `MemoryMax`; only the render is OOM-killed at this boundary |
| `Enrichment:IconRenderScopeSwapMax` | `0` | Render scope `MemorySwapMax`; keeps render memory out of swap |
| `Enrichment:IconRenderScopeCpuQuota` | `150%` | Render scope `CPUQuota`; the API unit's own quota no longer covers the render |
| `Enrichment:IconStorePath` | `icons` | `{sha256}.png` icon store |
| `Enrichment:MaxParallelism` | `4` | Concurrent enrichments |
| `Enrichment:SuccessRecheckInterval` | `24h` | Healthy-app re-check window |
| `Enrichment:FailedRecheckInterval` | `12h` | Backoff after `last_error` |
| `Enrichment:DownloadTimeout` | `10min` | APK download HTTP timeout |
| `Enrichment:RunLogPath` | `null` | Append-only per-pass human-readable log (one line per app, timestamped slow-action details, plus the issues snapshot, §7.3); null disables it |
| `Enrichment:GitHubToken` / `GitLabToken` | `null` (+ `SHIZU_GITHUB_TOKEN` / `SHIZU_GITLAB_TOKEN` env fallback) | Release-API auth/rate limits |
| `Sync:ListPath` | `/opt/shizuappstore/list` | Local list clone |
| `Sync:FastLoopMinutes` | `15` | Fast-loop period (≥ 1) |
| `Sync:NightlyTimeUtc` | `03:00` | Full re-check time (UTC) |
| `Sync:RunOnStartup` | `true` | Boot pass (first boot = backfill) |
| `Sync:PollEnabled` | `true` | Fast-path release poll (§7.2); off without a GitHub PAT |
| `Sync:PollParallelism` | `8` | Concurrent release-metadata fetches in the poll |

## 10. Invariants & gotchas (do not break)

- Forge-first is policy: no change may make F-Droid primary while a
  forge link exists. Izzy is forge-like (it hosts the developers'
  own builds); only f-droid.org community rebuilds rank below forge.
- One row per package and signing identity: `(app_id, package_name,
  sig_key, abi)` is unique; a candidate never creates a second row for
  the same package and signature.
- Candidates never alter which source is primary other than through
  the `is_primary` rule (forge-like first, then higher
  `version_code`, then fixed source order); exactly one primary per
  app.
- Signatures govern client choice: clients filter `downloads[]` by
  the installed cert's fingerprint, compare that candidate's version,
  and never switch a user between signatures.
- `apps.url` is not an identity - always key on
  `(listing, url, category)`.
- Variant rows (`root_app_id` set) are not list rows: list matching,
  staling and due/recheck selection only ever touch roots, and
  candidate-only resolution never creates variants. Grouping is by APK
  label: packages that share the root's label are flavors on the root
  (each download keeps its `package_name`), and only a distinct label
  gets its own variant row.
- The checksum short-circuit only skips work: it may never change a
  primary, drop a download row, or leave a row half-healed. Skipping
  requires a complete row (package, icon hash, icon file, no heal
  pending) and an unchanged or older release date; a newer release
  always re-analyzes.
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
  (tests re-register no-op policies; the poisoned-UA policy in §2 is
  the only sanctioned bypass); never put `[ResponseCache]`
  on the icons action; no `ORDER BY`/`Max`/`Where` over
  `DateTimeOffset` in LINQ shared with SQLite tests - sort and
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
