#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "${script_dir}/open-terminal-command.sh" "SyncFactors .NET API" "./scripts/codex/start-next-api.sh"
