# ShizuAppStoreServer — Implementation Plan

Backend for the Shizu app store (directory + direct APK links, icons hosted).
List source: `awesome-shizuku` (`README.md` + `pages/CLOSED_SOURCE.md`, EN only).

## 0. Locked decisions

| Decision | Value |
|---|---|
| Location | `/home/tim/Development/ShizuAppStoreServer` |
| Stack | C# / .NET 10 (`net10.0`), controllers-style API, `.slnx` solution |
| Shape | **Single binary, single systemd service** (web API + hosted background services in one project) |
| Projects | `src/ShizuAppStoreServer` (web+worker), `src/ShizuAppStoreServer.Core`, `tests/ShizuAppStoreServer.Core.Tests` |
| DB | Postgres 17 via EF Core + Npgsql |
| List input | `README.md` + `pages/CLOSED_SOURCE.md`; `ARCHIVED.md` = exclusion |
| History source | `git log` of the `awesome-shizuku` clone (no custom diffing) |
| APKs | Never hosted; direct GitHub/GitLab/F-Droid URLs or Play redirect |
| Hosted files | App icons only (`/icons/{sha256}.png`) |
| Scope | All entries except Play-sole-source ones |
| Monetization | Donations/supporter links only — no paywalls, no license keys (paid push feature scrapped) |
| Deploy | Bare metal + systemd, no Docker |

## 1. Deploy layout (`/opt/shizuappstore/`)

```
/opt/shizuappstore/app/      # single published binary + appsettings.Production.json
/opt/shizuappstore/icons/    # icon store (worker writes, api serves)
/opt/shizuappstore/list/     # awesome-shizuku git clone (Worker pulls from here)
```

One unit: `shizuappstore.service` (Kestrel on `127.0.0.1:5100`, `Restart=always`,
dedicated `shizu` user). Deploy flow: `dotnet publish -c Release` → rsync →
run EF migration bundle → `systemctl restart`. Ship `deploy/shizuappstore.service`
+ `deploy/deploy.sh` + `docs/server-setup.md` (one-time: .NET 10 runtime,
Postgres, `aapt2` via Android cmdline-tools `sdkmanager "build-tools;34.0.0"`,
list clone) in this repo.

## 2. NuGet packages

- **Core:** `Markdig` ✅ (installed, v1.3.2), `Npgsql.EntityFrameworkCore.PostgreSQL`,
  `SixLabors.ImageSharp` ✅ (v2.1.11 — last Apache-2.0 line, patched for
  CVE-2025-54575; v3+ is Split-Licensed and v4 needs a build key, unnecessary
  for PNG resize + avatars). Git history via `git` CLI subprocess (no LibGit2Sharp).
  GitHub Releases via raw `HttpClient` + `System.Text.Json`, **not** Octokit:
  the §8 stubbed-`HttpClient` tests need direct handler injection, and the
  API surface used (list releases, conditional GET) is trivial.
- **Web:** `Microsoft.AspNetCore.OpenApi` (built-in OpenAPI; Scalar UI in dev),
  rate-limiting via built-in middleware.
- **Jobs:** `BackgroundService` + `PeriodicTimer` inside the web project — no
  Quartz/Hangfire, no message bus (`sync_requests` table as trigger queue).

## 3. Postgres schema (EF Core, `Core/Data/`)

- **`categories`** — `id (bigint identity)`, `name`, `slug` unique, `parent_id`
  nullable (Vendor-specific → Pixel/OneUI/MIUI/Other), `section` (`apps|libraries|misc`)
- **`apps`** — `id`, `slug` unique, `name`, `description`, `license` (normalized,
  `Propietary`→`Proprietary`), `listing` (`main|closed-source`), `type`
  (`app|library|flow`), flags (`is_recommended`, `has_paid`, `has_iap`, `has_ads`,
  `trial_days`, `requires_root`), `parent_id` nullable (nested entries),
  `url`, `source_url` nullable, `source_kind`
  (`github|gitlab|codeberg|fdroid|izzy|play|other`), `availability`
  (`direct_apk|play_redirect|link_only|excluded`), `excluded_reason`,
  `package_name` nullable, `version_code`, `version_name`, `apk_url`, `apk_size`,
  `apk_sha256`, `min_sdk`, `store_url`, `icon_hash`, `category_id`,
  `added_at`, `updated_at` (from git history), `last_checked_at`, `last_error`
- **`app_versions`** — append-only `(app_id, version_code, version_name, apk_url,
  detected_at)` → feeds update checks
- **`sync_runs`** — `(started_at, finished_at, trigger, head_commit, added,
  updated, removed, failed, error)`
- **`sync_requests`** — webhook/manual trigger queue `(requested_at, reason, processed)`

## 4. Parser (`Core/Parsing/`, ✅ DONE — M1)

