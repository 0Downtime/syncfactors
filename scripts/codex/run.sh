#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

show_usage() {
  cat <<'EOF'
Usage:
  ./scripts/codex/run.sh -Service <api|worker|mock|stack> [-Profile <mock|real>] [-Restart] [-SkipBuild]
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
    exec pwsh "${script_dir}/run.ps1" -Help
    ;;
esac

exec pwsh "${script_dir}/run.ps1" "$@"
