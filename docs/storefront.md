# Storefront (shizustore.com)

Browser-facing companion to the Android client: a .NET 10 Razor Pages app
that server-renders one linkable HTML page per catalog app. It exists so
other projects can deep-link to `https://shizustore.com/apps/{slug}`; on a
phone with the client installed the page opens the app detail screen, on
anything else it shows the app and links straight to the upstream APK.

The storefront lives in this repo (`src/ShizuAppStoreServer.Web/`) but is a
separate process and systemd unit. It does **not** use the public API: it
reads the same Postgres database directly, read-only, and reuses
`ShizuDbContext` and the entities from `ShizuAppStoreServer.Core`. No
migrations, no writes; the API stays the only writer.

## Why direct DB instead of the API

- Same repo, so entity/mapping changes are compile-checked in one build.
- No CORS, rate limit, output-cache or User-Agent attribution problems for
  browser traffic (browsers cannot call the API cross-origin at all).
- Icons are read from the same physical icon store the API serves, so the
  site works even if the API process is down.

Reusing Core pulls Core's transitive packages (Markdig, ImageSharp,
Microsoft.Extensions.AI) into the publish output even though the storefront
only uses the data layer; that is accepted for the compile-time safety.

## Pages and endpoints

| Route | Behavior |
|---|---|
| `GET /` | Landing page: hero icon, subtitle linking to the awesome-shizuku list, Download APK / GitHub / Browse apps buttons with icons, and a screenshot strip hotlinked from the ShizuStore catalog entry with the full-screen viewer. The app banner below the header is suppressed here. |
| `GET /apps` | Searchable app list: search box plus category, sort and price filter chips; the category filter opens a scrollable chip cloud, the other two open menus; a recommended toggle; sorted by most starred by default, 48 rows per page with numbered pagination. |
| `GET /apps/{slug}` | Detail page modeled on the client's Android detail screen: header with icon/name/author/version, tag chips (the category chip links to the filtered app list), stats strip, availability call to action, tonal notices (Play-only, billing, closed source), tracker card, about/usage/changelog/permissions dialogs with sanitized markdown, screenshots, link rows, collapsible sources (each candidate labelled with its ABI, or Universal for a fat build)/app information/signature, and "More from this developer" / "More from this category" carousels. Unknown or `excluded` slugs -> 404 rendered as the client's "App no longer available" placeholder. |
| `GET /icons/{sha}.png` | Physical icon file from `Icons:StorePath` (the API's `/opt/shizuappstore/icons`). 64-hex check -> 400, missing -> 404, `Cache-Control: public, max-age=86400, immutable`. |
| `GET /.well-known/assetlinks.json` | App Link verification for `me.timschneeberger.shizustore`, fingerprints from config. |
| `GET /healthz` | `{"status":"ok"}`, `no-store`. |

Detail rendering mirrors the API (`AppMapper` semantics) and the client UI:
`displayName` fallback, primary download first then `versionCode` desc,
category path root-to-leaf, space-joined signature sets, tracker tag grouping.
Tag chips follow the client's order (stars off `direct_apk`, category, min
SDK, relative update age, license, Dhizuku) plus list-screen extras
(recommended, trial, root, type). Availability drives the call to action
(`direct_apk` -> "Download APK", `play_redirect` -> "View on Play" with the
Play notice, `link_only` -> "View website"); only `direct_apk` entries get the
installs/downloads/stars/size stats strip. Compact counts, SI sizes and
relative ages use the client's own formatting rules. Carousels query the same
database for other entries by author key and category (excluding the current
slug, limited to 12).

