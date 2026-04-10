#!/usr/bin/env bash
set -euo pipefail

reuse_existing=false
if [[ "${1:-}" == "--reuse-existing" ]]; then
  reuse_existing=true
  shift
fi

if [[ $# -lt 2 ]]; then
  echo "Usage: $0 [--reuse-existing] <label> <command> [args...]" >&2
  exit 1
fi

label="$1"
shift

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/../.." && pwd)"

if [[ "${1:-}" == "pwsh" ]]; then
  shift
  set -- "$("${script_dir}/resolve-pwsh.sh")" "$@"
fi

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

repo_root_q="$(quote_for_shell "${repo_root}")"
label_q="$(quote_for_shell "${label}")"
command_q="$(join_command "$@")"

child_command="cd ${repo_root_q} && printf '\033]0;%s\007' ${label_q} && printf 'Starting %s\\n\\n' ${label_q} && ${command_q}; exit_code=\$?; printf '\\n[%s] exited with status %s.\\n' ${label_q} \"\$exit_code\"; exec \${SHELL:-/bin/zsh} -l"

if [[ -d "/System/Applications/Utilities/Terminal.app" || -d "/Applications/Utilities/Terminal.app" ]]; then
  osascript - "${label}" "${child_command}" "${reuse_existing}" <<'EOF'
on run argv
  set targetLabel to item 1 of argv
  set shellCommand to item 2 of argv
  set reuseExisting to item 3 of argv

  set targetTab to missing value
  set targetWindow to missing value

  tell application "Terminal"
    activate

    if reuseExisting is "true" then
      repeat with currentWindow in windows
        repeat with currentTab in tabs of currentWindow
          set tabTitle to ""
          set tabName to ""

          try
            set tabTitle to custom title of currentTab
          end try

          try
            set tabName to name of currentTab
          end try

          if tabTitle is targetLabel or tabName is targetLabel then
            set targetWindow to currentWindow
            set targetTab to currentTab
            exit repeat
          end if
        end repeat

        if targetTab is not missing value then
          exit repeat
        end if
      end repeat
    end if

    if targetTab is not missing value then
      do script shellCommand in targetTab
      set selected of targetTab to true
    else
      do script shellCommand
      delay 0.2
      set targetWindow to front window
      set targetTab to selected tab of targetWindow
    end if

    try
      set custom title of targetTab to targetLabel
    end try
  end tell
end run
EOF
  exit 0
fi

if [[ -d "/Applications/Ghostty.app" ]] && command -v open >/dev/null 2>&1; then
  open -na Ghostty.app --args -e /bin/zsh -lc "${child_command}"
  exit 0
fi

echo "Neither Ghostty.app nor Terminal.app is available on this machine." >&2
exit 1
