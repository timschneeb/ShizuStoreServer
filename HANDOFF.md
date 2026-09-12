# HANDOFF — ShizuAppStoreServer context for a fresh agent

> Read this + `PLAN.md` before doing anything. This file is the full
> conversation history distilled: decisions, rejected alternatives, and why.

## 1. People, repos, landscape

- **User:** Tim (`timschneeb`), Android dev (RootlessJamesDSP, GalaxyBudsClient, …).
- **List repo:** `/home/tim/Development/awesome-shizuku` — curated awesome-list of
  Shizuku apps (Shizuku = ADB-privilege framework for non-root Android).
  558-line `README.md`, plus `pages/CLOSED_SOURCE.md`, `pages/ARCHIVED.md`,
  `pages/RISH.md`, each with `_cn`/`_tw` translations (parse EN only).
  Ingestion reads the README `## Apps` section only; `CLOSED_SOURCE.md`
  is intentionally ignored (Play-only proprietary entries).
  CI in `.github/workflows`, validation in `scripts/lint.py` + `scripts/check_links.py`.
- **This repo:** `/home/tim/Development/ShizuAppStoreServer` — C# backend for the store.
  Contains `PLAN.md` (build plan, milestones) + this file.
- **Related local repos (reference only):**
  - `../trackawesomelist-source` was claimed but **does not exist locally**; the real
    one is `github.com/timschneeb/trackawesomelist-source` (fork of trackawesomelist,
    **Deno, AGPL-3.0**). It parses awesome-list markdown → JSON + git-blame change
    detection (`parser/`, `format-markdown-item.ts`, `format-category.ts`,
    config `type: list`). Its output powers `github.com/timschneeb/changelog-awesome-shizuku`
    (daily changelogs, linked from the README). **Never copy its files (AGPL);**
    reimplementing grammar ideas is fine. Use its output as a cross-check oracle.
  - `../app-crawler` — user's automated crawler finding new Shizuku projects on
    GitHub/F-Droid (referenced in README note). Untouched so far; potential future
    enrichment source (discovery of new entries).

## 2. Decision history (why things are the way they are)

- **Monetization v1 (dead):** Publish store app on Google Play, core free + open
  source, paywalled push notifications (~$0.50) via Play Billing, plus remove-ads /
  donation tiers. Killed because:
- **The pivot:** user will **NOT upload to Play at all**. It is a *proper* sideloaded
  app store (APK installs via `PackageInstaller`). Consequence: no Play Billing, no
  Play policies to obey — but also no Play distribution. Monetization now =
  **donations/supporter links only** (website + in-app links, no payment SDKs).
  The paid-push-notifications idea was explicitly **scrapped**; the
  `/v1/changes?since=` feed is a *free* client-sync primitive.
- **Play-policy research (now moot but recorded):** Play forbids in-app APK
  distribution (directory + deep-links would have been required) and mandates Play
  Billing for digital goods. Neither applies post-pivot.
- **Name journey:** ShizukuIndex → must be descriptive → `Shizu` abbreviation OK,
  multi-word OK → `ShizukuCatalogApi` → rejected as solution name (sounds like the
  repo *contains* the catalog) → `ShizukuCatalogService` (with `.Api` subproject)
  → user proposed **`ShizuAppStoreServer`** → initially pushed back ("Store"
  implies APK distribution, a Play-policy smell) → **approved after the pivot**,
  since it really is a store backend now.
- **Arch evolution:** Api+Worker projects + Docker Compose + SQLite →
  **Postgres** (user choice) → **single binary + single systemd service, no
  Docker** (user deploys bare-metal) → deploy root **`/opt/shizuappstore/`**.
- **F-Droid repo idea (rejected):** self-hosted F-Droid repo was suggested (free
  compat with Droid-ify/Neo Store). User: **own appstore only**, no F-Droid
  repo pattern/API structure.
- **APK mirroring (rejected):** no re-hosting. Download from GitHub/GitLab/F-Droid
  at enrich time (extract icon, then delete); clients install from upstream URLs.
  Only icons are hosted. Exception path: Play redirect when no other source.
- **Scope:** all entries **except Play-sole-source ones** (e.g. paid + closed-source
  with only a Play link → `availability=excluded`). Play+GitHub combos (Canta,
  Inure…) → `direct_apk` via GitHub. **No Play scraping whatsoever.**
- **DB verification (open):** asked whether to run local Postgres here for
  integration tests or defer to the server — **unanswered, still open** (see §6).

## M3 decisions (enricher)

- **No Octokit:** raw `HttpClient` + `System.Text.Json` for the Releases
  API. The plan's stubbed-`HttpClient` tests want direct handler injection;
  the used surface (list releases, `If-None-Match`) is trivial. PAT from
  `SHIZU_GITHUB_TOKEN`, `X-GitHub-Api-Version: 2022-11-28`.
- **ImageSharp 2.1.11**, not 4.x: v4 needs a `sixlabors.lic` build key
  (warning breaks the 0-warning standard); 2.1.11 is pure Apache-2.0 and
  patched for CVE-2025-54575 (GIF DoS — irrelevant to APK PNGs, but clean).
  Revisit only if v3+ features are ever needed (<$1M grant would apply).
- **Real-aapt2 catch:** modern build-tools emit `minSdkVersion:'NN'`, not
  `sdkVersion:'NN'` — found by assembling a real APK locally and fixed
  before it ever reached the server. Parser accepts both; verbatim-output
  test locks the format.
- **Orphan-icon check reads `DbSet.Local` too:** the M6 loop enriches a
  batch before saving, so unsaved tracker state must count as "still used".
- Letter-avatars use an embedded 5×7 font (no system-font dependency →
  identical output on dev machine and server).

## 3. Client (Android app) — all ideas collected

- **Distribution:** sideloaded via GitHub releases. Own store app only.
- **Template: AuroraDroid** (Aurora OSS, GitLab, FOSS F-Droid client, native Android,
  `PackageInstaller`-based installs). Plan: fork it, **keep** browse/search UI
  patterns, details screen, installer session flow, signature/permission display
  (trust matters for sideloading); **rip out** F-Droid `index.xml` sync/parse and
  multi-repo management UI; **replace with** Retrofit + Room syncing against this
  server's `/v1/*` endpoints.
