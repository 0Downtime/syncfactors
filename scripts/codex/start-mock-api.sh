#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${script_dir}/load-worktree-env.sh"

exec pwsh ./SyncFactors.Old/scripts/Start-MockSuccessFactorsApi.ps1 -Port "${MOCK_SF_PORT}"
