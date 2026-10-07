# Monitoring stack

Prometheus + Grafana + node_exporter running as a Docker Compose stack on
`zbox`, scraping:

- `zbox` host metrics from the sidecar node_exporter (job `node-zbox`)
- `srv1` host metrics from the cloudflared tunnel hostname
  `node-srv1.timschneeberger.me` (job `node-srv1`, basic auth)
- the ShizuStore API at `https://shizustore.timschneeberger.me/metrics`
  (job `shizu-api`, bearer auth)
- the ShizuStore storefront at `https://shizustore.com/metrics`
  (job `shizustore-web`, bearer auth)

Grafana listens on `3001` (host port `3000` is taken by lanraragi) and is
reachable at `http://<zbox-lan-ip>:3001` and `http://<zbox-tailscale-ip>:3001`.
Prometheus binds `127.0.0.1:9090` on zbox only. Grafana unified alerting
delivers the ShizuStore health rules to Telegram (see Alerts).

## srv1 (node_exporter)

```bash
sudo pacman -Sy prometheus-node-exporter
sudo install -d -m 755 /etc/prometheus-node-exporter
# bcrypt hash for basic auth user "monitoring" (read it from a file, not argv)
printf '%s\n' "$PASSWORD" | htpasswd -inB -C 10 monitoring
sudo install -m 640 -o root -g node_exporter web.yml /etc/prometheus-node-exporter/web.yml
sudo install -m 644 node-exporter.conf.d /etc/conf.d/prometheus-node-exporter
sudo install -d -m 755 /etc/systemd/system/prometheus-node-exporter.service.d
sudo install -m 644 node-exporter-limits.conf /etc/systemd/system/prometheus-node-exporter.service.d/limits.conf
sudo systemctl daemon-reload
sudo systemctl enable --now prometheus-node-exporter
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:9100/metrics            # 401
curl -s -o /dev/null -w '%{http_code}\n' -u monitoring:"$PASSWORD" http://127.0.0.1:9100/metrics  # 200
```

Then add the Cloudflare tunnel hostname (dashboard, same tunnel as
shizustore.com): `node-srv1.timschneeberger.me` -> `http://localhost:9100`.
Do not attach a Zero Trust Access application; the exporter basic auth is the
gate.

## zbox (Prometheus + Grafana + node_exporter)

One-time file prep, expected under `/home/tim/monitoring`:

```
prometheus/prometheus.yml
prometheus/secrets/{shizu-api.token,shizustore-web.token,node-srv1.pass}   # 600, owned by 65534
.env                                                                      # 600, GF_SECURITY_ADMIN_PASSWORD
grafana/provisioning/...
grafana/dashboards/...
```

`/home/tim` must be traversable by container users: `chmod 751 /home/tim`.

Deploy the stack by pasting `compose.yml` into Portainer (Stacks -> Add
stack -> Web editor) and setting `GF_SECURITY_ADMIN_PASSWORD` in the stack's
environment variables. Portainer resolves `env_file` paths inside its own
container, so the compose supplies Grafana's variables inline; the CLI reads
the same value from `.env` next to the compose file. The community dashboards come from
`grafana/dashboards/fetch-dashboards.sh` (downloads 19924, 19925, 1860 and
patches them for provisioning); `shizustore-overview.json` is maintained here.
It has a KPI band (24h requests per service, error rate, active requests), HTTP
request panels (rate, p95, error rate, connections, methods), a
requests/latency/errors-by-path table, a top-apps table from
`shizu_app_views_total` (client and webstore sources), and job/install/host
rows.

Token sources:

- `shizu-api.token`: `SHIZU_ADMIN_SECRET` (or `SHIZU_ADMIN_TOKEN`) from
  `/etc/shizuappstore/env` on srv1.
- `shizustore-web.token`: `Metrics:Token` from
  `/opt/shizustore-web/app/appsettings.Production.json` on srv1.
- `node-srv1.pass`: the plaintext behind the bcrypt hash in srv1's
  `/etc/prometheus-node-exporter/web.yml`.

## Alerts (Grafana unified alerting)

Provisioned from `grafana/provisioning/alerting/`:

- `rules.yaml`: folder `ShizuStore`, group `shizu-health`, 1m interval. API
  scrape down, API p95 above 1.5s, sync stalled (no successful sync pass in
  45m), catalog identity issue (a `catalog` kind row in the latest sync
  snapshot), storefront scrape down, storefront p95, storefront 5xx.
- `policies.yaml`: root route to `shizu-telegram`, grouped by alertname and
  service.
- `templates.yaml`: `shizu.telegram.message`, an HTML message with a bold
  state and an explicit `<a href>` link.
- `contact-points.example.yaml`: copy to `contact-points.yaml` on the Grafana
  host only, fill in the bot token and chat id, and keep it out of version
  control. The settings are `parse_mode: HTML` and
  `message: '{{ template "shizu.telegram.message" . }}'`.

Telegram drops `<a>` anchors whose host has no dot, so `GF_SERVER_ROOT_URL`
and `GF_SERVER_DOMAIN` must be the public Grafana hostname (the compose sets
`https://grafana.timschneeberger.me`); that is what makes the "Open in
Grafana" link clickable. Rules are read-only in the UI: edit the files, then
reload with `POST /api/admin/provisioning/alerting/reload`.

## Verification

```bash
# zbox
docker compose -f /home/tim/monitoring/compose.yml ps
curl -s localhost:9090/api/v1/targets | jq -r '.data.activeTargets[] | "\(.labels.job) \(.health)"'
curl -s localhost:3001/api/health
# from anywhere with the node-srv1 password
curl -su monitoring:"$PASSWORD" https://node-srv1.timschneeberger.me/metrics | head
```
