#!/bin/sh
# Runs once on first PostgreSQL start (docker-entrypoint-initdb.d). Creates least-privilege roles:
#   backora_owner — owns the schema; used ONLY by the migrator (DDL).
#   backora_app   — used by API and worker; DML only, no DDL, cannot rewrite the audit trail.
set -eu
: "${BACKORA_OWNER_PASSWORD:?}" "${BACKORA_APP_PASSWORD:?}"
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
  -v owner_pw="$BACKORA_OWNER_PASSWORD" -v app_pw="$BACKORA_APP_PASSWORD" <<'SQL'
CREATE ROLE backora_owner LOGIN PASSWORD :'owner_pw';
CREATE ROLE backora_app LOGIN PASSWORD :'app_pw' NOSUPERUSER NOCREATEDB NOCREATEROLE;
CREATE DATABASE backora OWNER backora_owner;
\connect backora
REVOKE ALL ON SCHEMA public FROM PUBLIC;
ALTER SCHEMA public OWNER TO backora_owner;
GRANT USAGE ON SCHEMA public TO backora_app;
ALTER DEFAULT PRIVILEGES FOR ROLE backora_owner IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO backora_app;
ALTER DEFAULT PRIVILEGES FOR ROLE backora_owner IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO backora_app;
SQL
