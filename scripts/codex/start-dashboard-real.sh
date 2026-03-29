#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${script_dir}/load-worktree-env.sh"

real_config="./config/local.real-successfactors.real-ad.sync-config.json"

if [[ ! -f "${real_config}" ]]; then
  echo "Missing ${real_config}. Run ./scripts/codex/setup-worktree-macos.sh first." >&2
  exit 1
fi

exec npm run web:dev -- --config "${real_config}"
