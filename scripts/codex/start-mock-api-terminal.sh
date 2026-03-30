#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "${script_dir}/open-terminal-command.sh" "SyncFactors mock API" "./scripts/codex/run.sh" --service mock