- **Sync model:** `GET /v1/changes?since=` = incremental sync (new/updated badges,
  update checks). Never full dumps.
- **Per-availability client behavior:** `direct_apk` → download `apk_url` → system
  installer session; `play_redirect` → open Play listing; `link_only` → source page
  in Custom Tab; `excluded` → never sent to clients.
- **Push/update alerts:** no paywall (scrapped). FCM works sideloaded *where Play
  Services exists*; **polling/UnifiedPush fallback required** for de-Googled users
  (large overlap with Shizuku/F-Droid audience).
- **Monetization in client:** donation/supporter links only.
- **Milestone:** M7, separate repo, after backend M1–M6.

## 4. List data facts (parser-relevant)

- Entry grammar (== `scripts/lint.py`): `* [Name](url) [tags] - desc \`License\`
  [(Source code)](url)`. Pre-tags: `✨` recommended, `` `Paid`/`IAP`/`Ads`/`Root` ``,
  `` `N-day trial` `` (+ decorative `💰`). License typo `` `Propietary` `` exists
  in real data (RecentAppsTV, Tasker Settings) → normalize to `Proprietary`.
- Structure: `##` sections (skip Table of contents/Languages/License/Annotations +
  `>` quotes), `###` categories, `####` subcategories (only under Vendor-specific:
  Pixel/OneUI/MIUI/Other). Nested indented bullets = children (aShell → aShell
  You; Shizuku Keeper → Keeper Lite). Empty `### System` exists (zero entries).
  Heading `### Flows for [Automate](…)` contains a link (parse literal text).
  Descriptions contain inline markdown/links (e.g. CVE link in SMTShell).
- Entry kinds need a `type` field: apps, dev libraries, Automate flows, misc;
  only the Apps section is ingested now, the other kinds stay out.

## 5. Current implementation state (M1 + M2 DONE)

- Solution `ShizuAppStoreServer.slnx` (note: .NET 10 SDK defaults to **`.slnx`**,
  not `.sln`), `net10.0`, Nullable + ImplicitUsings on. SDKs installed: 6/8/9/10.
- `src/ShizuAppStoreServer` (webapi, controllers; template files
  WeatherForecast*/`.http` deleted), `src/ShizuAppStoreServer.Core`
  (`Class1.cs` deleted; Markdig 1.3.2 installed), `tests/…Core.Tests` (xUnit).
- M1: `Core/Parsing/{AwesomeListParser,ParsedModels,Slug}.cs`,
  `tests/…/AwesomeListParserTests.cs` — **build 0 warnings, tests green**,
  incl. full real-README parse.
- M2: `Core/Data/{Enums,Category,App,AppVersion,SyncRun,SyncRequest,ShizuDbContext,ShizuDbContextFactory}.cs`
  (snake_case tables per PLAN §3, enums as strings, `apps.url` **non-unique**:
  real data lists the same URL in several categories — verified `fluffy`,
  `krude`, `AlwaysOnDisplayToggle`) + `Migrations/InitialCreate` (+ SQL script
  + `efbundle` both generate cleanly) + `Core/History/GitHistoryService.cs`
  (`git log --reverse -p`, first `+* [Name](url)` sighting = `added_at`, last
  = `updated_at`, keyed by URL) + `Core/Sync/CatalogUpserter.cs` (match by
  `(listing,url,category)`; same-URL rename keeps id+slug; old-location-gone =
  move-reuse; old-location-still-parsed = keep duplicate rows; stale
  `(url,category)` pairs hard-deleted). Tests: `GitHistoryServiceTests`,
  `CatalogUpserterTests` (SQLite in-memory) — **build 0 warnings, 18/18 green**,
  incl. full real-list backfill (>300 apps, hierarchy, dup URLs, git history).
- Web host registers `ShizuDbContext` (Npgsql, `ConnectionStrings:Shizu`).
  EF package pins: `Microsoft.EntityFrameworkCore*` 10.0.12 explicit in
  Core+Web (Npgsql 10.0.3 drags in EF 10.0.4 → MSB3277 without the pins).
- M3: `Core/Sources/{SourceClassifier,GitHubReleaseClient,ApkAssetSelector}.cs`
  + `Core/Enrichment/{BadgingParser,Aapt2Runner,IconProcessor,LetterAvatarGenerator,AppEnricher,BulkEnricher,EnrichmentOptions}.cs`
  + `apps.enrich_etag` migration (`AddAppEnrichEtag`) + DI wiring in Program.cs
  (`Enrichment` config section, `SHIZU_GITHUB_TOKEN` env, typed HttpClients,
  `Aapt2Runner` singleton, scoped `AppEnricher`) + `docs/server-setup.md`.
  Tests: `SourcesTests`, `EnrichmentTests`, `AppEnricherTests` — **build
  0 warnings, 82/82 green** (all hermetic: stubbed HttpClient, fake aapt2,
  in-memory ZIP/PNG; plus env-gated `RealAapt2SmokeTests`, proven locally).
- M4: `Core/Sources/{GitLabReleaseClient,FdroidIndex,FdroidRepoClient}.cs`
  (GitLab releases via raw `HttpClient`, `PRIVATE-TOKEN` from
  `SHIZU_GITLAB_TOKEN`; streaming `index.xml` parser — first `<package>`
  per app; scope-cached provider, one fetch per repo per scope, ETag in
  `apps.enrich_etag`) + generic `ApkAssetSelector.PickApk` overload (GitLab
  links carry no size → API order breaks ties) + `IconProcessor`
  raw-image path + `apps.exclude_override` migration
  (`AddAppExcludeOverride`, clean `ADD COLUMN … DEFAULT FALSE`) + DI
  (`IGitLabReleaseClient`, `FdroidRepoClient`, scoped provider,
  `SHIZU_GITLAB_TOKEN` env). Enricher resolution order: GitHub → GitLab →
  F-Droid/Izzy → fallback; Play-sole-source (no usable source link; the
  CLOSED_SOURCE rows are no longer ingested) → `excluded` + reason unless override → then
  `play_redirect` + `store_url`; Play+non-forge-source → `play_redirect`
  (never excluded). F-Droid needs no APK download (index already has
  version/size/hash/minSdk; icon mirrored 640→legacy→avatar); same
  recorded asset/version → `UpToDate`. Tests: `SourcesTests`,
  `FdroidTests`, `AppEnricherTests` — **build 0 warnings, 139/139 green**.
