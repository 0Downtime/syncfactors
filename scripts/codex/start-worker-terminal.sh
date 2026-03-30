#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "${script_dir}/open-terminal-command.sh" "SyncFactors worker" "pwsh" "./scripts/codex/run.ps1" "-Service" "worker"
