#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
pwsh_bin="${PWSH:-pwsh}"
exec "${pwsh_bin}" "${script_dir}/Run-SyncFactorsProductionReadiness.ps1" "$@"
