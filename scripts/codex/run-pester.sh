#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${script_dir}/load-worktree-env.sh"

exec env \
  -u SF_AD_SYNC_SF_USERNAME \
  -u SF_AD_SYNC_SF_PASSWORD \
  -u SF_AD_SYNC_SF_CLIENT_ID \
  -u SF_AD_SYNC_SF_CLIENT_SECRET \
  -u SF_AD_SYNC_AD_SERVER \
  -u SF_AD_SYNC_AD_USERNAME \
  -u SF_AD_SYNC_AD_BIND_PASSWORD \
  -u SF_AD_SYNC_AD_DEFAULT_PASSWORD \
  pwsh ./SyncFactors.Old/scripts/Invoke-TestSuite.ps1 -Detailed
