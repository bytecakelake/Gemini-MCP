#!/usr/bin/env bash
# Gemini MCP - Docker self-hosting helper (Linux / macOS / Git Bash)
#
#   docker/docker.sh setup [--share-dir DIR] [--bind 127.0.0.1] [--port 5100] [--domain "localhost, 127.0.0.1"] [--force]
#   docker/docker.sh up          # build + start (runs setup first if .env is missing), waits until healthy
#   docker/docker.sh down | restart | rebuild | logs | status
#   docker/docker.sh cert        # export the Caddy local CA root certificate (MCP_TLS=internal) to docker/caddy-root.crt
#   docker/docker.sh reset-admin # forgot the dashboard password: delete the admin account; a new setup code is logged
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"  # docker/ folder: compose file, .env, .env.example
ENV_FILE="$ROOT/.env"
HEALTH_CHECKED=(gemini-mcp gemini-mcp-dashboard gemini-mcp-gateway)
GATEWAY="gemini-mcp-gateway"
PROXY="gemini-mcp-caddy"
CA_FILE="$ROOT/caddy-root.crt"

COMMAND="${1:-up}"
[ $# -gt 0 ] && shift
SHARE_DIR="" BIND="" PORT="" DOMAIN="" FORCE=0
while [ $# -gt 0 ]; do
  case "$1" in
    --share-dir) SHARE_DIR="$2"; shift 2 ;;
    --bind) BIND="$2"; shift 2 ;;
    --port) PORT="$2"; shift 2 ;;
    --domain) DOMAIN="$2"; shift 2 ;;
    --force) FORCE=1; shift ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

# Same precedence as docker compose: shell environment first, then .env.
env_get() {
  local value="${!1:-}"
  if [ -z "$value" ] && [ -f "$ENV_FILE" ]; then
    value="$(sed -n "s/^$1=//p" "$ENV_FILE" | tail -1 | tr -d '\r"')"
  fi
  printf '%s' "$value"
}
is_loopback() { case "$1" in 127.0.0.1|localhost|::1|"[::1]") return 0 ;; *) return 1 ;; esac; }
compose() { (cd "$ROOT" && docker compose "$@"); }

setup() {
  if [ -f "$ENV_FILE" ] && [ "$FORCE" -eq 0 ]; then
    echo ".env already exists (use --force to regenerate): $ENV_FILE"; return
  fi
  local share bind port domain
  share="$(cd "${SHARE_DIR:-$HOME}" && pwd)"
  # Git Bash on Windows: Claude passes C:\... paths, so store the Windows form.
  command -v cygpath >/dev/null 2>&1 && share="$(cygpath -w "$share")"
  bind="${BIND:-127.0.0.1}"
  port="${PORT:-5100}"
  domain="${DOMAIN:-localhost, 127.0.0.1}"
  sed -e "s|^HOST_SHARE_DIR=.*|HOST_SHARE_DIR=$share|" \
      -e "s|^MCP_BIND=.*|MCP_BIND=$bind|" \
      -e "s|^MCP_PORT=.*|MCP_PORT=$port|" \
      -e "s|^MCP_DOMAIN=.*|MCP_DOMAIN=$domain|" \
      -e "s|^DOCKER_UID=.*|DOCKER_UID=$(id -u)|" \
      -e "s|^DOCKER_GID=.*|DOCKER_GID=$(id -g)|" \
      "$ROOT/.env.example" > "$ENV_FILE"
  chmod 600 "$ENV_FILE"
  echo "Created $ENV_FILE"
  echo "  shared folder : $share  ->  /share (outputs: $share/gemini-mcp-output)"
  echo "  listen        : https://$bind:$port  (TLS names: $domain)"
  if ! is_loopback "$bind" && [ -z "$DOMAIN" ]; then
    echo "  note          : add this host's name/IP to MCP_DOMAIN in .env (or pass --domain) so remote clients get a matching certificate"
  fi
  return 0
}

require_docker() { docker info --format '{{.ServerVersion}}' >/dev/null 2>&1 || { echo "Docker engine is not running." >&2; exit 1; }; }

require_api_key() {
  if [ -z "$(env_get GEMINI_API_KEY)" ] && [ -z "${GEMINI_API_KEY:-}" ]; then
    echo "GEMINI_API_KEY is not set (export it or set it in .env)." >&2; exit 1
  fi
}

