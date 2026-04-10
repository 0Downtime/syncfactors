#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
pwsh_bin="$("${script_dir}/resolve-pwsh.sh")"

show_usage() {
  cat <<'EOF'
Usage:
  ./scripts/codex/run.sh -Service <api|ui|worker|mock|stack> [-Profile <mock|real>] [-Restart] [-SkipBuild]
  ./scripts/codex/run.sh --help

This is the shell wrapper for ./scripts/codex/run.ps1.
Use it when you want the same launcher behavior from bash/zsh.
EOF
}

if [[ $# -eq 0 ]]; then
  show_usage
  exit 1
fi

case "${1}" in
  -h|--help|help)
    show_usage
    printf '\n'
    exec "${pwsh_bin}" "${script_dir}/run.ps1" -Help
    ;;
esac

exec "${pwsh_bin}" "${script_dir}/run.ps1" "$@"
