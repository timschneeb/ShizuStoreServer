#!/usr/bin/env bash
# Downloads the community dashboards used by the monitoring stack and patches
# them for file provisioning: drop __inputs/__requires and bind the datasource
# placeholder to the provisioned Prometheus uid.
set -euo pipefail
dir="$(cd "$(dirname "$0")" && pwd)"

fetch() {
  local id="$1" out="$2"
  curl -fsSL "https://grafana.com/api/dashboards/${id}/revisions/latest/download" -o "${dir}/${out}"
}

fetch 19924 19924-aspnetcore.json
fetch 19925 19925-aspnetcore-endpoint.json
fetch 1860 1860-node-exporter-full.json

python3 - "$dir" <<'PY'
import json
import pathlib
import sys

target = pathlib.Path(sys.argv[1])
for name in ("19924-aspnetcore.json", "19925-aspnetcore-endpoint.json"):
    path = target / name
    doc = json.loads(path.read_text())
    doc.pop("__inputs", None)
    doc.pop("__requires", None)
    doc["id"] = None
    doc = json.loads(json.dumps(doc).replace("${DS_PROMETHEUS}", "prometheus"))
    path.write_text(json.dumps(doc, indent=2))
PY

echo "Dashboards written to ${dir}"