- M5: `Core/Data/RemovedApp.cs` (table `removed_apps`, slug unique,
  `removed_at` index) + `AddRemovedApps` migration + upserter hook (stale
  delete writes/refreshes tombstone by slug; re-add clears it = resurrection)
  + `Api/{ApiEnums,Dtos,AppMapper,ApiOptions,AdminOptions}.cs` +
  `Controllers/{Apps,Categories,Changes,Meta,Health,Icons,Admin}.cs`:
  apps list (category-subtree, q/license/listing/availability/type/recommended
  filters, page clamp 1..200, sort updated|added|name, ETags) + detail by slug
  (404 for excluded, 304 support); categories tree with subtree counts +
  ETag; `changes?since=` added/updated/removed buckets oldest-first;
  meta (latest sync commit + counts); healthz (no limiter); icons
  `{sha}.png` (64-hex, immutable 1yr, manual header); admin webhook
  (hex-HMAC-SHA256 over raw body, `FixedTimeEquals`, 4KB cap → `SyncRequest`
  row + 202, 401 on bad signature, fail-closed 503 without secret).
  Program.cs: fixed-window limiter 100/min/IP (options resolved per request),
  output-cache policies (apps-list 60s vary-query\*, app-detail 60s,
  categories 5min, changes 30s, meta 60s; cache middleware before limiter so
  hits skip permits), `MapOpenApi` always + Scalar dev-only
  (`Scalar.AspNetCore` 2.17.3). SQLite provider limits (commented in code):
  no `ORDER BY`/`MAX`/comparison on `DateTimeOffset` in SQL → apps-list
  ordering+paging, changes filtering+ordering, categories MAX run client-side
  (catalog is tiny; Npgsql unaffected). Tests: new
  `tests/ShizuAppStoreServer.Web.Tests` (Mvc.Testing host with SQLite +
  option swaps, `NoOutputCachePolicy`) — full suite **0 warnings, 163/163
  green (142 Core + 21 Web)**, web suite stable across 3 runs.
