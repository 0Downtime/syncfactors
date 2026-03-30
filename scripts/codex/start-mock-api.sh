#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${script_dir}/load-worktree-env.sh"

exec pwsh ./scripts/Start-SyncFactorsMockSuccessFactors.ps1 -Urls "http://127.0.0.1:${MOCK_SF_PORT}"
