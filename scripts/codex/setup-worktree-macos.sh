#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/../.." && pwd)"

require_command() {
  local name="$1"
  local install_hint="$2"
  if ! command -v "${name}" >/dev/null 2>&1; then
    echo "Missing required command '${name}'. ${install_hint}" >&2
    exit 1
  fi
}

copy_if_missing() {
  local source_path="$1"
  local destination_path="$2"
  if [[ ! -e "${destination_path}" ]]; then
    cp "${source_path}" "${destination_path}"
    echo "Created ${destination_path#${repo_root}/}"
  fi
}

require_command "node" "Install Node.js before creating a Codex worktree for this repo."
require_command "npm" "Install npm before creating a Codex worktree for this repo."
require_command "pwsh" "Install PowerShell 7 before creating a Codex worktree for this repo."

cd "${repo_root}"

echo "Installing legacy web dependencies"
mkdir -p "${repo_root}/.npm-cache"
(
  cd "${repo_root}/SyncFactors.Old"
  if [[ -f "package-lock.json" || -f "npm-shrinkwrap.json" ]]; then
    npm ci --cache "${repo_root}/.npm-cache"
  else
    npm install --cache "${repo_root}/.npm-cache"
  fi
)

mkdir -p \
  "${repo_root}/state/runtime" \
  "${repo_root}/reports/output" \
  "${repo_root}/reports/mock-output"

copy_if_missing "${repo_root}/config/sample.mock-successfactors.real-ad.sync-config.json" "${repo_root}/config/local.mock-successfactors.real-ad.sync-config.json"
copy_if_missing "${repo_root}/config/sample.real-successfactors.real-ad.sync-config.json" "${repo_root}/config/local.real-successfactors.real-ad.sync-config.json"
copy_if_missing "${repo_root}/config/sample.syncfactors.mapping-config.json" "${repo_root}/config/local.syncfactors.mapping-config.json"
copy_if_missing "${repo_root}/.env.worktree.example" "${repo_root}/.env.worktree"

echo "Worktree bootstrap complete"
