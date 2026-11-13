# Backora V2 — production deployment

One Docker Compose stack: PostgreSQL, the migrator, the API, the V2 frontend and a Caddy TLS proxy.
Only the proxy is published (80/443). PostgreSQL sits on an internal-only network; the API is the only service with
outbound access (SSH to the execution host, HTTPS to S3). Every service runs read-only, non-root where the image
allows, with all Linux capabilities dropped and rotated JSON logs.

## Requirements
- Docker Engine 24+ with Compose v2, 2 vCPU / 4 GB RAM minimum.
- A DNS name for Backora (public for `acme`, or internal with `internal`/`files` TLS).
- The execution host's SSH private key (the API opens SSH only to that host).

## First install
```bash
cd deploy/production
./generate-config.sh backora.example.com admin@example.com acme   # or: internal | files
cp /path/to/execution_host_ed25519 secrets/execution_host_key && chmod 600 secrets/execution_host_key
# TLS mode "files" only: copy tls.crt (full chain) and tls.key into certs/
docker compose up -d --build
docker compose ps        # all services healthy; migrator "exited (0)"
```
Open `https://<your domain>`, sign in with the admin email and the password from
`backend/src/Backora.Tools/appsettings.Production.json` (`Bootstrap:AdminPassword`), change the password and turn on two-step verification. Then trust the execution host under **Execution host**.

## TLS modes (`BACKORA_TLS_MODE`)
| Mode | Use when | Certificates |
|---|---|---|
| `acme` | Public DNS name, ports 80/443 reachable from the internet | Let's Encrypt, renewed automatically |
| `internal` | Private network only | Caddy's internal CA; distribute its root: `docker compose cp proxy:/data/caddy/pki/authorities/local/root.crt .` |
| `files` | Company PKI | `certs/tls.crt` (full chain) and `certs/tls.key` |

## Configuration and secrets
Non-sensitive settings live in `appsettings.Production.json` (committed, built into the images):
`backend/src/Backora.Api/appsettings.Production.json` and `frontend/src/Backora.Web/appsettings.Production.json`.
Only secrets are injected as environment variables, which override appsettings:

| Secret value | Docker Compose | OpenShift |
|---|---|---|
| `ConnectionStrings__Backora`, `Encryption__Keys__primary`, `Jwt__SigningKey` | `env/api.env` | Secret `backora-api` → env vars on `backora-api` |
| Execution host SSH key | `secrets/execution_host_key` | Secret `backora-execution-host-key`, mounted at `/run/secrets` |

For the local Compose run, `env/*.env` also overrides a few non-secret values (local URL, `http://api:8080`, Docker
network ranges, separate migrator instead of `Database__MigrateOnStartup`).

## Secrets and backups
- `.env`, `env/`, `secrets/` and `certs/` are git-ignored. Keep them in your secret manager.
- **The encryption key (`Encryption__Keys__primary`) must be backed up separately.** Without it the stored credentials (S3 keys, SSH
  passwords) cannot be decrypted.
- Restore a database dump into a new install (before the API first starts): `docker compose up -d postgres`, then
  `docker compose exec -T postgres pg_restore -U postgres -d backora < backora.dump`.
- Database backup: `docker compose exec -T postgres pg_dump -U postgres -Fc backora > backora-$(date +%F).dump`
- Data-protection keys live in the `api-keys` and `frontend-keys` volumes; keep them across upgrades so sessions and
  protected values survive.

## Upgrades
```bash
git pull   # or unpack the new release
docker compose build && docker compose up -d     # the migrator runs before the API starts
```
Pin `BACKORA_VERSION` (and `BACKORA_REGISTRY` if you push images to a registry) to roll forward or back.

## Operations
- Health: `https://<domain>/health` (frontend), API `/health/ready` inside the stack (`docker compose ps`).
- Logs: `docker compose logs -f api frontend proxy`.
- Concurrency: `BACKORA_MAX_CONCURRENT_BACKUPS`, `BACKORA_MAX_CONCURRENT_TRANSFERS`.
- Tracing: set `OTEL_EXPORTER_OTLP_ENDPOINT`.
- One frontend replica: Blazor Server keeps live circuits per browser. Scale out only behind sticky sessions.

Kubernetes/OpenShift manifests: `backend/deploy/kubernetes/`.
