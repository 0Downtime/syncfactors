#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "${script_dir}/load-worktree-env.sh"

service=""
profile_override=""
skip_build=0

usage() {
  cat >&2 <<'EOF'
Usage: ./scripts/codex/run.sh --service api|worker|mock|stack [--profile mock|real] [--skip-build]
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --service)
      if [[ $# -lt 2 ]]; then
        usage
        exit 1
      fi
      service="$2"
      shift 2
      ;;
    --profile)
      if [[ $# -lt 2 ]]; then
        usage
        exit 1
      fi
      profile_override="$2"
      shift 2
      ;;
    --skip-build)
      skip_build=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage
      exit 1
      ;;
  esac
done

if [[ -z "${service}" ]]; then
  usage
  exit 1
fi

if [[ -n "${profile_override}" ]]; then
  export SYNCFACTORS_RUN_PROFILE="${profile_override}"
  if [[ -n "${SYNCFACTORS_CONFIG_PATH}" ]]; then
    export SYNCFACTORS_RESOLVED_CONFIG_PATH_ABS="${SYNCFACTORS_CONFIG_PATH_ABS}"
  else
    case "${SYNCFACTORS_RUN_PROFILE}" in
      mock)
        export SYNCFACTORS_PROFILE_CONFIG_PATH_ABS="${SYNCFACTORS_MOCK_CONFIG_PATH_ABS}"
        export SYNCFACTORS_RESOLVED_CONFIG_PATH_ABS="${SYNCFACTORS_MOCK_CONFIG_PATH_ABS}"
        ;;
      real)
        export SYNCFACTORS_PROFILE_CONFIG_PATH_ABS="${SYNCFACTORS_REAL_CONFIG_PATH_ABS}"
        export SYNCFACTORS_RESOLVED_CONFIG_PATH_ABS="${SYNCFACTORS_REAL_CONFIG_PATH_ABS}"
        ;;
      *)
        echo "Unsupported profile override '${SYNCFACTORS_RUN_PROFILE}'. Expected 'mock' or 'real'." >&2
        exit 1
        ;;
    esac
  fi
fi

skip_build_args=()
if [[ ${skip_build} -eq 1 ]]; then
  skip_build_args=( -SkipBuild )
fi
profile_args=()
if [[ -n "${profile_override}" ]]; then
  profile_args=( --profile "${profile_override}" )
fi
stack_args=()
if [[ ${skip_build} -eq 1 ]]; then
  stack_args+=( --skip-build )
fi
if [[ ${#profile_args[@]} -gt 0 ]]; then
  stack_args+=( "${profile_args[@]}" )
fi

case "${service}" in
  api)
    exec pwsh ./scripts/Start-SyncFactorsNextApi.ps1 \
      -ConfigPath "${SYNCFACTORS_RESOLVED_CONFIG_PATH_ABS}" \
      -MappingConfigPath "${SYNCFACTORS_MAPPING_CONFIG_PATH_ABS}" \
      -SqlitePath "${SYNCFACTORS_SQLITE_PATH_ABS}" \
      -Urls "http://127.0.0.1:${SYNCFACTORS_API_PORT}" \
      "${skip_build_args[@]}"
    ;;
  worker)
    exec pwsh ./scripts/Start-SyncFactorsWorker.ps1 \
      -ConfigPath "${SYNCFACTORS_RESOLVED_CONFIG_PATH_ABS}" \
      -MappingConfigPath "${SYNCFACTORS_MAPPING_CONFIG_PATH_ABS}" \
      -SqlitePath "${SYNCFACTORS_SQLITE_PATH_ABS}" \
      "${skip_build_args[@]}"
    ;;
  mock)
    exec pwsh ./scripts/Start-SyncFactorsMockSuccessFactors.ps1 \
      -Urls "http://127.0.0.1:${MOCK_SF_PORT}" \
      "${skip_build_args[@]}"
    ;;
  stack)
    "${script_dir}/open-terminal-command.sh" "SyncFactors mock API" "./scripts/codex/run.sh" --service mock "${stack_args[@]}"
    "${script_dir}/open-terminal-command.sh" "SyncFactors .NET API" "./scripts/codex/run.sh" --service api "${stack_args[@]}"
    "${script_dir}/open-terminal-command.sh" "SyncFactors worker" "./scripts/codex/run.sh" --service worker "${stack_args[@]}"
    ;;
  *)
    echo "Unsupported service '${service}'. Expected api, worker, mock, or stack." >&2
    exit 1
    ;;
esac
