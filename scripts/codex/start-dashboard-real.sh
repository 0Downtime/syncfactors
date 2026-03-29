#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${script_dir}/load-worktree-env.sh"

if [[ ! -f "${SYNCFACTORS_REAL_CONFIG_PATH_ABS}" ]]; then
  echo "Missing ${SYNCFACTORS_REAL_CONFIG_PATH_ABS}. Run ./scripts/codex/setup-worktree-macos.sh first." >&2
  exit 1
fi

cd "${REPO_ROOT}/SyncFactors.Old"
exec npm run web:dev -- --config "${SYNCFACTORS_REAL_CONFIG_PATH_ABS}"
