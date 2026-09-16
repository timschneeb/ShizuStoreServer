# Server setup (`/opt/shizuappstore/`)

One-time provisioning for the bare-metal host (Arch Linux, PostgreSQL).
`deploy/deploy.sh` + `shizuappstore.service` automate the recurring part;
this doc covers what must exist first.

```
/opt/shizuappstore/app/          # published single binary + appsettings.Production.json
/opt/shizuappstore/icons/        # icon store (worker writes, api serves /icons/{sha256}.png)
/opt/shizuappstore/list/         # awesome-shizuku git clone (worker fast-forwards, re-clones on failure)
/opt/shizuappstore/icon-render/  # Paparazzi tool (deploy.sh syncs it, Gradle writes build/ here)
/opt/shizuappstore/gradle-home/  # GRADLE_USER_HOME (Gradle daemon registry + caches)
```

Service account: dedicated `shizu` user, owns all five directories.
Kestrel listens on `127.0.0.1:5137`; a Cloudflare Tunnel (token-managed,
see the deploy flow) exposes it as `https://shizustore.timschneeberger.me`.
No local reverse proxy or TLS terminator is involved.

## .NET 10 runtime

```bash
sudo pacman -S dotnet-runtime-10.0 aspnet-runtime-10.0
```

The server is ASP.NET Core, so the ASP.NET runtime is needed alongside
the base runtime. No SDK needed: publishing happens on the dev machine,
and migrations run from a fresh `efbundle` built by `deploy/deploy.sh`.

## PostgreSQL

```bash
sudo pacman -S postgresql
sudo -u postgres initdb -D /var/lib/postgres/data   # first install only
sudo systemctl enable --now postgresql
sudo -u postgres psql -c "CREATE ROLE shizu LOGIN;"           # add a password if pg_hba requires one
sudo -u postgres psql -c "CREATE DATABASE shizuappstore OWNER shizu;"
```

If `CREATE DATABASE` fails with `template database "template1" has a
collation version mismatch` (glibc was upgraded after initdb), refresh the
template and postgres databases first:

```bash
sudo -u postgres psql -c "ALTER DATABASE template1 REFRESH COLLATION VERSION" \
  -c "ALTER DATABASE postgres REFRESH COLLATION VERSION"
```

The default Arch `pg_hba.conf` (`local all all trust`, `host all all
127.0.0.1/32 trust`) authenticates the `shizu` role without a password, so
the connection string below carries none. Add `Password=…` only when the
host uses a stricter `pg_hba`.

`appsettings.Production.json` (in `app/`, never committed; the deploy
rsync excludes it by name):

```json
{
  "ConnectionStrings": {
    "Shizu": "Host=localhost;Database=shizuappstore;Username=shizu"
  },
  "Enrichment": {
    "Aapt2Path": "/opt/android-sdk/build-tools/34.0.0/aapt2",
    "ApksignerPath": "/opt/android-sdk/build-tools/34.0.0/apksigner",
    "GradlePath": "/opt/gradle-8.14/bin/gradle",
    "IconToolDir": "/opt/shizuappstore/icon-render",
    "IconStorePath": "/opt/shizuappstore/icons",
    "RunLogPath": "/var/log/shizu/enrichment-runs.log",
    "MaxParallelism": 2
  },
  "Sync": {
    "ListPath": "/opt/shizuappstore/list",
    "PollParallelism": 4
  }
}
```

`MaxParallelism` 2 and `PollParallelism` 4 fit a 2 vCPU host; raise them on
beefier hardware. `RunLogPath` needs a writable directory: the unit's
`LogsDirectory=shizu` creates `/var/log/shizu` and grants access under
`ProtectSystem=strict`.

GitHub PAT (higher Releases-API rate limits for the ~350-repo backfill):

```bash
# systemd unit or /etc/shizuappstore/env:
SHIZU_GITHUB_TOKEN=github_pat_…
```

Anonymous works but is limited to 60 requests/hour; the initial backfill
needs the PAT, later ETag-conditioned re-checks are cheap either way.

Admin webhook secret (`POST /v1/admin/sync` authenticates with
`X-Shizu-Signature: hex(HMAC-SHA256(raw_body, secret))`; without a secret
the endpoint fail-closes with 503):

```bash
# systemd unit or /etc/shizuappstore/env:
SHIZU_ADMIN_SECRET=$(openssl rand -hex 32)
```

The `Admin:HmacSecret` config key works too, but env is preferred; never
commit the secret to appsettings.json.

## aapt2 (icon extraction)

