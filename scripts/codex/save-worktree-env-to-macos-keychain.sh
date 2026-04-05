#!/usr/bin/env bash
set -euo pipefail

show_usage() {
  cat <<'EOF'
Usage:
  ./scripts/codex/save-worktree-env-to-macos-keychain.sh [--remove-empty-values]
  ./scripts/codex/save-worktree-env-to-macos-keychain.sh --interactive VARIABLE_NAME [VARIABLE_NAME ...]
  ./scripts/codex/save-worktree-env-to-macos-keychain.sh --help

Reads .env.worktree and stores the SyncFactors key-backed values in the macOS
login Keychain under the service name defined by SYNCFACTORS_KEYCHAIN_SERVICE,
or "syncfactors" by default.

Use --remove-empty-values to delete blank entries from the Keychain instead of
storing them as empty strings.

Use --interactive to securely prompt for one or more variable values instead of
reading them from .env.worktree.
EOF
}

remove_empty_values=false
interactive_mode=false
interactive_names=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --remove-empty-values)
      remove_empty_values=true
      shift
      ;;
    --interactive)
      interactive_mode=true
      shift
      while [[ $# -gt 0 ]]; do
        case "$1" in
          --*)
            break
            ;;
          *)
            interactive_names+=("$1")
            shift
            ;;
        esac
      done
      ;;
    -h|--help)
      show_usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      show_usage >&2
      exit 1
      ;;
  esac
done

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "This helper only works on macOS." >&2
  exit 1
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/../.." && pwd)"
env_file="${repo_root}/.env.worktree"

trim_whitespace() {
  local value="$1"
  value="${value#"${value%%[![:space:]]*}"}"
  value="${value%"${value##*[![:space:]]}"}"
  printf '%s' "${value}"
}

read_env_value() {
  local name="$1"
  local line raw_name value

  if [[ ! -f "${env_file}" ]]; then
    return 1
  fi

  while IFS= read -r line || [[ -n "${line}" ]]; do
    line="$(trim_whitespace "${line}")"
    if [[ -z "${line}" || "${line}" == \#* ]]; then
      continue
    fi

    if [[ "${line}" != *=* ]]; then
      continue
    fi

    raw_name="${line%%=*}"
    raw_name="$(trim_whitespace "${raw_name}")"
    if [[ "${raw_name}" != "${name}" ]]; then
      continue
    fi

    value="${line#*=}"
    printf '%s' "${value}"
    return 0
  done < "${env_file}"

  return 1
}

validate_secret_name() {
  local name="$1"
  case "${name}" in
    SYNCFACTORS__AUTH__OIDC__CLIENTSECRET|SYNCFACTORS__AUTH__BOOTSTRAPADMIN__PASSWORD|SF_AD_SYNC_SF_USERNAME|SF_AD_SYNC_SF_PASSWORD|SF_AD_SYNC_SF_CLIENT_ID|SF_AD_SYNC_SF_CLIENT_SECRET|SF_AD_SYNC_AD_SERVER|SF_AD_SYNC_AD_USERNAME|SF_AD_SYNC_AD_BIND_PASSWORD|SF_AD_SYNC_AD_DEFAULT_PASSWORD)
      return 0
      ;;
    *)
      echo "Unsupported variable name: ${name}" >&2
      echo "Expected one of the SyncFactors key-backed secret names." >&2
      exit 1
      ;;
  esac
}

prompt_secret_value() {
  local name="$1"
  local value
  read -r -s -p "Value for ${name}: " value
  echo
  printf '%s' "${value}"
}

verify_secret_value() {
  local name="$1"
  local expected_value="$2"
  local actual_value

  if ! actual_value="$(security find-generic-password -s "${service}" -a "${name}" -w 2>/dev/null)"; then
    return 1
  fi

  [[ "${actual_value}" == "${expected_value}" ]]
}

verify_secret_removed() {
  local name="$1"
  ! security find-generic-password -s "${service}" -a "${name}" -w >/dev/null 2>&1
}

service="${SYNCFACTORS_KEYCHAIN_SERVICE:-}"
if [[ -z "${service}" ]]; then
  service="$(read_env_value "SYNCFACTORS_KEYCHAIN_SERVICE" || true)"
fi
service="${service:-syncfactors}"

secret_names=(
  SYNCFACTORS__AUTH__OIDC__CLIENTSECRET
  SYNCFACTORS__AUTH__BOOTSTRAPADMIN__PASSWORD
  SF_AD_SYNC_SF_USERNAME
  SF_AD_SYNC_SF_PASSWORD
  SF_AD_SYNC_SF_CLIENT_ID
  SF_AD_SYNC_SF_CLIENT_SECRET
  SF_AD_SYNC_AD_SERVER
  SF_AD_SYNC_AD_USERNAME
  SF_AD_SYNC_AD_BIND_PASSWORD
  SF_AD_SYNC_AD_DEFAULT_PASSWORD
)

stored_count=0
removed_count=0

if [[ "${interactive_mode}" == "true" ]]; then
  if [[ ${#interactive_names[@]} -eq 0 ]]; then
    echo "--interactive requires at least one variable name." >&2
    exit 1
  fi

  secret_names=("${interactive_names[@]}")
elif [[ ! -f "${env_file}" ]]; then
  cat >&2 <<EOF
Missing ${env_file}.
Run ./scripts/codex/setup-worktree-macos.sh first, or copy ./.env.worktree.example to ./.env.worktree.
EOF
  exit 1
fi

for name in "${secret_names[@]}"; do
  validate_secret_name "${name}"

  if [[ "${interactive_mode}" == "true" ]]; then
    value="$(prompt_secret_value "${name}")"
  else
    value="$(read_env_value "${name}" || true)"
  fi

  if [[ "${remove_empty_values}" == "true" && -z "${value}" ]]; then
    security delete-generic-password -s "${service}" -a "${name}" >/dev/null 2>&1 || true
    if ! verify_secret_removed "${name}"; then
      echo "Failed to verify removal for ${name} in macOS Keychain" >&2
      exit 1
    fi
    echo "Removed ${name} from macOS Keychain"
    removed_count=$((removed_count + 1))
    continue
  fi

  security add-generic-password -U -s "${service}" -a "${name}" -w "${value}" >/dev/null
  if ! verify_secret_value "${name}" "${value}"; then
    echo "Failed to verify stored value for ${name} in macOS Keychain" >&2
    exit 1
  fi
  echo "Stored ${name} in macOS Keychain"
  stored_count=$((stored_count + 1))
done

echo "Keychain import complete. Stored ${stored_count} value(s); removed ${removed_count} empty value(s)."
