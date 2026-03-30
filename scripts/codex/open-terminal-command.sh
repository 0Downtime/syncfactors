#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 2 ]]; then
  echo "Usage: $0 <label> <command> [args...]" >&2
  exit 1
fi

label="$1"
shift

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/../.." && pwd)"

quote_for_shell() {
  printf '%q' "$1"
}

join_command() {
  local parts=()
  local value
  for value in "$@"; do
    parts+=("$(quote_for_shell "${value}")")
  done

  local joined=""
  local part
  for part in "${parts[@]}"; do
    if [[ -n "${joined}" ]]; then
      joined+=" "
    fi
    joined+="${part}"
  done

  printf '%s' "${joined}"
}

escape_for_applescript() {
  local value="$1"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  printf '%s' "${value}"
}

repo_root_q="$(quote_for_shell "${repo_root}")"
label_q="$(quote_for_shell "${label}")"
command_q="$(join_command "$@")"

child_command="cd ${repo_root_q} && printf 'Starting %s\\n\\n' ${label_q} && ${command_q}; exit_code=\$?; printf '\\n[%s] exited with status %s.\\n' ${label_q} \"\$exit_code\"; exec \${SHELL:-/bin/zsh} -l"

if [[ -d "/Applications/Ghostty.app" ]] && command -v open >/dev/null 2>&1; then
  open -na Ghostty.app --args -e /bin/zsh -lc "${child_command}"
  exit 0
fi

if [[ -d "/System/Applications/Utilities/Terminal.app" || -d "/Applications/Utilities/Terminal.app" ]]; then
  child_command_applescript="$(escape_for_applescript "${child_command}")"
  osascript <<EOF
tell application "Terminal"
  activate
  do script "${child_command_applescript}"
end tell
EOF
  exit 0
fi

echo "Neither Ghostty.app nor Terminal.app is available on this machine." >&2
exit 1