Markdig AST for structure (`##` section, `###` category, `####` subcategory,
nested bullets → children); per-item fields from raw source text sliced via
`ParagraphBlock.Span`, using the grammar from `scripts/lint.py`:
`* [Name](url) [tags] - Description \`License\` [(Source code)](url)`.
Tags: `✨`→recommended; `` `Paid`/`IAP`/`Ads`/`Root` ``→flags;
`` `N-day trial` ``→`trial_days`; `💰` decorative. Malformed lines →
`ParseWarning` (never crash). Category nodes created lazily (empty `###`
yields no node). Slugs unique per document.
Gotchas (see code comments): `StringLineGroup.ToString()` does NOT return text.
Tests: 8/8 green incl. full real-README parse
(`tests/ShizuAppStoreServer.Core.Tests/AwesomeListParserTests.cs`).

## 5. Resolver + enricher (`Core/Sources/`, `Core/Enrichment/`)

- Classify `source_kind` from URL; **exclude** iff Play is the *sole* source URL
  (`availability=excluded` + reason; per-app override flag).
- **GitHub** (majority): Releases API with PAT from env → latest stable → pick
  `.apk` asset (name contains `release`, else largest) → temp download →
  `aapt2 dump badging` → package/version/icon → `apksigner verify
  --print-certs` → signing-cert SHA-256 + MD5 → icon chain: badging
  rasters (PNG/WebP in-house) → manifest `android:icon` via arsc
  (XML staged for Paparazzi, rasters in-house) → badging rasters →
  badging XML → Paparazzi/LayoutLib render (`tools/icon-render`,
  `DrawableStager` binary-AXML-to-text + per-icon Gradle render,
  top-left crop) → ImageSharp normalize to 192px PNG →
  `/icons/{sha256}.png` → **delete APK**.
  Record size, sha256, minSdk, sig fingerprints.
- **F-Droid/Izzy:** repo `index.xml` (singleton revalidating provider,
  conditional GET) → match package → `apk_url` = repo base + apk name.
  Index-only when the file can't be fetched; on version change the APK is
  downloaded and fully analyzed like a forge build (bytes on disk win over
  index values). Index `<sig>` (cert MD5) is always recorded.
- **GitLab:** Releases API → `.apk` asset link → same enrich path.
- **Forge-first, always:** a GitHub/GitLab link anywhere in the entry wins
  the primary APK; F-Droid is never primary when a forge source exists.
  Forge-primary apps additionally resolve the same package in the F-Droid
  main index and record it as the alternate variant (`fdroid_*` columns:
  URL, version, size, sha256, sig fingerprints) — downloaded + analyzed on
  change, index-only otherwise, cleared when dropped from the index.
- **Play + GitHub combos:** GitHub path wins; Play link kept as `store_url`.
  **No Play scraping whatsoever.**
- **Others** (Codeberg, llamalab, blob links): `link_only` + generated
  letter-avatar. Unrenderable XML (no renderer, stage abort, Gradle
  failure) → same fallback.
- Politeness: PAT + conditional requests, `SemaphoreSlim` max ~4 parallel
  enrichments; per-app `last_error` + backoff.

## 6. API contract (`/v1/`, controllers)

- `GET /v1/apps?category=&q=&license=&listing=&availability=&type=&recommended=&page=&pageSize=&sort=`
  → `{ items[], total, page, pageSize }`
- `GET /v1/apps/{slug}` — full detail (apk/store/source URLs, version, category path, flags,
  signing-cert fingerprints `sig_sha256`/`sig_md5`, F-Droid alternate variant object or null)
- `GET /v1/categories` — tree with per-node counts
- `GET /v1/changes?since=` (ISO-8601) → `{ added[], updated[], removed[] }` — free delta sync
- `GET /v1/meta` → `{ generatedAt, listCommit, counts }`
- `GET /icons/{sha256}.png` — static, immutable long-cache; `GET /healthz`;
  OpenAPI JSON + Scalar UI. Public, no auth; fixed-window rate limit
  (~100 req/min/IP); response caching + ETags.

## 7. Background jobs (hosted services in the single binary)

- **Fast loop (every 15 min):** `git fetch` list repo → HEAD changed? → parse →
  upsert (rename detection: same URL + new name = update, keep id) → queue
  enrichments → `sync_runs` row. Drains `sync_requests` first.
- **Nightly:** full re-enrich pass (release checks with ETags) → version bumps
  append to `app_versions`.
- **Webhook:** `POST /v1/admin/sync` (HMAC-SHA256, secret from env) → inserts
  into `sync_requests`.

## 8. Testing

- xUnit golden parser tests (✅ M1); resolver tests with stubbed `HttpClient`
  (no network); migrations validated in CI (`dotnet ef migrations bundle` dry-run).
- Cross-check oracle: parse a pinned list commit, compare item count + names
  against `trackawesomelist-source` output (sanity, not byte-equality).
- Do NOT copy files from `trackawesomelist-source` (AGPL-3.0); reimplementing
  its grammar from observation + own `lint.py` is clean.