The enricher shells out to `aapt2 dump badging` to read package, version
and icon paths from downloaded APKs. APKs are deleted right after; only
the 192px PNG icon is kept. Install via the Android command-line tools:

```bash
sudo pacman -S unzip
# download commandlinetools-linux-<rev>_latest.zip from
# https://developer.android.com/studio#command-line-tools-only
unzip commandlinetools-linux-*.zip -d /tmp/cmdline-tools
sudo install -d /opt/android-sdk
sudo cp -r /tmp/cmdline-tools/cmdline-tools /opt/android-sdk/cmdline-tools/latest
sudo chown -R shizu:shizu /opt/android-sdk
sudo -u shizu env ANDROID_HOME=/opt/android-sdk bash -c \
  'yes | /opt/android-sdk/cmdline-tools/latest/bin/sdkmanager --licenses'
sudo -u shizu env ANDROID_HOME=/opt/android-sdk \
  /opt/android-sdk/cmdline-tools/latest/bin/sdkmanager --install \
  "build-tools;34.0.0" "platforms;android-35"
```

The zip unpacks a `cmdline-tools/` directory whose contents must end up in
`cmdline-tools/latest/` (the canonical layout); `sdkmanager` cannot locate
the SDK root otherwise. Install `platforms;android-35` too: Paparazzi
compiles against `compileSdk 35`, so the platform is required even though
the runtime server never builds APKs.

Then point `Enrichment:Aapt2Path` at
`/opt/android-sdk/build-tools/34.0.0/aapt2` (verified with build-tools
35.0.0; the `dump badging` format is stable across 30–36).

Notes:

- aapt2 only needs `build-tools` (~50 MB) and `dump badging` is read-only:
  no `android.jar` and no emulator are involved. The `platforms;android-35`
  package is only for the Paparazzi icon renderer (see below).
- The min-SDK line is `minSdkVersion:'NN'` on modern build-tools
  (`sdkVersion:'NN'` on very old ones); the parser accepts both. Icon
  lines are `application-icon-{density}:'path'`.
- Sanity check on the server:
  `aapt2 version` → `Android Asset Packaging Tool (aapt) 2.x-…`.

## apksigner (signing-cert fingerprints)

Installable builds are keyed by signing identity: each `app_downloads`
row holds the fingerprints of one build candidate. f-droid.org hosts
F-Droid community rebuilds (a different key than the developer's unless
the build is reproducible); IzzyOnDroid (`apt.izzysoft.de`) hosts the
developers' own upstream builds (normally crawled from GitHub/GitLab),
so Izzy builds are signature-compatible with forge releases.

- `sig_sha256` / `sig_md5` come from `apksigner verify --print-certs`
  (SHA-256 + MD5 lines; rotation-aware, space-joined sets), or from the
  F-Droid index `<sig>` 32-hex MD5 for index-only rows.

Client matching: hash the installed app's signing cert and filter
`downloads[]` to candidates whose `sigSha256`/`sigMd5` match (membership
match against the space-joined sets). Among matches, compare the
candidate's `versionCode` against the installed version and offer the
primary download for a fresh install. There is no server-side source
lock and clients never switch signatures.

`apksigner` ships with the same build-tools package as `aapt2` but needs
Java:

```bash
sudo pacman -S jdk21-openjdk
```

`jdk21-openjdk` (not `jre-openjdk-headless`) is the right pick here
because the Paparazzi renderer below needs a full JDK anyway; Java 21 is
what Gradle 8.14 is verified against. The startup probe requires
`apksigner --version` to exit 0, so a missing Java runtime refuses boot
(same as aapt2/Gradle); per-APK signature failures are still best-effort
and leave `sig_sha256`/`sig_md5` null instead. Java lands on the default
`PATH` as `/usr/bin/java` via the `archlinux-java` symlink.

Then point `Enrichment:ApksignerPath` at
`/opt/android-sdk/build-tools/34.0.0/apksigner` (verified with 35.0.0,
which prints SHA-256 + SHA-1 + MD5 digests per signer). Signature
extraction is best-effort: without Java/apksigner `sig_sha256`/`sig_md5`
stay null and enrichment still succeeds (the F-Droid `<sig>` MD5 from the
index is always recorded for F-Droid/Izzy rows).

## Icon rendering (Gradle + Paparazzi)

XML launcher icons (adaptive icons, vectors) render through Google
LayoutLib via Paparazzi (`tools/icon-render`), so future drawable
features keep working without parser changes. Needs a JDK, Gradle and
one SDK platform (the tool targets `compileSdk 35`):