wait_healthy() {
  local name state
  for name in "${HEALTH_CHECKED[@]}"; do
    printf "Waiting for %s" "$name"
    state=""
    for _ in $(seq 1 90); do
      state="$(docker inspect -f '{{.State.Health.Status}}' "$name" 2>/dev/null || true)"
      [ "$state" = "healthy" ] || [ "$state" = "unhealthy" ] && break
      printf "."; sleep 1
    done
    if [ "$state" != "healthy" ]; then
      echo; docker logs --tail 50 "$name" || true
      echo "$name did not become healthy (state: $state)." >&2; exit 1
    fi
    echo " -> healthy"
  done
}

wait_proxy() {
  local state=""
  for _ in $(seq 1 30); do
    state="$(docker inspect -f '{{.State.Status}}' "$PROXY" 2>/dev/null || true)"
    [ "$state" = "running" ] && return 0
    sleep 1
  done
  docker logs --tail 50 "$PROXY" || true
  echo "Caddy proxy is not running (state: $state)." >&2; exit 1
}

# MCP_TLS=internal: clients must trust Caddy's local CA. It lives in the caddy-data volume, so it survives restarts.
cert() {
  require_docker
  docker cp "$PROXY:/data/caddy/pki/authorities/local/root.crt" "$CA_FILE" >/dev/null
  echo "Exported Caddy root CA: $CA_FILE"
  echo "Trust it for Claude Code (Node.js) : export NODE_EXTRA_CA_CERTS=\"$CA_FILE\""
  echo "Trust it in the Windows user store : certutil -user -addstore Root \"$CA_FILE\""
}

# The gateway prints a one-time setup code while no dashboard admin exists (no shell in the image, so ask it with deno eval).
show_setup_code() {
  local state code
  state="$(docker exec "$GATEWAY" deno eval "const r = await fetch('http://127.0.0.1:8080/api/admin/auth/state'); console.log((await r.json()).setupRequired)" 2>/dev/null || true)"
  [ "$state" = "true" ] || return 0
  code="$(docker logs "$GATEWAY" 2>&1 | sed -n 's/.*Dashboard setup code: \([A-Z0-9-]*\).*/\1/p' | tail -1)"
  [ -n "$code" ] && echo "Setup code   : $code  (create the dashboard admin password with it)"
  return 0
}

show_connection() {
  local bind port host base
  bind="$(env_get MCP_BIND)"; bind="${bind:-127.0.0.1}"
  port="$(env_get MCP_PORT)"; port="${port:-5100}"
  host="$bind"; [ "$bind" = "0.0.0.0" ] && host="<this-host>"
  base="https://$host:$port"
  echo
  echo "Dashboard    : $base/   (logs, monitoring, API keys)"
  show_setup_code
  echo "MCP endpoint : $base/mcp   (Authorization: Bearer <API key issued in the dashboard>)"
  echo "Skill        : $base/skill"
  local tls; tls="$(env_get MCP_TLS)"
  [ "${tls:-internal}" = "internal" ] && echo "TLS          : Caddy local CA (run '$0 cert' to export the root certificate for clients)"
  echo "Register in Claude Code:"
  echo "  claude mcp add --transport http gemini $base/mcp --header \"Authorization: Bearer <API key>\""
}

up() {
  require_docker
  [ -f "$ENV_FILE" ] || setup
  require_api_key
  [ $# -gt 0 ] && compose build "$@"
  compose up -d --build
  wait_healthy
  wait_proxy
  show_connection
}

reset_admin() {
  require_docker
  docker exec "$GATEWAY" deno run --allow-env --allow-read=/data --allow-write=/data main.ts --reset-admin
  show_setup_code
}

case "$COMMAND" in
  setup)       setup ;;
  up)          up ;;
  rebuild)     up --no-cache --pull ;;
  # compose validates ${GEMINI_API_KEY:?} for every command; down/logs do not need a real key.
  down)        require_docker; GEMINI_API_KEY="$(env_get GEMINI_API_KEY)" ; GEMINI_API_KEY="${GEMINI_API_KEY:-unused}" compose down ;;
  restart)     require_docker; require_api_key; compose restart; wait_healthy; wait_proxy; show_connection ;;
  cert)        cert ;;
  logs)        require_docker; GEMINI_API_KEY="$(env_get GEMINI_API_KEY)" ; GEMINI_API_KEY="${GEMINI_API_KEY:-unused}" compose logs -f --tail 200 ;;
  reset-admin) reset_admin ;;
  status)      require_docker
               docker ps -a --filter "name=^gemini-mcp" --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}\t{{.Image}}'
               show_connection ;;
  *) echo "usage: $0 {setup|up|down|restart|rebuild|logs|status|cert|reset-admin} [options]" >&2; exit 2 ;;
esac
