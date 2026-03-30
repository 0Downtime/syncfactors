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

require_command "dotnet" "Install the .NET 10 SDK before creating a Codex worktree for this repo."
require_command "pwsh" "Install PowerShell 7 before creating a Codex worktree for this repo."

cd "${repo_root}"

echo "Preparing SyncFactors.Next worktree defaults (mock SF + real AD)"

mkdir -p \
  "${repo_root}/state/runtime" \
  "${repo_root}/reports/output" \
  "${repo_root}/reports/mock-output"

copy_if_missing "${repo_root}/config/sample.mock-successfactors.real-ad.sync-config.json" "${repo_root}/config/local.mock-successfactors.real-ad.sync-config.json"
copy_if_missing "${repo_root}/config/sample.real-successfactors.real-ad.sync-config.json" "${repo_root}/config/local.real-successfactors.real-ad.sync-config.json"
copy_if_missing "${repo_root}/config/sample.empjob-confirmed.mapping-config.json" "${repo_root}/config/local.syncfactors.mapping-config.json"
copy_if_missing "${repo_root}/.env.worktree.example" "${repo_root}/.env.worktree"

echo "Worktree bootstrap complete. Start Mock SF, the .NET API, and the worker from Codex actions or scripts/codex."
