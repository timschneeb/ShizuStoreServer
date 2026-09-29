#!/usr/bin/env bash
# Recurring deploy of the public storefront (shizustore.com): publish the
# single binary -> rsync to the server -> restart the unit. Run from the repo
# root on the dev machine. The API and its migrations are untouched; the
# storefront reads the database directly (read-only).
#
# Usage: ./deploy/deploy-web.sh <ssh-host>   # e.g. ./deploy/deploy-web.sh shizu-server
set -euo pipefail

SERVER="${1:?usage: ./deploy/deploy-web.sh <ssh-host>}"
APP_DIR=/opt/shizustore-web/app
SERVICE=shizustore-web.service
# The service tree is owned by the shizu user; run the remote rsync as that
# user (passwordless sudo) instead of widening permissions.
REMOTE_RSYNC="sudo -u shizu rsync"

# 1. Single-file publish (profile: Properties/PublishProfiles/linux-x64.pubxml).
dotnet publish src/ShizuAppStoreServer.Web/ShizuAppStoreServer.Web.csproj \
  -p:PublishProfile=linux-x64

# 2. Ship the binary. appsettings.Production.json lives on the server (icon
# store path, connection string password) and is never overwritten here.
rsync -av --delete --rsync-path="$REMOTE_RSYNC" \
  --exclude 'appsettings.Production.json' \
  src/ShizuAppStoreServer.Web/bin/Release/net10.0/publish/ "$SERVER:$APP_DIR/"

# 3. Restart and confirm.
ssh "$SERVER" "sudo systemctl restart $SERVICE"
ssh "$SERVER" "systemctl is-active $SERVICE"