## 9. Milestones

- [x] **M1** — scaffolding + parser + golden tests
- [x] **M2** — EF schema/migrations + git-history backfill (`added_at`/`updated_at`)
  (done: `Core/Data/` entities + `InitialCreate` migration, `Core/History/`
  `git log -p` backfill keyed by URL, `Core/Sync/CatalogUpserter` with
  rename/move handling; `apps.url` is non-unique — real data lists the same
  URL in several categories)
- [x] **M3** — GitHub resolver + enricher + icon pipeline (+ `aapt2` server setup doc)
  (done: `Core/Sources/` classifier + releases client + APK picker,
  `Core/Enrichment/` aapt2 runner/parser + icon normalize + letter-avatars +
  per-app enricher + bulk fan-out, `apps.enrich_etag` migration, DI wiring,
  `docs/server-setup.md`; parser verified against real `aapt2 dump badging`
  output from build-tools 35.0.0)
- [x] **M4** — F-Droid/Izzy/GitLab resolvers + exclusion rules
  (done: `Core/Sources/` GitLab releases client + `index.xml` parser/client/
  scope-cached provider, generic APK picker overload, `IconProcessor`
  raw-image path; `apps.exclude_override` migration; enricher resolves
  GitHub → GitLab → F-Droid/Izzy → fallback, Play-sole-source →
  `excluded` unless overridden, Play+forge keeps `store_url`; F-Droid
  needs no APK download — version/size/hash/minSdk + mirrored icon come
  from the index)
- [x] **M5** — API controllers + OpenAPI + caching + rate limits + webhook endpoint
  (done: `removed_apps` tombstones close the M2 follow-up — upserter writes on
  stale delete, clears on re-add; `/v1/{apps,apps/{slug},categories,changes,meta}`,
  `/healthz`, `/icons/{sha}.png` (immutable), `POST /v1/admin/sync` (HMAC-SHA256,
  fail-closed 503); fixed-window rate limit 100/min/IP, output-cache policies
  per endpoint with cache-before-limiter; OpenAPI always, Scalar dev-only;
  `tests/ShizuAppStoreServer.Web.Tests` — 21 hermetic endpoint tests on a
  SQLite-swapped host, full suite 163/163 green, 0 warnings)
- [x] **M6** — scheduling services + systemd unit + deploy script + docs
  (done: `Core/Sync/SyncService` — drains `sync_requests`, best-effort
  `git fetch`, HEAD-gated pass (parse → history → upsert → archived →
  due-only enrich), `SyncRun` rows incl. error rows; `Web/Sync/`
  `EnrichmentRunner` + `SyncGate` + `SyncPassRunner` + fast-loop (15 min,
  `RunOnStartup`) + nightly full re-check (03:00 UTC);
  `FdroidIndexProvider` is a thread-safe revalidating singleton;
  `deploy/` unit + `deploy.sh` (publish → `ef migrations bundle` →
  rsync → migrate → restart) + `linux-x64` single-file profile,
  publish + bundle verified; `docs/server-setup.md` deploy flow;
  full suite 178/178 green, 0 warnings)
- [x] **M8** — APK signatures + F-Droid alternate variant (done before M7 —
  the client needs this API to match locally installed apps)
  (done: `Core/CertFingerprint` normalize/join helpers, `Core/Enrichment/`
  `apksigner` runner + output parser (SHA-256/SHA-1/MD5 per signer,
  rotation-aware), F-Droid parser fix — real `index.xml` carries
  version/versioncode/sig as *elements* (the M4 attribute reads always
  yielded 0/null; heals on next enrich) — `apps` gains `sig_sha256`,
  `sig_md5` + 7 `fdroid_*` variant columns (`AddApkSignatures` migration);
  enricher is forge-first with F-Droid-variant sidecar (download + analyze
  on change, index-only fallback, cleared when dropped), F-Droid primaries
  download on change (APK icon preferred, package-mismatch fails the pass);
  detail/summary DTOs expose sigs + variant; full suite 193/193 green
  (166 Core + 27 Web), 0 warnings; apksigner output locked by verbatim
  test + live run against a real signed APK)
- [ ] **M7** (separate repo) — AuroraDroid fork pointed at this API (keep its UI +
      `PackageInstaller` flow; replace F-Droid index sync with `/v1/changes` sync;
      `apk_url`→install, `store_url`→Play, `link_only`→Custom Tab;
      match the installed signing cert against
      `sig_sha256`/`sig_md5`/`fdroid` fingerprints and prefer the F-Droid
      variant URL when the F-Droid build is installed)

## 10. Risks

- `aapt2` acquisition is the fiddliest server step (sdkmanager licensing) — isolated in M3.
- Initial release backfill over ~350 repos is slow — one-time cost; ETags make later runs cheap.
- Client push: FCM works sideloaded where Play Services exists; add polling/UnifiedPush
  fallback for de-Googled users.
