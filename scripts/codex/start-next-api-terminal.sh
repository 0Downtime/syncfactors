#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "${script_dir}/open-terminal-command.sh" "SyncFactors .NET API" "pwsh" "./scripts/codex/run.ps1" "-Service" "api"