```bash
sudo pacman -S jdk21-openjdk
# Arch's `gradle` package is 9.x, which AGP 8.13.2 rejects; install 8.14
# standalone instead of adding a wrapper to the tool directory.
cd /tmp && curl -fSLO https://services.gradle.org/distributions/gradle-8.14-bin.zip
echo "61ad310d3c7d3e5da131b76bbf22b5a4c0786e9d892dae8c1658d4b484de3caa  gradle-8.14-bin.zip" | sha256sum -c
unzip -q gradle-8.14-bin.zip -d /tmp/gradle-unpack
sudo cp -r /tmp/gradle-unpack/gradle-8.14 /opt/gradle-8.14
sudo chown -R shizu:shizu /opt/gradle-8.14
sudo -u shizu /opt/gradle-8.14/bin/gradle --version   # sanity check
```

The tool directory itself is synced by `deploy/deploy.sh` (excluding
`build/`, `.gradle/` and `local.properties`), so create the two host-local
files once on the server:

```bash
sudo -u shizu sh -c 'printf "sdk.dir=/opt/android-sdk\n" > /opt/shizuappstore/icon-render/local.properties'
sudo -u shizu sh -c 'printf "org.gradle.jvmargs=-Xmx512m -XX:MaxMetaspaceSize=256m\norg.gradle.daemon.idletimeout=600000\n" > /opt/shizuappstore/gradle-home/gradle.properties'
```

Gradle writes its daemon registry and caches into `GRADLE_USER_HOME`
(`/opt/shizuappstore/gradle-home`, set by the unit) and the `build/`
directory inside the tool; both are in the unit's `ReadWritePaths`, which
`ProtectSystem=strict` otherwise blocks. Heap is capped at every level so
the first icon backfill cannot throw the host into swap: the daemon at
512m (file above), the forked Paparazzi test JVM at 512m plus a single
fork (`tools/icon-render/app/build.gradle`), one Gradle worker
(`tools/icon-render/gradle.properties`), the daemon's idle timeout at
10min, and the unit itself at `MemoryHigh=1500M` / `MemoryMax=1800M` /
`MemorySwapMax=256M` / `CPUQuota=150%` with `Nice=5` and low IO weight.
Raise these only after watching `systemctl status` and `free -h` during a
full pass.

Knobs (`Enrichment:` section): `GradlePath` (point at
`/opt/gradle-8.14/bin/gradle`), `IconToolDir` (point at
`/opt/shizuappstore/icon-render`), `PaparazziTimeout` (default
15min per icon; the Gradle daemon stays warm between renders), and
`IconRenderCpuAffinity` (default null; a `taskset -c` CPU list such as
`0` pins render builds to one core when the backfill competes with the
desktop). After enabling the affinity, stop any daemon left over from
before the change (`gradle --stop`) so the next launch starts it inside
`taskset`.

`RunLogPath` (default null) appends a human-readable section per sync
pass: a header, one line per scanned app as it finishes
(`[ 12/315] slug (Display Name)  OK|ok|skip|excluded|FAIL  detail`), a
totals footer, then the same catalog health snapshot as `GET /v1/issues`.
Runs are separated by a blank line. The log rotates at run boundaries
once it reaches 1 MiB, keeping exactly two files (`<path>` and
`<path>.1`, the previous `.1` overwritten), so no external logrotate is
needed. Example: `Enrichment__RunLogPath=/var/log/shizu/enrichment-runs.log`.

Smoke test from the repo root:

```bash
gradle -p tools/icon-render renderIcon -PstagedRes=<res-dir> \
  -PiconName=<drawable> -PiconPx=432 -Pout=/tmp/icon.png --rerun-tasks
```

`-PstagedRes` is the staged `res` dir the enricher prepares per icon;
`--rerun-tasks` defeats stale up-to-date checks while iterating.
Production renders run batched: one `renderIconBatch -PstagedRes=<shared-res> -Pbatch=<manifest>`
per pass, because each Gradle invocation pays a full task graph +
test-JVM + LayoutLib boot (~2.5min per icon unbatched). If the batch task
fails for every icon at once, check that `app/build.gradle` forwards the
`-Pbatch` value as the `iconBatch` test sysprop; a missing forward
silently runs single-icon mode against a nonexistent drawable.

## Startup tool check

