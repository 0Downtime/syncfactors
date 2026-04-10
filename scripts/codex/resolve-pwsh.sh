#!/usr/bin/env bash
set -euo pipefail

if command -v pwsh >/dev/null 2>&1; then
  command -v pwsh
  exit 0
fi

for candidate in \
  "/opt/homebrew/bin/pwsh" \
  "/usr/local/bin/pwsh" \
  "/opt/homebrew/opt/powershell/bin/pwsh" \
  "/usr/local/opt/powershell/bin/pwsh" \
  "${HOME}/.dotnet/tools/pwsh"
do
  if [[ -x "${candidate}" ]]; then
    printf '%s\n' "${candidate}"
    exit 0
  fi
done

echo "PowerShell executable 'pwsh' was not found. Install PowerShell 7 or ensure it is on PATH." >&2
exit 127