- M6: `Core/Sync/{SyncOptions,IEnrichmentRunner,SyncService}.cs` —
  `SyncPassResult` (trigger/head/commit counts/drained/warnings/archived/
  skipped/error); `RunAsync` catches non-cancelled (incl.
  `ChangeTracker.Clear()` + error `SyncRun` row); `RunCoreAsync` drains
  `sync_requests` (trigger `"webhook"`, processed only on success),
  best-effort `git fetch` (5-min timeout, continues off the local clone),
  HEAD-vs-last-run gate (unchanged + no requests → due-only enrich or
  `Skipped` writing nothing), reads `README.md` (Apps section only;
  `pages/CLOSED_SOURCE.md` intentionally ignored, an empty `closed-source`
  document sweeps its old rows out), README-only git history (earliest
  added/latest updated per URL), upsert (non-Apps sections sweep out as
  stale; saves itself), `pages/ARCHIVED.md` mark/un-mark (no early return
  on empty set — emptied file must still clear), enrich selection
  (full re-check: all non-excluded `force=true`; else client-side due
  window — SQLite can't do `DateTimeOffset` arithmetic, M5 precedent),
  `BulkEnricher` over ids, final save writes only requests + run row.
  `GitHistoryService.FetchAsync`, `AppEnricher.EnrichAsync(force=false)`
  opt-in, `FdroidIndexProvider` scoped→singleton (revalidating cache,
  per-repo gates, no stampede), `BulkEnricher.EnrichManyAsync<T>`.
  `Web/Sync/{EnrichmentRunner,SyncGate,SyncPassRunner,SyncWorker,
  NightlyWorker}.cs` (contested ticks skip; `TimeUntilNextNightly`
  strictly future; invalid time disables with a log). Program.cs wires
  options/history/gate/runner + 2 hosted services; `appsettings.json`
  `Sync` section. `deploy/shizuappstore.service` (hardened,
  `EnvironmentFile=/etc/shizuappstore/env`) + `deploy/deploy.sh`
  (publish → `ef migrations bundle` → rsync → migrate → restart) +
  `linux-x64` single-file profile (framework-dependent, untrimmed —
  EF/Npgsql reflection). Verified: profile publish yields a 12 MB
  linux-x64 ELF, bundle builds (dotnet-ef 10.0.1 warns it is older than
  the 10.0.12 runtime — harmless), `bash -n` clean. Tests:
  `SyncServiceTests` (real temp git repo, fake runner, 9 tests incl.
  archived resurrection + failed-pass request retention),
  `NightlyDelayTests`, F-Droid singleton-concurrency tests; factory
  boots workers idle (`RunOnStartup=false`). Full suite **0 warnings,
  178/178 green (152 Core + 26 Web)**, run 2× stable.
   `docs/server-setup.md` gained the full deploy flow (`/etc/
   shizuappstore/env`, unit install, first-boot backfill notes).
- **M8 DONE** (signatures + F-Droid variant, implemented before M7 — the
  client needs the API): F-Droid builds are delayed + differently signed,
  so the server is forge-first and records both signers. `Core/
  CertFingerprint.cs` (normalize/join, space-joined rotation sets);
  `Core/Enrichment/{ApkSignerRunner,ApkSignerParser}.cs` (mirror of the
  aapt2 runner; parses every `Signer #N certificate … digest` line —
  SHA-256/SHA-1/MD5, MD5 optional for old build-tools); `EnrichmentOptions.
  ApksignerPath` (default `apksigner`, needs a JRE) + DI singleton +
  appsettings key. **M4 parser bug found by real data:** live
  `index.xml` (16 MB, 4364 apps, fetched 2026-09-12) carries
  version/versioncode/sig as child *elements* — the attribute reads
  always yielded 0/null (13 262 packages, zero with attributes); fixed
  with attribute-fallback + `<sig>` (32-hex cert MD5, same on Izzy);
  heals on next enrich via the version-change path. `App` gains
  `SigSha256`/`SigMd5` + 7 `Fdroid*` variant columns (`AddApkSignatures`
  migration, clean nullable ADD COLUMNs, script-reviewed). `AppEnricher`:
  forge paths extract sigs best-effort (never fail enrichment);
  `ResolveFdroidVariantAsync` sidecar after fresh forge enriches
  (package-name lookup in F-Droid main index; download + analyze on
  change with bytes-win, index-only fallback, refresh-md5 short-circuit,
  cleared when dropped; broad catch — sidecar never kills primary);
  F-Droid primaries download + analyze on change (APK icon preferred,
  package-mismatch Fails, sigs from file with index-md5 fallback,
  UpToDate short-circuit keeps zero-download steady state). DTOs:
  summary += `SigSha256`/`SigMd5`, detail += both + nullable
  `FdroidVariantDto`. Tests: `ApkSignerTests` (verbatim real output
  from a locally signed APK — keytool + apksigner 35.0.0 roundtrip in
  /tmp, plus env-gated live test proven passing), `FdroidTests` real-
  format regression, 8 new enricher tests (sigs, variant full/up-to-
  date/cleared/index-only, signer-absent, F-Droid download + mismatch),
  Web detail test for sigs/variant; existing F-Droid call-count asserts
  updated for the APK attempt (1→2, 2→3). Full suite **0 warnings,
  193/193 green (166 Core + 27 Web)**. `docs/server-setup.md` gained
  the apksigner/JRE block + client-matching contract.
- **SPEC DONE** (`docs/SPEC.md`, post-M8): implementation & design spec —
  layout, pipeline/DI order, data model, ingestion, enrichment, sigs/
  variant + client contract, sync engine, full API behavior table,
  behavior-knobs table, invariants/gotchas. Every statement verified
  against code during drafting (caught + fixed: default sort is
  `updated` desc, forge icon chain has no mirror step); ops content
  stays in `server-setup.md`.
- **LIVE TEST DONE (2026-09-12, post-SPEC):** first real end-to-end run,
  no mocks. User started the system Postgres (18.6, was installed but
  inactive); `shizu` role + `shizuappstore` db; all 5 migrations applied
  clean via `dotnet ef database update`. Mini list
  (`/tmp/shizu-live-list`, fresh git repo): CatShare + AutoDND
  (F-Droid-primary + GitHub source, both verified .apk releases) +
  Play-sole-source Tasker (closed list) + empty ARCHIVED.md. Boot pass
  on Debug DLL (:5110) proved: HEAD commit recorded, 2 apps DirectApk
  with real package/version/minSdk/hash, real apksigner fingerprints,
  F-Droid variants resolved (CatShare variant byte-identical to forge:
  same sha256 + sigs, reproducible build detected live), Tasker
  Excluded, icons 192px immutable, meta/changes/categories correct,
  webhook 202 + 401 paths + queue drain with `webhook` trigger,
  empty archive clears nothing and errors nothing. **Real bug found:**
  `GitHistoryService` preserved committer offsets (+02:00) and Npgsql
  threw writing them to timestamptz (SQLite never catches this);
  fixed via `DateTimeStyles.AdjustToUniversal` at parse +
  `SaveChanges[Async]` UTC normalization in `ShizuDbContext`
  (choke point, loses nothing: timestamptz stores instants), locked
  by 2 hermetic tests (`ParseLogNormalizesCommitDatesToUtc`,
  `SaveChangesNormalizesDateTimeOffsetsToUtc`). Full suite now
  **200/200 green (172 Core + 28 Web), 0 warnings**. Server left
  running on :5110 against the system DB (live-test rows inside).
- **TIMEOUT FIX (post-live-test):** pass 4 failed both apps with no
  `last_error`: an upstream timeout (TaskCanceledException/OCE)
  escaped `Fail()` entirely. Fix: `DispatchAsync` choke point converts
  non-cancellation OCE to recorded `Failed` ("Upstream timed out",
  backoff applies); genuine cancellation still propagates. Transport
  failures (DNS, TLS, reset) get the same treatment with the cause
  attached ("Upstream error: ..."). Locked by 3 tests (timeout
  recorded, transport recorded, cancellation propagates).
- **LAUNCHER ICONS (post-live-test; F-Droid mirror rescue reverted
  per user):** live icons were letter-avatars because badging
  `application-icon-*` lines resolve anydpi adaptive XML for every
  density. Final pipeline (`ILauncherIconService`, stateless
  singleton): badging rasters incl. WebP (ImageSharp built-in);
  else manifest `android:icon` resolved through `resources.arsc`
  with density selection (raster preferred, XML allowed); else the
  badging XML path itself; XML roots dispatch to adaptive
  (bg/fg layers), vector (full path set incl. arc rotation,
  fills, linear/radial gradients), shape, gradient, or layer-list
  rendering via a hand-rolled scanline rasterizer (4x supersampled).
  Binary AXML + arsc table ported from ApkQuickReader.cs
  (alisakkaf/ApkShellext, MIT, attributed in-file); Windows-only
  pieces (GDI+, WPF, SharpZipLib, WebPWrapper) replaced with
  BCL/ImageSharp; `aapt2 dump xmltree/resources` text scraping
  removed again. Proven live: CatShare serves its mipmap xxxhdpi
  WebP logo; AutoDND (zero raster launcher art) renders its
  adaptive vector, checked pixel-exact against the repo source
  (white `M19,13H7v-2h10v2z` bar on `#252525`). Out of scope by
  design: stroked-only paths, sweep gradients, clip paths.
  Suite 183 Core + 29 Web.
- **TOKEN WIRING BUG (full backfill, live):** the first full run
  failed ~285 apps with 403/429s despite a PAT in env. Root cause:
  `AddHttpClient` client ctors take an optional token string that DI
  fills with its default (null), and `Program.cs` never applied
  `enrichment.GitHubToken/GitLabToken` to the clients — every forge
  call always went anonymous (also explains the untouched 5000/hr gh
  quota). Invisible to tests because they construct clients directly.
  Fix: `Program.ConfigureEnrichmentClients` applies Bearer and
  PRIVATE-TOKEN headers from options; covered by
  `EnrichmentClientWiringTests` executing the production
  registration (mutation-checked: token test fails with wiring
  removed). Lesson: optional ctor params + typed clients fail open;
  secrets need wire-level tests. Suite 183 Core + 31 Web.
- **SILENT-FAIL INSTRUMENTATION (post-live-test):** runs 4/7/9 each
  failed both apps with no `last_error` and nothing logged; cause
  never isolated (transient infrastructure suspected: network and
  DB were both healthy minutes later, rate limits untouched).
  Defenses added so it cannot recur silently: `DispatchAsync`
  converts timeouts and transport errors to recorded `Failed`;
  `EnrichmentRunner` catches the rest, records the row error, and
  returns the message; `SyncPassResult.FailedMessages` (not a
  column) threads them to `SyncPassRunner` warnings. Locked by
  2 tests (threading via throwing runner, runner catch via DI).
  Runs 10-12 all clean.
- **SCRATCH LIVES OUTSIDE /tmp:** `/tmp/shizu-live-list` vanished
  mid-session (selectively; neighboring files survived) and killed
  two boots with git working-directory errors. Live-run state now:
  list clone is the real awesome-shizuku checkout (never a toy
  list), icons at `/var/tmp/shizu-full-run/icons` via
  `Enrichment__IconStorePath`. Treat /tmp as disposable in all
  future live runs.
- **MASS-DELETE INCIDENT (live, own fault):** restarted the server
  with `Sync__ListPath` still pointing at the 3-entry mini list;
  the boot pass saw HEAD change, found 382 stale rows, hard-deleted
  them all (added=0 updated=2 removed=382). Tombstones worked as
  designed; recovery was repointing at the real clone, whose next
  pass re-added all 385 rows and cleared all 382 tombstones.
  Mini list deleted afterwards so it cannot happen again. PROPOSAL
  (needs user decision): guardrail refusing passes that would
  remove more than a threshold share of the catalog (upstream wipe
  or parse failure must never nuke the store silently).
- **PAPARAZZI ICONS (user call, custom renderer deleted):** the
  hand-rolled VectorDrawable renderer kept producing broken images
  and would rot on new drawable features, so `VectorDrawable.cs`
  (1213 lines) is gone; vectors/adaptive icons now render through
  Google LayoutLib via Paparazzi (`tools/icon-render`, Gradle 8.14 +
  AGP 8.13.2 + Paparazzi 1.3.5 pinned). `DrawableStager` turns
  binary AXML into text XML under a flat generated namespace
  (`shizu_N.xml`, rasters to `drawable-nodpi`, literals inlined,
  framework refs/theme attrs abort the stage); `PaparazziRenderer`
  runs one `gradle renderIcon` per icon (record mode always writes
  fresh, the snapshot source is deleted after copying so a failed
  render can never serve a stale PNG); the service crops the
  top-left 432px square and normalizes. AdaptiveIconDrawable cannot
  inflate under LayoutLib (device mask string is launcher-only),
  so the test parses the adaptive XML and composites bg/fg layers
  full-bleed through real Android drawables in a fixed-size view.
  Gradle lies about up-to-dateness (stale test/compile tasks even
  after edits): iterate with `--rerun-tasks`; `-PstagedRes` takes
  the staged `res` dir, not the work dir (wrong dir NPEs in
  `getXml`). Java ctors learned via javap: only no-arg
  `new Paparazzi()` exists, `snapshot(View,String)`, DeviceConfig
  presets only. Startup probes `gradle --version`
  (`Enrichment:GradlePath/IconToolDir/PaparazziTimeout`). Tests use
  null (raster-only) or fake renderers; live test is env-gated
   (`SHIZU_ICON_TOOL` + `SHIZU_REAL_APKS`). Proven on AutoDND
   (white bar on `#252525`, matches known-good).
 - **PAPARAZZI SCALING FIX + XML-FIRST + BATCH (user bug reports):**
   XML icons rendered as a small top-left square on gray (Paparazzi
   snapshots the whole device screen; the 432px crop ate gray), and
   plain vectors blew up full-screen (wrap_content + FIT_CENTER).
   The test now draws onto its own bitmap (canvas pre-scaled by
   intrinsic dims, LayoutLib drawables ignore setBounds) and writes
   an exact-size PNG per icon; the C# crop stays only as a guard.
   A ghost failure (test printed success, rule rethrew after) traced
   to Paparazzi pre-parsing every res XML and choking on the
   adaptive root: adaptive roots stage beside `res/`
   (`DrawableStager.RootTargetDir`). Order is now XML-first at each
   level (manifest XML, manifest raster, badging XML, badging
   raster). One Gradle invocation per icon cost ~2.5min (task graph
   + test JVM + LayoutLib boot each), so refresh renders batched:
   two-phase (prepare per app with `b{id}` prefixes into one shared
   dir, single `renderIconBatch` over a manifest, commit per app;
   a total batch throw fails every pending, missing per-icon files
   fall back). Live re-render of the whole catalog (`--refresh-icons`
   one-shot, exists because 304/unchanged paths keep old icon files
   forever): 301 checked, 135 refreshed, 158 current, 8 failed on
   transient Npgsql host errors (rows untouched, retry next pass).
    flicky/datasimtile/rebootnya verified full-bleed via API.
  - **FIVE-ICON FOLLOW-UP (orion/kdeconnect/nomorebackground/
    wireless-adb-switch/buge):** transparent or bg-only renders traced
    to nested `<inset>` layers inside adaptive roots (LayoutLib ignores
    insets; the layer file had no drawable of its own) plus a missing
    background on kdeconnect. Fix: `DrawableStager.NormalizeAdaptiveRoot`
    unwraps single-child insets (max 4 hops) and inserts a white
    background when absent. Buge (white + ghost droid) was a resource
    pick, not a render bug: its foreground id has a default-config XML
    fallback plus density PNGs, and XML-always-first chose the dead
    fallback while real devices show the PNG. Nested drawable refs
    (`ApkResources.ResolveString`, used while staging) now resolve
    device-faithfully (versioned XML, then best-density raster, then
    fallback XML; SDK version decoded at a +20 offset,
    proven by probing real APK configs). The toplevel manifest lookup
    stays XML-first (`ResolveXml`, then raster) per the standing rule:
    XML-first applies to the manifest icon only. Live `--refresh-icons`
    re-run after the fix: 301 checked, 8 refreshed (all five reported
    apps plus titanpad/openminis/privacify, same inset bug class, fixed
    for free), 0 failed. All five verified full-bleed via the icon
    files and served correctly through the API (orion endpoint +
    icon hash match). Server restarted on :5110 with the fresh build.
   Batch died once on ALL pendings because `app/build.gradle` never
   forwarded `-Pbatch` as the `iconBatch` sysprop (test ran single
   mode against a missing drawable); one-line fix, smoke-tested,
    lesson recorded in server-setup. Full suite **232/232 green
    (201 Core + 31 Web), 0 warnings**.
  - **SEVEN-ICON FOLLOW-UP (autoslide/extendroid/framex/hyperbridge/
    okkei-patcher/system-ui-tuner/turboims):** per-APK badging+arsc
    verdict: framex/turboims/autoslide/extendroid ship density PNGs
    only (zero XML), so their rasters are provably correct;
    hyperbridge/tuner/okkei had XML roots but served rasters. Root
    cause: foreground vectors tinted with `@color` refs
    (hyperbridge used a framework white, tuner an app color chained
    to framework white/v31-dark); `BinaryXml` emits `(0x..)` for
    unresolvable refs and the stager aborted staging. Fix 1: generated
    `Core/Enrichment/FrameworkColors.cs` (391-entry literal-only
    `android.R.color` table from API 35 android.jar, frozen dict) as
    fallback in `ApkResources.SelectBest` (package 0x01, non-XML picks
    only), plus SDK tie-break on density ties (tuner picks the v31
    dark). Fix 2: unversioned anydpi XML lost to density rasters, so
    `ResolveString` order is now versioned XML, unversioned anydpi
    XML, raster, fallback XML (default-config XML still loses to
    rasters, preserving the endorsed buge PNG pick). All three
     re-staged and rendered pixel-perfect via `renderIconBatch`
     (viewed: leaves, wrench, cloud). Full suite **243/243 green
     (212 Core + 31 Web), 0 errors**; heal3 `--refresh-icons` run
     after: 301 checked, 264 refreshed (mostly file restorations),
     37 current, 1 failed on a transient unanalyzable APK (icon
     kept). The 3 fixed apps got new hashes; the 4 raster-correct
     apps restored byte-identical.
   - **MISSING-FILES HEAL (7 files on disk vs 330 hashes):** hundreds
     of icon files once lived in the store (ext4 dir size) but were
     gone; the deleter was never identified. Real gap found: refresh
     never rewrote a file whose hash was current, so missing files
     404d forever. Fix: refresh writes the file before the equality
     check (no-op when present) and reports a restore as `Enriched`,
     plus 6 APKs with no declared icon (fpsviewer badging has an
     empty icon attr, 5 APKs downloaded to prove it) now heal their
     letter-avatar files like enrich does, and LinkOnly avatar files
     heal only when the regenerated hash matches. Orphan deletes log
     a Warning with the hash. Suite **246/246 green (215 Core +
     31 Web), 0 errors**.
   - **ICON_ADAPTIVE FLAG (user call, for client framing):**
     `ProcessedIcon.Adaptive` (true only for `<adaptive-icon>`
     roots — a set staged `RootFile` is the signal; plain vectors
     render full-bleed but stay false) flows into the new
     `App.IconAdaptive` column (`icon_adaptive`, NOT NULL DEFAULT
     FALSE, migration applied to the live DB) and both app DTOs as
     `iconAdaptive`: true means full-bleed mask-safe (rounded-square
     framing), false means raster, plain vector, or avatar (squircle
     box). Refresh paths re-sync the flag without icon churn, so one
     pass backfills the whole catalog; 304/no-resolve paths stay
     stale-gradual. CORRECTION (user call): the first cut marked
     every Paparazzi render adaptive, wrongly flagging plain vectors
     like curbox; `NormalizeRender` now takes the staged-root kind
     and only adaptive roots set the flag (suite 249/249).
   - **SWAPPED SERVED ICONS (sbatterytweaks/wireless-adb-switch):**
     each row pointed at the other's render (pixel-proven: mean diff
     0.07/0.26 on the swap pairing vs 116 cross) — fossil of the
     pre-gate era when parallel renders shared one snapshot file
     (fixed since via the C# semaphore). Fixed live by writing the
     correct bytes under true content hashes + row updates (sbatt
     52f62b90 white bg battery, wadbs 5efcf9d0 blue-gray chevrons,
     both 200, flags true); stale files deleted after a zero-referrer
     check. Lesson: hand-typed hashes bite (one 400 from a wrong
     name) — always sha256sum. Other race-era swaps remain possible;
     any icon not matching the app's real icon is suspect.
   - **INSET PADDING HONORED (user report: on-device icon has more
     padding):** `NormalizeAdaptiveRoot` used to unwrap `<inset>`
     layers to full-bleed, throwing the author's padding away (wadbs
     fg inset 24dp rendered edge-to-edge). Stager now keeps `<inset>`
     wrappers (non-inset single-child lifts stay); the Java test
     parses per-side inset fractions (dp/px/sp/unadorned over the
     108dp viewport, `%` literal) and draws bg full-bleed + fg into
     the padded rect (`drawIntoRect`, canvas-transform so vectors
     and rasters behave alike). Live re-render of wadbs verified
     padded (chevrons sit inside the 24dp rect on the blue-gray bg).
     Suite **247/247 green (216 Core + 31 Web), 0 warnings**;
     full `--refresh-icons` pass running to roll the padding out
     (only padding-changed renders get new hashes, no churn).
   - **FORCE REFRESH (user call: refresh ALL icons):** a normal
     refresh skips byte-identical renders, so self-consistent wrong
     files (the sbatt/wadbs swap) never surface. `--refresh-icons
     --force` re-renders + rewrites + recounts everything (identical
     renders count as refreshed). Suite **248/248 green (217 Core +
     31 Web)**. FRESH RUN (user call): old store (with the user's
     bug/tmp/old subfolders) moved aside to `icons-backup-1835`,
     empty store, full `--refresh-icons --force`: 301 checked, 301
     refreshed, 0 failed. 44 non-DirectApk hashes had no file
     anywhere (7 LinkOnly + 37 Excluded, all letter-avatars):
     regenerated deterministically via `LetterAvatarGenerator`
     with hash verification — 331/331 hashes now have files.
     Server restarted on :5110; wadbs serves the padded render
      with `iconAdaptive: true`. Flags backfilled: 273 adaptive,
      72 raster/avatar.
   - **FOUR-DIRECTIVE UPDATE (user calls):** GitHub now takes the
     newest non-draft release with prereleases counting (renamed
     `GetLatestReleaseAsync`; many Shizuku apps ship only
     prereleases). GitLab parses APK markdown links embedded in the
     release description (AuroraStore style) and resolves relative
     `/uploads/...` paths through
     `https://gitlab.com/api/v4/projects/{urlencoded-path}{url}`
     (web-UI uploads routes 404 or redirect to sign-in; verified
     live); API asset links still prefer `direct_asset_url`.
     `pages/CLOSED_SOURCE.md` is intentionally ignored: sync passes
     an empty `closed-source` document so old rows sweep out as
     stale. Only the README `## Apps` section is ingested;
     Development libraries and Miscellaneous content stay out and
     their old rows sweep out too. Suite **251/251 green (220 Core
     + 31 Web), 0 warnings**. Live rollout verified (52 rows swept,
     catalog 333 Main-only).

   - **F-DROID SOURCE LOCK + FALLBACK (user call: F-Droid-only repos
     must source their APK there, and the source must stay locked):**
     when a forge has no APK at all (no release, or no `.apk` asset),
     enrichment falls back to the F-Droid main index, matching the
     `<application>` whose `<source>` URL equals the entry's forge
     repo (`FindPackageBySourceAsync`). The source that supplied a
     build is recorded and locked: `apk_source`
     (`GitHub|GitLab|Codeberg|FDroid|Izzy|Play|Other`) plus
     `apk_source_ref` (F-Droid/Izzy package id). A locked F-Droid
     app skips the forge entirely; a locked forge app never falls
     back (different signing keys would make clients reject the
     update). The F-Droid variant sidecar is skipped when the
     primary is already F-Droid-locked. Migration `AddApkSourceLock`
     backfills existing rows from `apk_url` + `source_kind`. Live:
     the 4 F-Droid-only apps (Always On Display,
     AlwaysOnDisplayToggle, Insular, Smart Dock) now serve real
     F-Droid APKs and adaptive icons with locks; a re-run pass made
     zero GitHub calls for them. Suite **257/257 green (226 Core +
     31 Web), 0 warnings**.

   - **GITLAB GENERIC-LABEL APK LINKS (user report: batt):** release
     asset links whose label is generic (`APK`) were skipped because
     `ApkAssetSelector` only matched link names. Links now match by
     name or URL, so narektor/batt resolves to its GitLab-hosted
     `Batt-1.3.apk` and is locked to `GitLab`. Suite **259/259 green
     (228 Core + 31 Web), 0 warnings**.

   - **ZIP-APK RELEASE FALLBACK (user report: AppControl-X):** some
     projects attach only a zip that carries the APK inside
     (risunCode/AppControl-X v3.0.0). When a forge release ships no
     `.apk`, a `release`-named (else largest) `.zip` asset is
     downloaded, the APK inside is picked (release-named, else
     largest) and analyzed like a direct build. `apk_url` stays the
     archive download; `apk_size`/`apk_sha256` describe the archive
     and the new `apk_archive_entry` names the APK inside (exposed in
     the app detail DTO; clients extract it). Best-effort: an
     unusable archive or one without an APK falls through to the
     F-Droid fallback. Live: `appcontrolx` now `DirectApk` serving
     the v3.0.0 zip with entry
     `AppControlX-release-generated-signed.apk`, version 3.1.0,
      adaptive icon; suite **277/277 green (246 Core + 31 Web),
      0 warnings**.

