#!/bin/sh
# Generates deploy/.env with fresh random secrets for LOCAL/LAB use. Never commit the result.
set -eu
cd "$(dirname "$0")"
[ -f .env ] && { echo ".env already exists — refusing to overwrite secrets." >&2; exit 1; }
rnd() { openssl rand -base64 "$1" | tr -d '\n'; }
pw() { openssl rand -hex 18; }
mkdir -p .secrets && chmod 700 .secrets
[ -f .secrets/lab_id_ed25519 ] || ssh-keygen -t ed25519 -N "" -q -C backora-lab -f .secrets/lab_id_ed25519
umask 077
cat > .env <<ENV
POSTGRES_PASSWORD=$(pw)
BACKORA_OWNER_PASSWORD=$(pw)
BACKORA_APP_PASSWORD=$(pw)
BACKORA_ENCRYPTION_KEY=$(rnd 32)
BACKORA_JWT_SIGNING_KEY=$(rnd 48)
BACKORA_ADMIN_EMAIL=admin@backora.local
BACKORA_ADMIN_PASSWORD=Adm1n-$(openssl rand -hex 8)
BACKORA_PUBLIC_URL=http://localhost:8443
LAB_PUBLIC_KEY="$(cat .secrets/lab_id_ed25519.pub)"
LAB_S3_ACCESS_KEY=lab$(openssl rand -hex 6)
LAB_S3_SECRET_KEY=$(pw)
ENV
echo "Wrote deploy/.env (admin password inside; change it at first sign-in). Lab SSH key: deploy/.secrets/lab_id_ed25519"
