# Database

* **Engine:** PostgreSQL 18 (any supported 16+ release works; tests run against `postgres:18-alpine`).
* **Schema:** EF Core migrations in `src/Backora.Infrastructure/Persistence/Migrations`, applied **explicitly** by the migrator (`Backora.Tools migrate` / the `migrator` container). They are never applied implicitly at API start in production (`Database__MigrateOnStartup=false`).
* **Roles:** `init/01-create-roles.sh` creates `backora_owner` (DDL, migrator only) and `backora_app` (DML only, used by the API and worker). The `audit_events` table is additionally protected by triggers that reject UPDATE, DELETE and TRUNCATE for every role (migration `AuditAppendOnly`). For managed PostgreSQL, run the SQL in that script manually as an administrator.
* **Rollback:** each migration has a `Down`. To produce an idempotent SQL rollback script for DBA review: `dotnet ef migrations script <to> <from> -p src/Backora.Infrastructure -s src/Backora.Infrastructure --idempotent`. Production rollback = restore the pre-migration database backup (taken by the cutover procedure) and redeploy the previous images.
* See `docs/03-database-design.md` for the entity model and indexes.