- **PLAY-LISTING ICON FALLBACK (user call: Play-only entries):** apps
  that end external-only (no APK from any source) with a linked Play
  listing get the real listing icon scraped from the page `og:image`
  (`PlayStoreClient`) instead of a generated letter-avatar; `apk_url`
  stays null and the client shows an open-in-Play-store /
  open-externally button based on the entry URL. The enrichment result
  turns `AvatarFallback` so passes stop warning about them. Live: 6 of
  the 7 Play leftovers got real listing icons (cellreader,
  customanimator, fivegswitcher, rootactivitylauncher,
  rootlessjamesdsp, wifilist); `android-show-taps-2` has no Play
  listing (`show.taps` returns 404) and no forge release, so it stays
  iconless. Suite **292/292 green (261 Core + 31 Web), 0 warnings**.

- Nothing committed (no git repo initialized in ShizuAppStoreServer).
- Markdig gotchas (commented in code): `StringLineGroup.ToString()` does NOT
  return text → item text is sliced from source via `ParagraphBlock.Span`;
  inline API is `Descendants()`, not `DescendantNodes()`; category nodes are
  created lazily (entry-less `###` yields no node); doc-wide unique slugs.
- Nothing committed (no git repo initialized in ShizuAppStoreServer).

## 6. Immediate next step (M7, separate repo)

