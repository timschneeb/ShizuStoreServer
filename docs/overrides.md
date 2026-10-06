# App overrides and screenshot blocks

Operator manual for the two SQL-edited tables that steer the catalog without
code changes: `app_overrides` (per-entry field overrides, visibility overrides
and app-shape source overrides) and `blocked_screenshot_urls` (global
screenshot blocklist). Both are edited directly in Postgres; there is no admin
endpoint.

```bash
sudo -u postgres psql -d shizuappstore
```

## How edits propagate

The database and the sync pass cooperate:

- Triggers bump the affected `apps.updated_at` the moment the row changes, so
  `GET /v1/changes?since=` carries the entry immediately, before the next pass
  materializes anything.
- The applier runs inside every sync pass, after enrichment and before the
  Shizuku permission gate. It re-asserts active overrides because enrichment
  rewrites most metadata columns on every pass; the write-back keeps
  `updated_at` still while the served value does not change.
- The applier also runs on a fast pass that finds nothing due, so pickup
  latency is the fast-loop cadence (`Sync:FastLoopMinutes`, default 15
  minutes) or the nightly full pass (03:00 UTC). No restart is needed.

Trigger details:

- `app_overrides`: INSERT and DELETE always touch the app; UPDATE only when
  `value`, `deleted_at` or `app_slug` changed. Editing `note`, `applied_value`
  or `baseline_value` alone never moves the delta clock.
- `blocked_screenshot_urls`: INSERT and DELETE touch every app whose stored
  `screenshots` or active `screenshots` override contains the URL; UPDATE only
  when `url` or `deleted_at` changed.

## app_overrides columns

| Column | Meaning |
| --- | --- |
| `id` | Surrogate key. |
| `app_slug` | `apps.slug` of the target entry, exact and case-sensitive. |
| `field` | Snake_case `apps` column from the allowlist below, or a `source_*` kind. One row per `(app_slug, field)`. |
| `value` | Textual value, parsed per field. |
| `applied_value` | Last value materialized on the app row; the steady-state marker. Null until the first apply. |
| `baseline_value` | Natural value captured before the first apply; used to restore on soft delete. Null when not captured yet. |
| `note` | Free-form operator note. Never parsed, and edits do not move the delta clock. |
| `created_at`, `updated_at` | Bookkeeping timestamps. |
| `deleted_at` | Soft delete; the applier restores the baseline and drops the row. |

Rows whose `app_slug` is not in the catalog stay inert and apply again if the
entry returns with the same slug. Re-adding a soft-deleted row requires
clearing `deleted_at` in the upsert (see below).

## Overridable fields

Names are lowercase and exact. Unknown fields are logged and skipped.

### List-owned

| Field | Format |
| --- | --- |
| `name` | Text, required (empty rejected). |
| `description` | Text, required (empty rejected). |
| `license` | Text, empty clears. |
| `is_recommended`, `has_paid`, `has_iap`, `has_ads`, `requires_root` | `true` or `false`. |
| `trial_days` | Non-negative integer, empty clears. |
| `source_url` | Text (URL), empty clears. |

### Enrichment-owned

| Field | Format |
| --- | --- |
| `display_name`, `apk_label`, `package_name` | Text, empty clears. |
| `permissions` | Newline-separated list, empty clears. |
| `author_name`, `author_url`, `author_key` | Text, empty clears. |
| `full_description`, `readme_url`, `changelog`, `changelog_url` | Text, empty clears. |
| `store_url`, `version_name` | Text, empty clears. |
| `version_updated_at` | ISO-8601 timestamp, empty clears. |
| `stars` | Non-negative integer, empty clears. |
| `download_total` | Non-negative integer (64-bit), empty clears. |
| `icon_hash` | 64 hex characters (case-insensitive, stored lowercase), empty clears. |
| `icon_adaptive` | `true` or `false`. |
| `screenshots` | Newline-separated URLs, empty clears. The applier writes the list verbatim; the pipeline's 12-item cap does not apply. |

The pipeline also consumes `source_url`, `package_name` and `store_url` as
input. Overriding one changes the served value immediately, and enrichment uses
the new value on its next pass.

### Visibility

| Field | Format |
| --- | --- |
| `availability` | `direct_apk`, `play_redirect`, `link_only` or `excluded` (case-insensitive, snake_case). |
| `excluded_reason` | Text applied only while the row is `excluded`; empty clears. |

