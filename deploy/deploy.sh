#!/usr/bin/env bash
# Recurring deploy: publish single binary -> rsync to the server -> run the
# EF migration bundle -> restart the service. Run from the repo root on the
# dev machine. Prerequisites: .NET 10 SDK, dotnet-ef, ssh + rsync access to
# the server (one-time setup: docs/server-setup.md).
#
# Usage: ./deploy/deploy.sh <ssh-host>   # e.g. ./deploy/deploy.sh shizu-server
set -euo pipefail

SERVER="${1:?usage: ./deploy/deploy.sh <ssh-host>}"
APP_DIR=/opt/shizuappstore/app
ICON_TOOL_DIR=/opt/shizuappstore/icon-render
SERVICE=shizuappstore.service
# The service tree is owned by the shizu user; run the remote rsync as that
# user (passwordless sudo) instead of widening permissions.
REMOTE_RSYNC="sudo -u shizu rsync"

# 1. Single-file publish (profile: Properties/PublishProfiles/linux-x64.pubxml).
dotnet publish src/ShizuAppStoreServer/ShizuAppStoreServer.csproj \
  -p:PublishProfile=linux-x64

# 2. Fresh EF migration bundle (runs on the server, needs only the runtime).
dotnet ef migrations bundle \
  --project src/ShizuAppStoreServer.Core/ShizuAppStoreServer.Core.csproj \
  --startup-project src/ShizuAppStoreServer/ShizuAppStoreServer.csproj \
  --configuration Release \
  --output deploy/out/efbundle \
  --force

# 3. Ship the binary, production config note, and the bundle.
# (appsettings.Production.json + /etc/shizuappstore/env live on the server
# and are never overwritten by this script.)
rsync -av --delete --rsync-path="$REMOTE_RSYNC" \
  --exclude 'appsettings.Production.json' \
  src/ShizuAppStoreServer/bin/Release/net10.0/publish/ "$SERVER:$APP_DIR/"
rsync -av --rsync-path="$REMOTE_RSYNC" deploy/out/efbundle "$SERVER:$APP_DIR/efbundle"

# 3b. The Paparazzi icon tool is not part of the publish output, so it is
# synced separately. build/, .gradle/, and local.properties are host-local
# state (Gradle caches and the SDK path) and stay untouched.
rsync -av --delete --rsync-path="$REMOTE_RSYNC" \
  --exclude 'build/' --exclude '.gradle/' --exclude 'local.properties' \
  tools/icon-render/ "$SERVER:$ICON_TOOL_DIR/"

# 4. Migrate + restart on the server.
# The connection string comes from the server's own
# appsettings.Production.json / /etc/shizuappstore/env, so no secrets cross
# the wire here. The bundle resolves its config from $APP_DIR, hence the cd.
ssh "$SERVER" "cd $APP_DIR && sudo -u shizu env ASPNETCORE_ENVIRONMENT=Production $APP_DIR/efbundle"
ssh "$SERVER" "sudo systemctl restart $SERVICE"
ssh "$SERVER" "systemctl is-active $SERVICE"
