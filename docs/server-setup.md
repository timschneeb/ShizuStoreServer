# Server setup (`/opt/shizuappstore/`)

One-time provisioning for the bare-metal host (Postgres 17). The M6
`deploy/deploy.sh` + `shizuappstore.service` automate the recurring part;
this doc covers what must exist first.

```
/opt/shizuappstore/app/      # published single binary + appsettings.Production.json
/opt/shizuappstore/icons/    # icon store (worker writes, api serves /icons/{sha256}.png)
/opt/shizuappstore/list/     # awesome-shizuku git clone (worker pulls from here)
```

Service account: dedicated `shizu` user, owns all three directories.
Kestrel listens on `127.0.0.1:5100` behind the existing reverse proxy.

## .NET 10 runtime

```bash
# Microsoft package feed, then:
sudo apt install dotnet-runtime-10.0
```

No SDK needed on the server (publish happens on the dev machine;
migrations run via a fresh `efbundle` built by `deploy/deploy.sh`).

## Postgres 17

```bash
sudo apt install postgresql-17
sudo -u postgres psql -c "CREATE USER shizu WITH PASSWORD '…';"
sudo -u postgres psql -c "CREATE DATABASE shizuappstore OWNER shizu;"
```

`appsettings.Production.json` (in `app/`, never committed):

```json
{
  "ConnectionStrings": {
    "Shizu": "Host=localhost;Database=shizuappstore;Username=shizu;Password=…"
  },
  "Enrichment": {
    "Aapt2Path": "/opt/android-sdk/build-tools/34.0.0/aapt2",
    "IconStorePath": "/opt/shizuappstore/icons",
    "MaxParallelism": 4
  }
}
```

GitHub PAT (higher Releases-API rate limits for the ~350-repo backfill):

```bash
# in the systemd unit (M6) or /etc/shizuappstore/env:
SHIZU_GITHUB_TOKEN=github_pat_…
```

Anonymous works but is limited to 60 requests/hour — the initial backfill
needs the PAT; nightly re-checks (ETag-conditioned) are cheap either way.

Admin webhook secret (`POST /v1/admin/sync` authenticates with
`X-Shizu-Signature: hex(HMAC-SHA256(raw_body, secret))`; without a secret
the endpoint fail-closes with 503):

```bash
# in the systemd unit (M6) or /etc/shizuappstore/env:
SHIZU_ADMIN_SECRET=$(openssl rand -hex 32)
```

(The `Admin:HmacSecret` config key works too, but env is preferred — never
commit the secret to appsettings.json.)

## aapt2 (icon extraction)

The enricher shells out to `aapt2 dump badging` to read package/version/
icon paths from downloaded APKs (APKs are deleted right after; only the
192px PNG icon is kept). Install via the Android command-line tools:

```bash
sudo mkdir -p /opt/android-sdk/cmdline-tools
# download commandlinetools-linux-*.zip from
# https://developer.android.com/studio#command-line-tools-only
sudo unzip commandlinetools-linux-*.zip -d /opt/android-sdk/cmdline-tools
sudo mv /opt/android-sdk/cmdline-tools/cmdline-tools /opt/android-sdk/cmdline-tools/latest
export ANDROID_HOME=/opt/android-sdk
yes | sudo -u shizu /opt/android-sdk/cmdline-tools/latest/bin/sdkmanager --licenses
sudo -u shizu /opt/android-sdk/cmdline-tools/latest/bin/sdkmanager "build-tools;34.0.0"
```

Then point `Enrichment:Aapt2Path` at
`/opt/android-sdk/build-tools/34.0.0/aapt2` (verified locally with
build-tools 35.0.0 — the `dump badging` format is stable across 30–36).

Notes:

- Only the `build-tools` package is needed (~50 MB); no platform, no
  emulator, no full SDK.
- `dump badging` is read-only and needs **no** `android.jar` (that is only
  required to *build* APKs, which the server never does).
- The badging line that carries the min SDK is `minSdkVersion:'NN'` on
  modern build-tools (`sdkVersion:'NN'` on very old ones); the parser
  accepts both, and icon lines are `application-icon-{density}:'path'`.
- Sanity check on the server:
  `aapt2 version` → `Android Asset Packaging Tool (aapt) 2.x-…`.

## apksigner (signing-cert fingerprints)

F-Droid builds are delayed and — unless reproducible — signed with a
different key than the developer's forge releases. The server is
therefore **forge-first** (a GitHub/GitLab link anywhere in the entry
wins the primary APK) and records **both** signers, so clients can
detect which build is installed locally and offer the matching download:

- `sig_sha256` / `sig_md5` — primary APK fingerprints, read by
  `apksigner verify --print-certs` (SHA-256 + MD5 lines; rotation-aware,
  space-joined sets).