### App-shape source kinds

These do not write an app column; they steer the source seam
(`IAppSourceOverrides`) and are consume-once.

| Field | Value | Effect |
| --- | --- | --- |
| `source_release_home` | `owner/repo` | Enrichment and release polling resolve releases from this repo instead of the listed repo (instafel, LinkSheet). |
| `source_gitcode_mirror` | `targetOwner/targetRepo\|readmeOwner/readmeRepo` | Releases come from the GitCode mirror, the README from the GitHub repo (hlbmerge). |
| `source_scan_all_releases` | `true` | All non-draft releases are scanned and their assets grouped (SmartspacerPlugins). |
| `source_prefer_prerelease` | `true` | The newest servable prerelease wins over stable (Universal-ReVanced-Manager). |

A new or edited kind forces exactly one recheck, steady state is a no-op, and
soft-deleting a consumed row forces one more recheck. No baseline is captured
and no app column or delta clock moves. The five known cases ship as a data
migration; add more like any other row.

### Not overridable

Identity and bookkeeping (`id`, `slug`, `url`, `listing`, `category_id`,
`parent_id`, `root_app_id`), publish and schedule state (`published_at`,
`added_at`, `updated_at`, `list_updated_at`, `last_checked_at`, `last_error`,
`enrich_etag`, `forge_assets_stale`), install counters and all `usage_*`
columns. Use the dedicated mechanisms instead: `app_unlist_overrides` to hide
an entry, `app_download_exclusions` to drop packages, `package_exceptions` and
`apps.exclude_override` for the Shizuku gate, `config_flags` for catalog
purges.

## Workflows

### Set or change a value

The upsert clears `deleted_at`, so it also revives a soft-deleted row:

```sql
INSERT INTO app_overrides (app_slug, field, value, note, created_at, updated_at)
VALUES ('mixplorer', 'display_name', 'MiXplorer', 'operator choice', now(), now())
ON CONFLICT (app_slug, field) DO UPDATE
SET value = EXCLUDED.value,
    note = EXCLUDED.note,
    deleted_at = NULL,
    updated_at = now();
```

The app row is touched immediately and the value materializes on the next pass
(the first apply also captures the baseline). Re-running the same insert is a
no-op for the served value: the applier re-asserts it without moving
`updated_at`.

Without `deleted_at = NULL` a re-armed row would be read as a pending restore
and the applier would drop it instead of applying the new value.

### Restore the natural value

```sql
UPDATE app_overrides
SET deleted_at = now()
WHERE app_slug = 'mixplorer' AND field = 'display_name';
```

The next pass writes the captured baseline back (for enrichment-owned fields it
also clears `last_checked_at`/`last_error` to force a recheck), clears the
baseline and deletes the row. A physical `DELETE` does not restore: the served
value stays until the pipeline naturally rewrites that column, so prefer the
soft delete. The same applies to source kinds (one more recheck when the row
had been consumed).

### Replace an icon manually

Icons are content-addressed files in the icon store (`/opt/shizuappstore/icons`
on the server) served as `/icons/{sha}.png`:

```bash
sha=$(sha256sum myicon.png | awk '{print $1}')
sudo cp myicon.png "/opt/shizuappstore/icons/$sha.png"
```

Then upsert `icon_hash` (`$sha`) and `icon_adaptive` (`true` for a
full-bleed/squircle icon, `false` for a rounded square). Copy the file before
inserting the row, or insert and copy immediately: the applier only reads the
hash, so clients get a 404 until the file exists, and the icon orphan cleaner
keeps any hash an active override references. An empty `icon_hash` clears the
icon; enrichment backfills a natural one on the next pass.

### Hide or restore an entry

`availability = 'excluded'` hides the entry like an operator unlist: reason
`Excluded by an override.`, a `removed[]` tombstone and a 404 on the detail
endpoint. Soft delete restores the previous availability, clears the reason
(only when it is the override reason), removes the tombstone and forces a
recheck. `excluded_reason` alone applies only while the row is excluded, for
example to keep a custom reason on an entry excluded by the Shizuku gate.
`app_unlist_overrides` and `ARCHIVED.md` outrank overrides: while such a reason
is set, visibility rows stay pending and apply once the unlist or archive goes
away.

### Override screenshots for one app

`screenshots` takes a newline-separated list of URLs; an empty value clears all
of them. The applier writes the list verbatim after each pass, and the global
blocklist still filters on top of it. Dollar quoting keeps the newlines
readable:

