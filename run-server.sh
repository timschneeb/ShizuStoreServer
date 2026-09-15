#!/usr/bin/env bash
# Start the ShizuAppStoreServer from the repo root (IconToolDir resolves
# relative to it) with the persisted environment in server.env.
# Usage: ./run-server.sh  (rebuild first: dotnet build ShizuAppStoreServer.slnx -c Release)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENV_FILE="$HOME/.local/share/shizuappstore/server.env"
LOG_FILE="/var/tmp/shizu-full-run/server.log"

if [[ ! -f "$ENV_FILE" ]]; then
    echo "Missing $ENV_FILE" >&2
    exit 1
fi

if pgrep -f "ShizuAppStoreServer.dll" >/dev/null; then
    echo "Server already running; stop it first (pkill -f ShizuAppStoreServer.dll)." >&2
    exit 1
fi

set -a
# shellcheck disable=SC1090
source "$ENV_FILE"
set +a

cd "$ROOT"
nohup dotnet src/ShizuAppStoreServer/bin/Release/net10.0/ShizuAppStoreServer.dll >"$LOG_FILE" 2>&1 &
echo "Started pid $! (log: $LOG_FILE)"
