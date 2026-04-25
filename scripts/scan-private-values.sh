#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${repo_root}"

private_org="$(printf '\x73\x70\x69\x72\x65')"

patterns=(
  "(^|[^[:alnum:]])${private_org}([^[:alnum:]]|$)"
  "${private_org}qa"
  "${private_org}[[:space:]_-]*energy"
  'qa''dc'
  'api''4[.]successfactors[.]com'
  '10[.]1[.]182[.][0-9]{1,3}'
  '10[.]0[.]20[.][0-9]{1,3}'
  '62a''742e6-2508-4ed7-8b84-1ba181''d5194f'
  'af7''19b5d-e2e7-47f6-9d35-aadc3b''b62fd0'
  '633''bc466-bbff-40b8-a315-9833a''265fd8d'
  'adb''441fe-c3c6-42ad-9b61-c227d''cbde914'
  'd43''e8285-6c8b-4a51-8c7a-4e2d22''eed351'
  'fat''cats44'
)

pattern="$(IFS='|'; echo "${patterns[*]}")"
tmp_file="$(mktemp)"
trap 'rm -f "${tmp_file}"' EXIT

git ls-files -z \
  ':!:src/SyncFactors.Api/node_modules/**' \
  ':!:**/bin/**' \
  ':!:**/obj/**' \
  ':!:state/**' \
  ':!:logs/**' \
  ':!:reports/**' \
  > "${tmp_file}"

if [[ ! -s "${tmp_file}" ]]; then
  exit 0
fi

if xargs -0 grep -IEni "${pattern}" < "${tmp_file}"; then
  cat >&2 <<'EOF'

Private value scan failed.
Replace organization-specific domains, OUs, hostnames, IPs, tenant IDs, group IDs, and secrets with examples before committing.
EOF
  exit 1
fi
