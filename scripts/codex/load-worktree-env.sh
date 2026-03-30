#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/../.." && pwd)"
env_file="${repo_root}/.env.worktree"

if [[ ! -f "${env_file}" ]]; then
  cat >&2 <<EOF
Missing ${env_file}.
Run ./scripts/codex/setup-worktree-macos.sh first, or copy ./.env.worktree.example to ./.env.worktree.
EOF
  exit 1
fi

set -a
# shellcheck disable=SC1090
source "${env_file}"
set +a

export SYNCFACTORS_CONFIG_PATH="${SYNCFACTORS_CONFIG_PATH:-./config/local.mock-successfactors.real-ad.sync-config.json}"
export SYNCFACTORS_MAPPING_CONFIG_PATH="${SYNCFACTORS_MAPPING_CONFIG_PATH:-./config/local.syncfactors.mapping-config.json}"
export PORT="${PORT:-4280}"
export MOCK_SF_PORT="${MOCK_SF_PORT:-18080}"
export REPO_ROOT="${repo_root}"

resolve_repo_path() {
  local path="$1"
  if [[ "${path}" = /* ]]; then
    printf '%s\n' "${path}"
    return
  fi

  printf '%s/%s\n' "${repo_root}" "${path#./}"
}

export SYNCFACTORS_CONFIG_PATH_ABS="$(resolve_repo_path "${SYNCFACTORS_CONFIG_PATH}")"
export SYNCFACTORS_MAPPING_CONFIG_PATH_ABS="$(resolve_repo_path "${SYNCFACTORS_MAPPING_CONFIG_PATH}")"
export SYNCFACTORS_REAL_CONFIG_PATH_ABS="${repo_root}/config/local.real-successfactors.real-ad.sync-config.json"

cd "${repo_root}"
