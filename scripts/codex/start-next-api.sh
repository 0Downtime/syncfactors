#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
pwsh_bin="$("${script_dir}/resolve-pwsh.sh")"
exec "${pwsh_bin}" "${script_dir}/run.ps1" -Service api "$@"