- `fdroid_variant { apk_url, version, sha256, sig_sha256, sig_md5 }` —
  the same package's F-Droid build, resolved by package name in the
  F-Droid main index and analyzed on change (nullable when the package
  is not on F-Droid).

Client matching: hash the installed app's signing cert (SHA-256 and
MD5) and compare against all four fingerprints; when an
`fdroid_variant` fingerprint matches, offer `fdroid_variant.apk_url`
instead of the primary `apk_url`.

`apksigner` ships with the same build-tools package as `aapt2` but
needs a JRE:

```bash
sudo apt install default-jre-headless
```

Then point `Enrichment:ApksignerPath` at
`/opt/android-sdk/build-tools/34.0.0/apksigner` (verified locally with
35.0.0, which prints SHA-256 + SHA-1 + MD5 digests per signer).
Signature extraction is best-effort: without Java/apksigner the
`sig_*` columns stay null and enrichment still succeeds (the F-Droid
`<sig>` MD5 from the index is always recorded).

## Icon rendering (Gradle + Paparazzi)

XML launcher icons (adaptive icons, vectors) render through Google
LayoutLib via Paparazzi (`tools/icon-render`), so future drawable
features keep working without parser changes. Needs a JRE, Gradle,
and one SDK platform (the tool targets `compileSdk 35`):

```bash
sudo apt install default-jre-headless
# Gradle 8.13+: https://gradle.org/install/ (verified locally with 8.14)
sudo -u shizu /opt/android-sdk/cmdline-tools/latest/bin/sdkmanager "platforms;android-35"
# one-time per checkout: point the tool at the SDK
printf 'sdk.dir=/opt/android-sdk\n' | sudo tee tools/icon-render/local.properties
```

Knobs (`Enrichment:` section): `GradlePath` (default `gradle`),
`IconToolDir` (default `tools/icon-render`), `PaparazziTimeout`
(default 15min per icon; the Gradle daemon stays warm between
renders). Smoke test from the repo root:

```bash
gradle -p tools/icon-render renderIcon -PstagedRes=<res-dir> \
  -PiconName=<drawable> -PiconPx=432 -Pout=/tmp/icon.png --rerun-tasks
```

(`-PstagedRes` is the staged `res` dir the enricher prepares per
icon; `--rerun-tasks` defeats stale up-to-date checks while
iterating. Production renders run batched: one `renderIconBatch
-PstagedRes=<shared-res> -Pbatch=<manifest>` per pass, because each
Gradle invocation pays a full task graph + test-JVM + LayoutLib
boot (~2.5min per icon unbatched). If the batch task fails for
every icon at once, check that `app/build.gradle` forwards the
`-Pbatch` value as the `iconBatch` test sysprop — a missing forward
silently runs single-icon mode against a nonexistent drawable.)

## Startup tool check

All three binaries are verified at startup (`aapt2 version`,
`apksigner --version`, `gradle --version` must exit 0) — the server logs the detected
versions and **refuses to boot** without them instead of serving a
catalog that never enriches. Only the `Testing` environment used by
the integration suite skips the check.

## List clone

```bash
sudo -u shizu git clone https://github.com/timschneeb/awesome-shizuku /opt/shizuappstore/list
```

The fast loop (M6) `git fetch`es before each pass and records the HEAD in
`sync_runs.head_commit`.

## Deploy flow (recurring, M6)

One-time unit install (after provisioning above):

```bash
sudo cp deploy/shizuappstore.service /etc/systemd/system/
sudo mkdir -p /etc/shizuappstore
# /etc/shizuappstore/env holds the secrets (root-owned, not world-readable):
sudo sh -c 'printf "ConnectionStrings__Shizu=Host=localhost;Database=shizuappstore;Username=shizu;Password=…\nSHIZU_GITHUB_TOKEN=github_pat_…\nSHIZU_ADMIN_SECRET=$(openssl rand -hex 32)\n" > /etc/shizuappstore/env'
sudo chmod 600 /etc/shizuappstore/env
sudo systemctl daemon-reload
sudo systemctl enable --now shizuappstore.service
```

Each deploy (from the repo root on the dev machine):

```bash
./deploy/deploy.sh <ssh-host>
```

The script publishes the single-file binary (`linux-x64` profile),
builds a fresh `efbundle`, rsyncs `app/` (never touching
`appsettings.Production.json` or `/etc/shizuappstore/env`), runs the
bundle as `shizu`, and restarts the service.

First boot is the initial backfill: `Sync:RunOnStartup` defaults to
`true`, so the worker parses the list clone, upserts the catalog, and
enriches all ~350 apps (slow one-time pass over the Releases APIs —
this is what the PAT is for). Watch it via
`journalctl -u shizuappstore -f` and `sync_runs` rows; later passes are
cheap (HEAD-gated fast loop every 15 min + nightly full re-check at
03:00 UTC, both tunable under `Sync:`).