The server verifies `aapt2 version`, `apksigner --version` and
`gradle --version` (all must exit 0) at startup, logs the detected
versions, and refuses to boot without them instead of serving a catalog
that never enriches. `Enrichment:IconToolDir` is resolved against the
process working directory, so the production config must use the absolute
`/opt/shizuappstore/icon-render` (the unit's `WorkingDirectory` is
`/opt/shizuappstore/app`, which contains no `tools/`). Only the `Testing`
environment used by the integration suite skips the check.

## List clone

```bash
sudo -u shizu git clone https://github.com/timschneeb/awesome-shizuku /opt/shizuappstore/list
```

The fast loop `git fetch`es before each pass, fast-forwards the local
branch to its upstream (`git merge --ff-only`), and records the HEAD in
`sync_runs.head_commit`. The clone is never edited locally; if a
fast-forward cannot apply (diverged history or a dirty tree) the worker
deletes the clone and re-clones it from origin.

## Deploy flow

One-time unit install (after provisioning above):

```bash
sudo cp deploy/shizuappstore.service deploy/shizu-sync-once.service /etc/systemd/system/
sudo install -d -m 750 -o root -g shizu /etc/shizuappstore
# /etc/shizuappstore/env holds the secrets. Group-readable by shizu so
# manual one-shot runs can source it; systemd reads it as root either way.
sudo sh -c 'umask 027; printf "SHIZU_GITHUB_TOKEN=github_pat_…\nSHIZU_ADMIN_SECRET=%s\n" "$(openssl rand -hex 32)" > /etc/shizuappstore/env'
sudo chown root:shizu /etc/shizuappstore/env
sudo chmod 640 /etc/shizuappstore/env
sudo systemctl daemon-reload
sudo systemctl enable shizuappstore.service
```

The unit assumes `/opt/shizuappstore` is traversable by the service user
and ships `GRADLE_USER_HOME=/opt/shizuappstore/gradle-home`,
`ANDROID_HOME=/opt/android-sdk`, `LogsDirectory=shizu`, and
`ReadWritePaths` for `icons`, `icon-render`, `gradle-home` and `list`
(the list clone is `git fetch`ed in place).

Exposure: a token-managed Cloudflare Tunnel on the same host routes
`shizustore.timschneeberger.me` to `http://localhost:5137` (configured in
the Cloudflare dashboard, not on disk). Nothing else needs to be added;
verify from a client with `curl https://shizustore.timschneeberger.me/v1/apps`.
If the hostname ever gets a Zero Trust Access application, remember the
API must stay reachable without a login prompt.

Each deploy (from the repo root on the dev machine):

```bash
./deploy/deploy.sh <ssh-host>
```

The script publishes the single-file binary (`linux-x64` profile), builds
a fresh `efbundle`, rsyncs `app/` (never touching
`appsettings.Production.json` or `/etc/shizuappstore/env`), syncs
`tools/icon-render/` (host-local `build/`, `.gradle/`, `local.properties`
excluded), runs the bundle as `shizu`, and restarts the service. Both
rsyncs run the remote side as `shizu` (`--rsync-path="sudo -u shizu
rsync"`), so the service tree never becomes writable by the login user.

First boot is the initial backfill: `Sync:RunOnStartup` defaults to true,
so the worker parses the list clone, upserts the catalog, and enriches
all ~350 apps (a slow one-time pass over the Releases APIs; this is what
the PAT is for). To keep the API down until the catalog is populated,
leave the service disabled and run the backfill through the one-shot
unit, which carries the same environment, hardening and resource caps:

```bash
sudo systemctl start shizu-sync-once   # returns immediately (Type=oneshot)
systemctl status shizu-sync-once
journalctl -u shizu-sync-once -f
sudo systemctl enable --now shizuappstore
```

Never start the one-shot while `shizuappstore.service` is active: two
enrich passes would race on the same rows. A failed run reports
`Result=exit-code` (or `timeout`) in `systemctl status`; `reset-failed`
before retrying a `--sync-once --full` re-run.

`appsettings.Production.json` is written before the first
`deploy.sh` run, because the rsync into `app/` excludes it and the
migration bundle needs the connection string. Later passes are cheap: a
HEAD-gated fast loop every
15 min plus a nightly full re-check at 03:00 UTC, both tunable under
`Sync:`. Each fast-loop pass also runs the release poll: one release-feed
call per forge app plus one F-Droid/Izzy index fetch per repo, so the
PAT's 5000 calls/hr covers the ~1200/hr steady state. Without
`SHIZU_GITHUB_TOKEN` the poll stays off (startup warning) and fast passes
enrich due-only apps; `Sync:PollEnabled` / `Sync:PollParallelism` tune it.