AuroraDroid fork pointed at this API: keep its UI + `PackageInstaller`
flow; replace F-Droid index sync with `/v1/changes` sync;
`apk_url`→install, `store_url`→Play, `link_only`→Custom Tab.
Match the installed signing cert (SHA-256 + MD5 over the cert bytes)
against `sig_sha256`/`sig_md5`/`fdroid_variant` fingerprints and prefer
the variant URL when the F-Droid build is installed (M8 API is ready).
Backend M1–M6 + M8 are done and live-tested end-to-end on real
Postgres 18.6 (see §5 LIVE TEST: real boot, real GitHub/F-Droid).
The system DB on :5432 holds the real catalog
  (385 apps, Paparazzi-rendered icons) plus the live-test rows.
  Full suite now 292/292 (261 Core + 31 Web), 0 warnings.

## M8 decisions (signatures + F-Droid variant)

- **Forge-first is policy, not accident:** resolution order already
  preferred GitHub/GitLab; now explicit + the F-Droid side is retained
  as data (variant) instead of being discarded when a forge link wins.
- **Modern apksigner prints MD5** (`Signer #1 certificate MD5 digest`,
  verified on build-tools 35.0.0) — no algorithm gap with the index
  `<sig>`; the server can compare forge vs F-Droid signers directly.
  Older build-tools lack the MD5 line → parser treats it as optional.
