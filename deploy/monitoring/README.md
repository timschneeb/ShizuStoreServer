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
Prometheus binds `127.0.0.1:9090` on zbox only. Alerts are not configured.

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

Token sources:

- `shizu-api.token`: `SHIZU_ADMIN_SECRET` (or `SHIZU_ADMIN_TOKEN`) from
  `/etc/shizuappstore/env` on srv1.
- `shizustore-web.token`: `Metrics:Token` from
  `/opt/shizustore-web/app/appsettings.Production.json` on srv1.
- `node-srv1.pass`: the plaintext behind the bcrypt hash in srv1's
  `/etc/prometheus-node-exporter/web.yml`.

## Verification

```bash
# zbox
docker compose -f /home/tim/monitoring/compose.yml ps
curl -s localhost:9090/api/v1/targets | jq -r '.data.activeTargets[] | "\(.labels.job) \(.health)"'
curl -s localhost:3001/api/health
# from anywhere with the node-srv1 password
curl -su monitoring:"$PASSWORD" https://node-srv1.timschneeberger.me/metrics | head
```
