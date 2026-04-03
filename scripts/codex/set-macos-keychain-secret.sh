#!/usr/bin/env bash
set -euo pipefail

show_usage() {
  cat <<'EOF'
Usage:
  ./scripts/codex/set-macos-keychain-secret.sh VARIABLE_NAME

Stores a SyncFactors secret in the macOS Keychain under the service name
defined by SYNCFACTORS_KEYCHAIN_SERVICE, or "syncfactors" by default.

Example:
  ./scripts/codex/set-macos-keychain-secret.sh SF_AD_SYNC_AD_BIND_PASSWORD
EOF
}

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" || $# -ne 1 ]]; then
  show_usage
  exit $([[ $# -eq 1 ]] && echo 0 || echo 1)
fi

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "This helper only works on macOS." >&2
  exit 1
fi

name="$1"
service="${SYNCFACTORS_KEYCHAIN_SERVICE:-syncfactors}"

read -r -s -p "Value for ${name}: " secret
echo

security add-generic-password -U -s "${service}" -a "${name}" -w "${secret}" >/dev/null
echo "Stored ${name} in macOS Keychain service '${service}'."
