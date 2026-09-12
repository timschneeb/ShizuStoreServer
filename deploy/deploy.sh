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
SERVICE=shizuappstore.service

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
rsync -av --delete \
  --exclude 'appsettings.Production.json' \
  src/ShizuAppStoreServer/bin/Release/net10.0/publish/ "$SERVER:$APP_DIR/"
scp deploy/out/efbundle "$SERVER:$APP_DIR/efbundle"

# 4. Migrate + restart on the server.
# The connection string comes from the server's own
# appsettings.Production.json / /etc/shizuappstore/env, so no secrets cross
# the wire here.
ssh "$SERVER" "sudo -u shizu $APP_DIR/efbundle && sudo systemctl restart $SERVICE"
ssh "$SERVER" "systemctl is-active $SERVICE"
