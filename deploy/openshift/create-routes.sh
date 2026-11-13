#!/usr/bin/env bash
# Backora V2 on OKD (project "backora"): one public host, split by path.
#
#   https://backora.example.com/      -> service backora-frontend  (Blazor Server UI)
#   https://backora.example.com/api   -> service backora-api       (REST API; its routes already start with /api,
#                                                                    so the path is forwarded unchanged)
#
# The OpenShift router sends /api/... to the API route (longest path wins) and everything else to the frontend.
# TLS is terminated at the router (edge). backora.example.com is outside the cluster's *.apps wildcard, so pass the
# certificate for it (full chain + key); without them the router serves its default certificate and browsers warn.
#
# Usage (logged in with `oc login`, read the script first):
#   ./create-routes.sh                                # routes only, router's default certificate
#   ./create-routes.sh tls.crt tls.key [ca.crt]       # with the backora.example.com certificate
#
# The existing backora.apps.cluster.example.com route is left alone; remove it once the new host works.
set -euo pipefail

HOST="${BACKORA_HOST:-backora.example.com}"
PROJECT="${BACKORA_PROJECT:-backora}"
CERT="${1:-}"; KEY="${2:-}"; CA="${3:-}"

tls=()
if [[ -n "$CERT" || -n "$KEY" ]]; then
  [[ -f "$CERT" && -f "$KEY" ]] || { echo "certificate or key file not found" >&2; exit 1; }
  tls=(--cert="$CERT" --key="$KEY")
  [[ -n "$CA" ]] && tls+=(--ca-cert="$CA")
fi

oc project "$PROJECT" >/dev/null

# Port names differ between `oc new-app` (8080-tcp) and hand-written services (http); use whatever the service has.
port_of() { oc get svc "$1" -o jsonpath='{.spec.ports[0].name}'; }
FRONTEND_PORT="$(port_of backora-frontend)"
API_PORT="$(port_of backora-api)"

oc create route edge backora \
  --service=backora-frontend --port="${FRONTEND_PORT:-8080}" \
  --hostname="$HOST" --path=/ --insecure-policy=Redirect ${tls[@]+"${tls[@]}"}
# Blazor Server keeps one long-lived WebSocket per tab: pin a browser to one pod and let the socket stay open.
oc annotate route backora --overwrite \
  haproxy.router.openshift.io/balance=source \
  haproxy.router.openshift.io/timeout=1h

oc create route edge backora-api \
  --service=backora-api --port="${API_PORT:-8080}" \
  --hostname="$HOST" --path=/api --insecure-policy=Redirect ${tls[@]+"${tls[@]}"}

oc get routes backora backora-api
echo "Done. Check: curl -sI https://$HOST/ (the UI) and curl -si https://$HOST/api/v1/system/info (401 without a token means the API answered)"
