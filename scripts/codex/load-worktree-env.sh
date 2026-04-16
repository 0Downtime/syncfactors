#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/../.." && pwd)"
env_file="${repo_root}/.env.worktree"
pwsh_bin="$("${script_dir}/resolve-pwsh.sh")"
worktree_env_helper="${script_dir}/Invoke-WorktreeEnvHelper.ps1"

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

export SYNCFACTORS_RUN_PROFILE="${SYNCFACTORS_RUN_PROFILE:-mock}"
export SYNCFACTORS_CONFIG_PATH="${SYNCFACTORS_CONFIG_PATH:-}"
export SYNCFACTORS_MAPPING_CONFIG_PATH="${SYNCFACTORS_MAPPING_CONFIG_PATH:-./config/local.syncfactors.mapping-config.json}"
export SYNCFACTORS_SQLITE_PATH="${SYNCFACTORS_SQLITE_PATH:-state/runtime/syncfactors.db}"
export SYNCFACTORS_API_BIND_HOST="${SYNCFACTORS_API_BIND_HOST:-127.0.0.1}"
export SYNCFACTORS_API_PUBLIC_HOST="${SYNCFACTORS_API_PUBLIC_HOST:-127.0.0.1}"
export SYNCFACTORS_API_PORT="${SYNCFACTORS_API_PORT:-5087}"
export MOCK_SF_PORT="${MOCK_SF_PORT:-18080}"
export NUGET_HTTP_CACHE_PATH="${NUGET_HTTP_CACHE_PATH:-state/nuget/http-cache}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-state/nuget/packages}"
export NUGET_PLUGINS_CACHE_PATH="${NUGET_PLUGINS_CACHE_PATH:-state/nuget/plugin-cache}"
export REPO_ROOT="${repo_root}"

resolve_repo_path() {
  local path="$1"
  if [[ -z "${path}" ]]; then
    printf '\n'
    return
  fi

  if [[ "${path}" = /* ]]; then
    printf '%s\n' "${path}"
    return
  fi

  printf '%s/%s\n' "${repo_root}" "${path#./}"
}

export SYNCFACTORS_CONFIG_PATH_ABS="$(resolve_repo_path "${SYNCFACTORS_CONFIG_PATH}")"
export SYNCFACTORS_MAPPING_CONFIG_PATH_ABS="$(resolve_repo_path "${SYNCFACTORS_MAPPING_CONFIG_PATH}")"
export SYNCFACTORS_SQLITE_PATH_ABS="$(resolve_repo_path "${SYNCFACTORS_SQLITE_PATH}")"
export NUGET_HTTP_CACHE_PATH="$(resolve_repo_path "${NUGET_HTTP_CACHE_PATH}")"
export NUGET_PACKAGES="$(resolve_repo_path "${NUGET_PACKAGES}")"
export NUGET_PLUGINS_CACHE_PATH="$(resolve_repo_path "${NUGET_PLUGINS_CACHE_PATH}")"
export SYNCFACTORS_MOCK_CONFIG_PATH_ABS="${repo_root}/config/local.mock-successfactors.real-ad.sync-config.json"
export SYNCFACTORS_REAL_CONFIG_PATH_ABS="${repo_root}/config/local.real-successfactors.real-ad.sync-config.json"

mkdir -p "${NUGET_HTTP_CACHE_PATH}"
mkdir -p "${NUGET_PACKAGES}"
mkdir -p "${NUGET_PLUGINS_CACHE_PATH}"

case "${SYNCFACTORS_RUN_PROFILE}" in
  mock)
    profile_config_path="${SYNCFACTORS_MOCK_CONFIG_PATH_ABS}"
    ;;
  real)
    profile_config_path="${SYNCFACTORS_REAL_CONFIG_PATH_ABS}"
    ;;
  *)
    echo "Unsupported SYNCFACTORS_RUN_PROFILE '${SYNCFACTORS_RUN_PROFILE}'. Expected 'mock' or 'real'." >&2
    exit 1
    ;;
esac

if [[ -n "${SYNCFACTORS_CONFIG_PATH}" ]]; then
  resolved_sync_config_path="${SYNCFACTORS_CONFIG_PATH_ABS}"
else
  resolved_sync_config_path="${profile_config_path}"
fi

export SYNCFACTORS_PROFILE_CONFIG_PATH_ABS="${profile_config_path}"
export SYNCFACTORS_RESOLVED_CONFIG_PATH_ABS="${resolved_sync_config_path}"

keychain_service="$("${pwsh_bin}" -NoProfile -File "${worktree_env_helper}" -Action resolve-keychain-service-name -EnvFilePath "${env_file}")"

load_keychain_secret() {
  local name="$1"
  local current_value="${!name:-}"
  if [[ -n "${current_value}" ]]; then
    return
  fi

  if [[ "$(uname -s)" != "Darwin" ]]; then
    return
  fi

  local secret
  if secret="$(security find-generic-password -s "${keychain_service}" -a "${name}" -w 2>/dev/null)"; then
    export "${name}=${secret}"
  fi
}

while IFS= read -r secret_name; do
  [[ -z "${secret_name}" ]] && continue
  load_keychain_secret "${secret_name}"
done < <("${pwsh_bin}" -NoProfile -File "${worktree_env_helper}" -Action list-secure-store-variable-names)

cd "${repo_root}"
