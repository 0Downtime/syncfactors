#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
config_path="${1:-./reports/demo-rich/config/demo.mock-sync-config.json}"
port="${PORT:-4280}"
pattern="tsx web/server/index.ts --config ${config_path}"
flush_cache="${FLUSH_CACHE:-1}"

cd "$repo_root"

if [[ "$flush_cache" == "1" ]]; then
  echo "Removing web client cache and build artifacts"
  rm -rf \
    "$repo_root/web/dist" \
    "$repo_root/web/client/node_modules/.vite" \
    "$repo_root/node_modules/.vite"
fi

existing_pids="$(pgrep -f "$pattern" || true)"
if [[ -n "$existing_pids" ]]; then
  echo "Stopping existing demo web server: $existing_pids"
  while IFS= read -r pid; do
    [[ -n "$pid" ]] || continue
    kill "$pid"
  done <<< "$existing_pids"
  sleep 1
fi

echo "Starting demo web server on http://127.0.0.1:${port}"
exec npm run web:dev -- --config "$config_path"
