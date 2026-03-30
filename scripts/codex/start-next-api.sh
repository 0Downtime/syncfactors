#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${script_dir}/load-worktree-env.sh"

exec pwsh ./scripts/Start-SyncFactorsNextApi.ps1 \
  -ConfigPath "${SYNCFACTORS_CONFIG_PATH_ABS}" \
  -MappingConfigPath "${SYNCFACTORS_MAPPING_CONFIG_PATH_ABS}" \
  -Urls "http://127.0.0.1:${SYNCFACTORS_API_PORT}"
