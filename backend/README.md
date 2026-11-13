# Backora — backend (.NET 10 · PostgreSQL)

Enterprise backup orchestration. Backora v2 is a full rebuild of Backora v1 (Laravel); `Backora.Tools import-legacy` migrates a v1 database.

| | |
|---|---|
| API | `src/Backora.Api` — ASP.NET Core minimal APIs, `/api/v1`, plus legacy-compatible `/api/token` |
| Worker | `src/Backora.Worker` — lease-based execution of backups, S3 offloads and restores; scheduler; retention; notifications |
| Tools | `src/Backora.Tools` — `migrate`, `create-admin`, `rotate-keys`, `import-legacy`, `verify-import` |
| Database | PostgreSQL 18, EF Core migrations, least-privilege roles (`database/`) |
| Frontend | `../frontend` — Blazor Web App (BFF) |

## Quick start (containers)

```bash
cd deploy && ./generate-env.sh && docker compose --profile lab up -d --build
# → http://localhost:8443
```

## Documentation

2. [Architecture](docs/02-architecture.md)
3. [Database design](docs/03-database-design.md)
4. [API reference](docs/04-api-reference.md) (OpenAPI: `/openapi/v1.json`)
5. [Security model](docs/05-security.md)
6. [Backup engine, restore safety, retention, scheduling](docs/06-backup-engine.md)
7. [Development, deployment, configuration, monitoring, tests](docs/07-operations.md)
8. [Known limitations & behaviour changes](docs/08-known-limitations.md)
