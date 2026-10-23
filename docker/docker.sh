#!/usr/bin/env bash
# Gemini MCP - Docker self-hosting helper (Linux / macOS / Git Bash)
#
#   docker/docker.sh setup [--share-dir DIR] [--bind 127.0.0.1] [--port 5100] [--with-token] [--force]
#   docker/docker.sh up        # build + start (runs setup first if .env is missing), waits until healthy
#   docker/docker.sh down | restart | rebuild | logs | status
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"  # docker/ folder: compose file, .env, .env.example
ENV_FILE="$ROOT/.env"
CONTAINER="gemini-mcp"

COMMAND="${1:-up}"
[ $# -gt 0 ] && shift
SHARE_DIR="" BIND="" PORT="" WITH_TOKEN=0 FORCE=0
while [ $# -gt 0 ]; do
  case "$1" in
    --share-dir) SHARE_DIR="$2"; shift 2 ;;
    --bind) BIND="$2"; shift 2 ;;
    --port) PORT="$2"; shift 2 ;;
    --with-token) WITH_TOKEN=1; shift ;;
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
  local share bind port token=""
  share="$(cd "${SHARE_DIR:-$HOME}" && pwd)"
  # Git Bash on Windows: Claude passes C:\... paths, so store the Windows form.
  command -v cygpath >/dev/null 2>&1 && share="$(cygpath -w "$share")"
  bind="${BIND:-127.0.0.1}"
  port="${PORT:-5100}"
  if [ "$WITH_TOKEN" -eq 1 ] || ! is_loopback "$bind"; then
    token="$(od -An -N32 -tx1 /dev/urandom | tr -d ' \n')"
  fi
  sed -e "s|^HOST_SHARE_DIR=.*|HOST_SHARE_DIR=$share|" \
      -e "s|^MCP_BIND=.*|MCP_BIND=$bind|" \
      -e "s|^MCP_PORT=.*|MCP_PORT=$port|" \
      -e "s|^MCP_AUTH_TOKEN=.*|MCP_AUTH_TOKEN=$token|" \
      -e "s|^DOCKER_UID=.*|DOCKER_UID=$(id -u)|" \
      -e "s|^DOCKER_GID=.*|DOCKER_GID=$(id -g)|" \
      "$ROOT/.env.example" > "$ENV_FILE"
  chmod 600 "$ENV_FILE"
  echo "Created $ENV_FILE"
  echo "  shared folder : $share  ->  /share (outputs: $share/gemini-mcp-output)"
  echo "  listen        : $bind:$port"
  [ -n "$token" ] && echo "  auth token    : generated (MCP_AUTH_TOKEN in .env)"
  return 0
}

require_docker() { docker info --format '{{.ServerVersion}}' >/dev/null 2>&1 || { echo "Docker engine is not running." >&2; exit 1; }; }

require_api_key() {
  if [ -z "$(env_get GEMINI_API_KEY)" ] && [ -z "${GEMINI_API_KEY:-}" ]; then
    echo "GEMINI_API_KEY is not set (export it or set it in .env)." >&2; exit 1
  fi
}

wait_healthy() {
  printf "Waiting for container health"
  local state=""
  for _ in $(seq 1 60); do
    state="$(docker inspect -f '{{.State.Health.Status}}' "$CONTAINER" 2>/dev/null || true)"
    [ "$state" = "healthy" ] && { echo " -> healthy"; return 0; }
    [ "$state" = "unhealthy" ] && break
    printf "."; sleep 1
  done
  echo; docker logs --tail 50 "$CONTAINER" || true
  echo "Container did not become healthy (state: $state)." >&2; exit 1
}

show_connection() {
  local bind port host url
  bind="$(env_get MCP_BIND)"; bind="${bind:-127.0.0.1}"
  port="$(env_get MCP_PORT)"; port="${port:-5100}"
  host="$bind"; [ "$bind" = "0.0.0.0" ] && host="<this-host>"
  url="http://$host:$port/mcp"
  echo
  echo "MCP endpoint : $url"
  echo "Skill        : http://$host:$port/skill"
  echo "Register in Claude Code:"
  if [ -n "$(env_get MCP_AUTH_TOKEN)" ]; then
    echo "  claude mcp add --transport http gemini $url --header \"Authorization: Bearer <MCP_AUTH_TOKEN from .env>\""
  else
    echo "  claude mcp add --transport http gemini $url"
  fi
}

up() {
  require_docker
  [ -f "$ENV_FILE" ] || setup
  local bind; bind="$(env_get MCP_BIND)"
  if ! is_loopback "${bind:-127.0.0.1}" && [ -z "$(env_get MCP_AUTH_TOKEN)" ]; then
    echo "MCP_BIND=$bind exposes the server beyond this machine; set MCP_AUTH_TOKEN in .env first." >&2; exit 1
  fi
  require_api_key
  [ $# -gt 0 ] && compose build "$@"
  compose up -d --build
  wait_healthy
  show_connection
}

case "$COMMAND" in
  setup)   setup ;;
  up)      up ;;
  rebuild) up --no-cache --pull ;;
  # compose validates ${GEMINI_API_KEY:?} for every command; down/logs do not need a real key.
  down)    require_docker; GEMINI_API_KEY="$(env_get GEMINI_API_KEY)" ; GEMINI_API_KEY="${GEMINI_API_KEY:-unused}" compose down ;;
  restart) require_docker; require_api_key; compose restart; wait_healthy ;;
  logs)    require_docker; GEMINI_API_KEY="$(env_get GEMINI_API_KEY)" ; GEMINI_API_KEY="${GEMINI_API_KEY:-unused}" compose logs -f --tail 200 ;;
  status)  require_docker
           docker ps -a --filter "name=^${CONTAINER}$" --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}\t{{.Image}}'
           show_connection ;;
  *) echo "usage: $0 {setup|up|down|restart|rebuild|logs|status} [options]" >&2; exit 2 ;;
esac