```sql
INSERT INTO app_overrides (app_slug, field, value, note, created_at, updated_at)
VALUES ('some-app', 'screenshots',
        $list$https://example.com/1.png
https://example.com/2.png$list$, 'operator picks', now(), now())
ON CONFLICT (app_slug, field) DO UPDATE
SET value = EXCLUDED.value,
    deleted_at = NULL,
    updated_at = now();
```

### Block a screenshot URL globally

```sql
INSERT INTO blocked_screenshot_urls (url, note, created_at, updated_at)
VALUES ('https://raw.githubusercontent.com/foo/bar/abcdef/screenshots/banner.png',
        'banner, not a screenshot', now(), now())
ON CONFLICT (url) DO UPDATE SET note = EXCLUDED.note, updated_at = now();
```

Unblock with `UPDATE blocked_screenshot_urls SET deleted_at = now() WHERE url = '...';`.
The URL returns on the next scan that finds it, and a repo fallback re-scan is
throttled to the weekly `RepoScreenshotsRecheckInterval`.

## blocked_screenshot_urls

| Column | Meaning |
| --- | --- |
| `id` | Surrogate key. |
| `url` | Exact URL string, unique. Matching is ordinal: scheme, host and path must match the stored screenshot string byte for byte. |
| `note` | Operator note. |
| `created_at`, `updated_at` | Bookkeeping timestamps. |
| `deleted_at` | Soft delete; the row stops hiding the URL. |

Effects:

- Intake: F-Droid/Izzy index and repo scans drop blocked URLs, so they never
  enter `screenshots`.
- Every pass purges stored hits from `screenshots` and moves `updated_at` (the
  served detail changed), so the column converges.
- Read paths filter immediately, so a block takes effect before the next pass:
  the API detail endpoint and the storefront never serve a blocked URL.
- INSERT and DELETE touch every carrier's `updated_at` for `/v1/changes`. A
  physical DELETE works like a soft delete but loses the note history.

## Verifying and troubleshooting

Inspect rows and their state:

```sql
SELECT app_slug, field, value, applied_value, baseline_value, deleted_at,
       CASE
         WHEN deleted_at IS NOT NULL THEN 'soft deleted, restore on next pass'
         WHEN applied_value IS NULL THEN 'pending first apply'
         WHEN applied_value <> value THEN 'pending value edit'
         ELSE 'steady'
       END AS status
FROM app_overrides
ORDER BY app_slug, field;
```

The applier logs `Materialized operator overrides on N apps.` when it writes,
and `App override {slug}/{field} ignored: {reason}` for rows it skips
(`unknown field`, `value is required`, `expected true or false`, `expected an
integer`, `must not be negative`, `expected an ISO-8601 timestamp`, `expected
64 hex characters`, `expected direct_apk, play_redirect, link_only or
excluded`, `expected owner/repo`, `expected
targetOwner/targetRepo|readmeOwner/readmeRepo`). Both appear in
`journalctl -u shizuappstore`.

To force pickup (and a re-enrichment) now, queue a pass:

```bash
TOKEN=$(sudo sed -n 's/^SHIZU_ADMIN_SECRET=//p' /etc/shizuappstore/env)
curl -fsS -X POST https://shizustore.timschneeberger.me/v1/admin/sync \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"reason":"materialize overrides"}'
```

Common problems:

| Symptom | Likely cause |
| --- | --- |
| Value not served although `value` is set | Row is inert: `app_slug` typo, or the entry left the catalog. |
| Row still pending after a pass | An unlist or `ARCHIVED.md` reason is set and outranks visibility rows. |
| Row gone without a soft delete | Physical `DELETE`, or (for source kinds) an invalid value that was logged and ignored. |
| Steady row but a stale served value | The applier re-asserts it on the next pass; the trigger already emitted the change. |
| Blocked URL still in `/v1/apps/{slug}` | The URL string differs (case, query, trailing slash); matching is exact. |
| `applied_value` older than `value` | Value edit not picked up yet; wait for the fast pass or trigger one. |

## Related

- `docs/SPEC.md` §3 (data model) and §7 (sync) describe the behavior.
- `docs/server-setup.md` covers the deploy flow and the admin triggers.
- `docs/listing-and-metadata.md` explains how the pipeline chooses sources and
  files.
