#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
pwsh_bin="${PWSH_BIN:-pwsh}"

exec "${pwsh_bin}" "${script_dir}/Validate-SuccessFactorsContract.ps1" "$@"