Markdown (full description, usage report, changelog) is rendered server-side
with Markdig and sanitized with HtmlSanitizer; relative README images resolve
against the entry's source URL. Icons come from an inline SVG sprite
(`Pages/Shared/_IconSprite.cshtml`, path data ported from the client's vector
drawables); the `/apps` filter chips and their menus additionally use a
vendored Material Symbols Rounded subset (`wwwroot/lib/material-symbols`,
Apache-2.0) for the glyphs the sprite does not carry. mdui (Material Design 3
web components,
MIT) is vendored under `wwwroot/lib/mdui`, so no CDN and no inline script are
needed; the CSP allows inline styles because mdui sets style attributes. The
site header is an `mdui-top-app-bar` with `scroll-behavior="elevate"`, kept
sticky via `site.css` so it gains the material shadow once scrolled; its nav
holds the Apps link as a text `mdui-button` and a GitHub icon button to the
project repo. mdui
color tokens are overridden with the client's fallback brand palette
(`site.css`), which also carries `:not(:defined)` guards so unupgraded custom
elements do not flash unstyled before the deferred bundle runs. Collapsible sections use native `<details>`. HTML is
output-cached for 60 seconds, varying by Android vs other User-Agent so the
"Open in app" button is only rendered for Android browsers.

## Configuration

| Key | Production | Development (`appsettings.Development.json`) |
|---|---|---|
| `ConnectionStrings:Shizu` | `Host=localhost;Database=shizuappstore;Username=shizu` (password only in `appsettings.Production.json` on the host) | `Host=127.0.0.1;Port=55432;...` |
| `Icons:StorePath` | `/opt/shizuappstore/icons` | `/home/tim/.local/share/shizu-dev/icons` |
| `AssetLinks:PackageName` | `me.timschneeberger.shizustore` | same |
| `AssetLinks:Fingerprints` | release signing cert SHA-256 (list) | release fingerprint |

## Local development

The dev database is the API's development Postgres on `127.0.0.1:55432`
(see `docs/server-setup.md` for the API side):

```bash
dotnet run --project src/ShizuAppStoreServer.Web
# http://localhost:5139
```

Tests (hermetic, SQLite-swapped, no live database):

```bash
dotnet test tests/ShizuAppStoreServer.Web.Tests/ShizuAppStoreServer.Web.Tests.csproj --nologo -v q
```

## Deploy

One-time setup on the host:

```bash
sudo mkdir -p /opt/shizustore-web/app
sudo chown -R shizu:shizu /opt/shizustore-web
# then create /opt/shizustore-web/app/appsettings.Production.json (mode 640)
sudo cp deploy/shizustore-web.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now shizustore-web.service
```

`appsettings.Production.json` carries the database password and is never
overwritten by the deploy script. Recurring deploys from the repo root:

```bash
./deploy/deploy-web.sh <ssh-host>
```

The script publishes the single-file `linux-x64` binary, rsyncs it to
`/opt/shizustore-web/app` (excluding `appsettings.Production.json`) and
restarts `shizustore-web.service`. It never touches the API unit, the
database schema or the icon store.

Cloudflare (dashboard, not on disk, because `cloudflared` is
token-managed): add a tunnel public hostname `shizustore.com` pointing at
`http://localhost:5139`. No Zero Trust Access policy: the storefront is
public, like the API. Kestrel binds loopback only via
`ASPNETCORE_URLS=http://127.0.0.1:5139`.

## App Links and fingerprints

The client declares two deep link forms: `https://shizustore.com/apps/{slug}`
(Android App Link, `autoVerify`) and `shizustore://apps/{slug}` (custom
scheme, works without verification). `/.well-known/assetlinks.json` publishes
the Android app and its signing certificate fingerprints; the fingerprints
come from `AssetLinks:Fingerprints` and must match the installed build's
signing key.

The current release fingerprint (from the GitHub release APK
`ShizuStore-1.3.1.apk`, cert `CN=ShizuStore,
EMAILADDRESS=shizustore@timschneeberger.me, C=DE`):

```
E7:62:FF:C1:5D:2F:8D:E8:AB:19:1D:F5:3E:A5:A5:25:F9:0D:3F:F3:79:56:FC:D4:E3:04:E0:34:0B:AF:BA:BA
```

Extract it for a new release with:

```bash
apksigner verify --print-certs ShizuStore-<version>.apk
# SHA-256 digest -> uppercase, colon-separated
```

Debug builds are signed with the AOSP testkey and are intentionally not
listed; test deep links on debug builds with the custom scheme, and verify
App Links after installing the release build
(`adb shell pm verify-app-links --re-verify me.timschneeberger.shizustore`).
