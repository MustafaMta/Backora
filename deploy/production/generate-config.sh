#!/bin/sh
# Writes the production configuration with fresh random secrets. Never commit the results.
#   ./generate-config.sh <public-host[:port]> <admin-email> [acme|internal|files]
# Outputs (chmod 600, git-ignored): .env (PostgreSQL + proxy) and env/{api,migrator,frontend}.env (.NET settings).
# On OpenShift set the same keys as env vars and Secrets instead.
# To carry over an existing installation, export before running:
#   BACKORA_ENCRYPTION_KEY (required to decrypt stored credentials), BACKORA_ADMIN_EMAIL / BACKORA_ADMIN_PASSWORD,
#   BACKORA_EXECUTION_HOST_KEY_FILE (path to the execution host private key on this machine).
set -eu
cd "$(dirname "$0")"
[ $# -ge 2 ] || { echo "usage: $0 <public-host[:port]> <admin-email> [acme|internal|files]" >&2; exit 2; }
[ -f .env ] && { echo ".env already exists; refusing to overwrite production secrets." >&2; exit 1; }
HOSTPORT=$1; ADMIN=${BACKORA_ADMIN_EMAIL:-$2}; TLS=${3:-acme}
case "$TLS" in acme|internal|files) ;; *) echo "TLS mode must be acme, internal or files" >&2; exit 2;; esac
DOMAIN=${HOSTPORT%%:*}
rnd() { openssl rand -base64 "$1" | tr -d '\n'; }
pw() { openssl rand -hex 24; }
PG=$(pw); OWNER=$(pw); APP=$(pw)
ENC=${BACKORA_ENCRYPTION_KEY:-$(rnd 32)}
JWT=$(rnd 48)
ADMINPW=${BACKORA_ADMIN_PASSWORD:-$(openssl rand -base64 18 | tr -d '\n/+=')Aa1!}
KEYFILE=${BACKORA_EXECUTION_HOST_KEY_FILE:-./secrets/execution_host_key}
URL=https://$HOSTPORT
mkdir -p env && chmod 700 env
umask 077
cat > .env <<ENV
BACKORA_VERSION=2.0.0
BACKORA_DOMAIN=$DOMAIN
BACKORA_TLS_MODE=$TLS
ACME_EMAIL=$ADMIN
POSTGRES_PASSWORD=$PG
BACKORA_OWNER_PASSWORD=$OWNER
BACKORA_APP_PASSWORD=$APP
BACKORA_EXECUTION_HOST_KEY_FILE=$KEYFILE
ENV
cat > env/api.env <<ENV
ConnectionStrings__Backora='Host=postgres;Port=5432;Database=backora;Username=backora_app;Password=$APP;Maximum Pool Size=50'
Platform__PublicUrl='$URL'
Encryption__ActiveKeyId='primary'
Encryption__Keys__primary='$ENC'
Jwt__SigningKey='$JWT'
ForwardedHeaders__KnownNetworks__0='172.16.0.0/12'
Database__MigrateOnStartup='false'
Worker__EnableOperations='false'
Worker__EnableScheduler='false'
Worker__EnableRetention='false'
Worker__EnableNotifications='false'
ExecutionHost__PrivateKeyFile='/run/secrets/execution_host_key'
ExecutionHost__TargetKeyFile='/run/secrets/execution_host_key'
ENV
cat > env/migrator.env <<ENV
ConnectionStrings__Backora='Host=postgres;Port=5432;Database=backora;Username=backora_owner;Password=$OWNER'
Encryption__ActiveKeyId='primary'
Encryption__Keys__primary='$ENC'
Bootstrap__AdminEmail='$ADMIN'
Bootstrap__AdminPassword='$ADMINPW'
ENV
cat > env/frontend.env <<ENV
Backend__BaseUrl='http://api:8080'
Frontend__PublicUrl='$URL'
Frontend__AllowInsecureCookies='false'
ENV
chmod 600 .env env/api.env env/migrator.env env/frontend.env
echo "Wrote .env and env/{api,migrator,frontend}.env (Worker switches start OFF; turn them on after cutover)."
echo "Public URL: $URL  ·  first admin: $ADMIN (password in env/migrator.env; only used when no users exist)"
echo "Back up the encryption key (Encryption__Keys__primary): without it stored credentials cannot be decrypted."