- **Bytes on disk win:** file hash/version/sigs from the analyzed APK
  override index values (index skew happens; the bytes are what clients
  get). Package-name mismatch is the one hard failure (wrong file).
- **Signer extraction never fails enrichment:** missing Java/apksigner
  or unsigned APKs → null sigs, outcome unchanged (variant falls back
  to index-only with the `<sig>` MD5, which covers client matching).
- **Variant sidecar is strictly additive:** runs only after fresh
  forge enriches, broad-caught, clears stale rows when F-Droid drops
  the package; steady state is 304s + dict lookups, zero downloads.
- **F-Droid primaries keep zero-download steady state:** the APK is
  fetched only on version change (UpToDate short-circuit untouched).
- **Startup tool gate (post-M8 hardening):** `Core/Enrichment/
  ExternalToolProbe.cs` runs `aapt2 version` + `apksigner --version`
  after `builder.Build()`; any non-zero/missing binary → critical log
  + `InvalidOperationException` (fail fast, never serve a catalog that
  can't enrich). Skipped only in the `Testing` environment, which the
  `ShizuApiFactory` sets via `UseEnvironment` so Web.Tests stay
  hermetic. Verified both directions: bogus paths → boot refuses with
  both tools named; real boot logs both version lines and serves
  `/healthz`.

## M6 decisions (sync engine + deploy)

- **HEAD gate before work:** unchanged HEAD + no queued requests →
  due-only enrich or a `Skipped` pass that writes nothing (keeps
  `sync_runs` a clean audit log, not a heartbeat table).
- **Fetch is best-effort:** a dead remote must not stop the API from
  serving the last-known catalog — the pass continues off the local
  clone (5-min timeout, then proceed).
- **Requests drain only on success:** a failed pass leaves
  `sync_requests` unprocessed so the webhook trigger isn't lost.
- **ARCHIVED.md emptiness is meaningful:** no early return on an empty
  URL set — an emptied file must still *un*-mark resurrected apps
  (caught during implementation).
- **Due-window filtering stays client-side** (SQLite can't do
  `DateTimeOffset` arithmetic — M5 precedent; catalog is tiny).
- **F-Droid provider is a revalidating singleton,** not scope-cached:
  every call still conditional-GETs (cached or seed ETag), so freshness
  semantics are unchanged — the singleton only shares parsing, with
  per-repo gates against stampedes.
- **Deploy is framework-dependent single-file, untrimmed:** the server
  already needs the .NET 10 runtime (per `server-setup.md`), and
  trimming is unsafe for EF/Npgsql reflection. `deploy.sh` builds the
  `efbundle` fresh each time (`dotnet ef migrations bundle` — note the
  subcommand; bare `dotnet ef bundle` does not exist), so no bundle
  binary is committed.
- **Boundary lesson from tests:** a second pass at exactly +24h makes
  *everything* due — the no-change test passes at +1h to stay inside
  the re-check window.

## M5 decisions (API + test host)

- **Tombstones close the M2 follow-up:** `removed_apps` rows feed
  `/v1/changes removed[]`; upserter owns both directions (delete → write,
  re-add → clear).
- **Test-host config goes through DI, not `ConfigureAppConfiguration`:**
  with minimal hosting, `WebApplication.CreateBuilder` rebuilds
  `builder.Configuration` from appsettings/env/cmdline only — host-builder
  in-memory values layer *under* appsettings.json and never reach Program.cs
  (cost a debugging session: icon dir + cache flag silently ignored). Per-suite
  knobs are `RemoveAll`+`AddSingleton` swaps of
  `ApiOptions`/`EnrichmentOptions`/`AdminOptions` (commented in
  `ShizuApiFactory`); the rate-limiter policy resolves `ApiOptions` per
  request so the swap takes effect.
- **`OutputCacheMiddleware` has no request-driven bypass**
  (`Cache-Control: no-cache`/`no-store` are ignored — stale `/v1/meta`
  reads proved it). The test host overwrites all five cache policies with
  `NoOutputCachePolicy` (re-adding a policy name wins); production caching
  is untouched. Test clients are plain (`NewClient()`).
- **Fresh `:memory:` SQLite has no schema** until `ResetAsync`
  (`EnsureCreated`) — symptom is endpoint 500s, not seed errors
  (caught by `RateLimitTests`).
- **EF SQLite vs Npgsql:** covered in §5 (client-side DateTimeOffset
  ordering/comparison/MAX). Npgsql-targeted check was deferred, then
  done live 2026-09-12 (see §5 LIVE TEST: AdjustToUniversal parse +
  SaveChanges UTC normalization).

## M4 decisions (resolvers)

- **F-Droid needs no APK download:** `index.xml` already carries
  version/size/hash/minSdk; only the icon is mirrored. GitHub stays the
  winner when a forge source URL exists (F-Droid builds differ from dev
  APKs), so resolution order is GitHub → GitLab → F-Droid → fallback.
- **`ReadElementContentAsString` trap:** it parks the reader *on* the
  next sibling start, so the caller's `Read()` loop skips that sibling
  (took the *last* `<package>`, dropped hash/minSdk). Parser uses a
  `ReadLeafText` helper that stops on the element's own end tag instead.
- **Exclusion is strict sole-source:** Play primary + no usable source
  link (a second Play URL doesn't count). Anything else Play-primary is
  `play_redirect`, never excluded.

- **DB verification (decided for M2):** no local Postgres here
  (only `postgresql-libs`, docker daemon down, no passwordless sudo), so M2
  was validated without a live DB: upserter/history tests run on SQLite
  in-memory, migration SQL reviewed (`dotnet ef migrations script`), `efbundle`
  builds. Npgsql-targeted integration test was deferred to the user's server
  (`/opt/shizuappstore`, Postgres 17); done live 2026-09-12 instead
  (system Postgres 18.6, see §5 LIVE TEST). M5 follow-up: tombstones for
  `GET /v1/changes removed[]` (M2 hard-deletes stale rows).
