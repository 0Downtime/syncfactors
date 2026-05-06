#!/usr/bin/env bash
set -euo pipefail

dotnet_install_dir="/opt/dotnet"
verify_only="false"
write_system_env="true"
sonarqube_url=""
agent_service_name=""

usage() {
  cat <<'USAGE'
Usage: install-azure-devops-sonarqube-agent-deps.sh [options]

Verifies and installs Linux dependencies for the Azure DevOps SonarQube pipeline:
  - Git, curl, CA certificates, unzip, jq
  - PowerShell 7
  - OpenJDK 17
  - .NET SDK 10.0.x
  - JAVA_HOME_17_X64, DOTNET_ROOT, and PATH environment exports

Options:
  --verify-only                 Check dependencies without installing.
  --sonarqube-url URL           Verify network reachability to SonarQube.
  --dotnet-install-dir PATH     Install .NET SDK under PATH. Default: /opt/dotnet.
  --agent-service NAME          Add a systemd override for the Azure DevOps agent service.
  --no-system-env               Do not write /etc/profile.d environment exports.
  -h, --help                    Show this help.

Examples:
  sudo ./scripts/install-azure-devops-sonarqube-agent-deps.sh
  sudo ./scripts/install-azure-devops-sonarqube-agent-deps.sh --sonarqube-url https://sonarqube.example.com
  sudo ./scripts/install-azure-devops-sonarqube-agent-deps.sh --agent-service vsts.agent.org.pool.agent.service
USAGE
}

log() {
  printf '[ado-sonar-deps] %s\n' "$*"
}

warn() {
  printf '[ado-sonar-deps] warning: %s\n' "$*" >&2
}

fail() {
  printf '[ado-sonar-deps] error: %s\n' "$*" >&2
  exit 1
}

curl_https() {
  curl --fail --silent --show-error --location --proto '=https' --tlsv1.2 "$@"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --verify-only)
      verify_only="true"
      shift
      ;;
    --sonarqube-url)
      sonarqube_url="${2:-}"
      [[ -n "$sonarqube_url" ]] || fail "--sonarqube-url requires a value."
      shift 2
      ;;
    --dotnet-install-dir)
      dotnet_install_dir="${2:-}"
      [[ -n "$dotnet_install_dir" ]] || fail "--dotnet-install-dir requires a value."
      shift 2
      ;;
    --agent-service)
      agent_service_name="${2:-}"
      [[ -n "$agent_service_name" ]] || fail "--agent-service requires a value."
      shift 2
      ;;
    --no-system-env)
      write_system_env="false"
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      fail "Unknown argument: $1"
      ;;
  esac
done

[[ "$(uname -s)" == "Linux" ]] || fail "This script only supports Linux."

if [[ "$(id -u)" -eq 0 ]]; then
  sudo_cmd=()
elif command -v sudo >/dev/null 2>&1; then
  sudo_cmd=(sudo)
else
  fail "Run as root or install sudo."
fi

version_ge() {
  local current="$1"
  local required="$2"
  [[ "$(printf '%s\n%s\n' "$required" "$current" | sort -V | head -n 1)" == "$required" ]]
}

detect_package_manager() {
  if command -v apt-get >/dev/null 2>&1; then
    echo apt
  elif command -v dnf >/dev/null 2>&1; then
    echo dnf
  elif command -v yum >/dev/null 2>&1; then
    echo yum
  elif command -v zypper >/dev/null 2>&1; then
    echo zypper
  else
    echo unknown
  fi
}

install_os_packages() {
  local package_manager
  package_manager="$(detect_package_manager)"

  if [[ "$verify_only" == "true" ]]; then
    log "verify-only: skipping OS package install."
    return
  fi

  case "$package_manager" in
    apt)
      log "installing packages with apt."
      "${sudo_cmd[@]}" apt-get update
      "${sudo_cmd[@]}" env DEBIAN_FRONTEND=noninteractive apt-get install -y \
        ca-certificates curl git jq unzip openjdk-17-jdk
      ;;
    dnf)
      log "installing packages with dnf."
      "${sudo_cmd[@]}" dnf install -y \
        ca-certificates curl git jq unzip java-17-openjdk-devel
      ;;
    yum)
      log "installing packages with yum."
      "${sudo_cmd[@]}" yum install -y \
        ca-certificates curl git jq unzip java-17-openjdk-devel
      ;;
    zypper)
      log "installing packages with zypper."
      "${sudo_cmd[@]}" zypper --non-interactive install \
        ca-certificates curl git jq unzip java-17-openjdk-devel
      ;;
    *)
      fail "Unsupported package manager. Install Git, curl, CA certs, unzip, jq, and OpenJDK 17 manually."
      ;;
  esac
}

find_java_home_17() {
  local candidates=()

  if [[ -n "${JAVA_HOME_17_X64:-}" ]]; then
    candidates+=("$JAVA_HOME_17_X64")
  fi

  if [[ -n "${JAVA_HOME:-}" ]]; then
    candidates+=("$JAVA_HOME")
  fi

  while IFS= read -r candidate; do
    candidates+=("$candidate")
  done < <(find /usr/lib/jvm -maxdepth 1 -type d \( -iname '*17*' -o -iname 'java-17-openjdk*' \) 2>/dev/null | sort)

  if command -v javac >/dev/null 2>&1; then
    candidates+=("$(dirname "$(dirname "$(readlink -f "$(command -v javac)")")")")
  elif command -v java >/dev/null 2>&1; then
    candidates+=("$(dirname "$(dirname "$(readlink -f "$(command -v java)")")")")
  fi

  local candidate
  for candidate in "${candidates[@]}"; do
    if [[ -x "$candidate/bin/java" ]]; then
      local version_output
      version_output="$("$candidate/bin/java" -version 2>&1 | awk -F '"' '/version/ { split($2, parts, "."); print parts[1]; exit }')"
      if [[ "$version_output" == "17" ]]; then
        echo "$candidate"
        return 0
      fi
    fi
  done

  return 1
}

has_dotnet_10() {
  if [[ -x "$dotnet_install_dir/dotnet" ]]; then
    "$dotnet_install_dir/dotnet" --list-sdks 2>/dev/null | awk '{print $1}' | grep -Eq '^10\.'
    return
  fi

  if command -v dotnet >/dev/null 2>&1; then
    dotnet --list-sdks 2>/dev/null | awk '{print $1}' | grep -Eq '^10\.'
    return
  fi

  return 1
}

install_dotnet_10() {
  if has_dotnet_10; then
    log ".NET SDK 10.0.x already installed."
    return
  fi

  if [[ "$verify_only" == "true" ]]; then
    warn ".NET SDK 10.0.x missing."
    return
  fi

  log "installing .NET SDK 10.0.x to $dotnet_install_dir."
  local installer
  installer="$(mktemp)"
  curl_https --output "$installer" https://dot.net/v1/dotnet-install.sh
  "${sudo_cmd[@]}" mkdir -p "$dotnet_install_dir"
  "${sudo_cmd[@]}" bash "$installer" --channel 10.0 --install-dir "$dotnet_install_dir"
  rm -f "$installer"
  "${sudo_cmd[@]}" ln -sf "$dotnet_install_dir/dotnet" /usr/local/bin/dotnet
}

has_powershell_7() {
  # shellcheck disable=SC2016
  command -v pwsh >/dev/null 2>&1 \
    && pwsh -NoProfile -Command 'exit ([int]($PSVersionTable.PSVersion.Major -lt 7))' >/dev/null 2>&1
}

source_os_release() {
  [[ -r /etc/os-release ]] || fail "/etc/os-release missing; cannot configure Microsoft package repository."
  # shellcheck disable=SC1091
  . /etc/os-release
}

install_powershell_apt() {
  source_os_release

  case "${ID:-}" in
    ubuntu|debian)
      ;;
    *)
      fail "apt detected, but OS '${ID:-unknown}' is unsupported for automatic PowerShell install."
      ;;
  esac

  [[ -n "${VERSION_ID:-}" ]] || fail "VERSION_ID missing from /etc/os-release; cannot install PowerShell."

  local repository_deb
  repository_deb="$(mktemp --suffix=.deb)"
  log "configuring Microsoft package repository for ${ID} ${VERSION_ID}."
  curl_https --output "$repository_deb" "https://packages.microsoft.com/config/${ID}/${VERSION_ID}/packages-microsoft-prod.deb"
  "${sudo_cmd[@]}" dpkg -i "$repository_deb"
  rm -f "$repository_deb"

  "${sudo_cmd[@]}" apt-get update
  "${sudo_cmd[@]}" env DEBIAN_FRONTEND=noninteractive apt-get install -y powershell
}

install_powershell_rpm() {
  source_os_release

  local rhel_major="${VERSION_ID%%.*}"
  [[ -n "$rhel_major" ]] || fail "VERSION_ID missing from /etc/os-release; cannot install PowerShell."

  log "configuring Microsoft package repository for RHEL-compatible ${rhel_major}."
  "${sudo_cmd[@]}" rpm --import https://packages.microsoft.com/keys/microsoft.asc
  curl_https "https://packages.microsoft.com/config/rhel/${rhel_major}/prod.repo" \
    | "${sudo_cmd[@]}" tee /etc/yum.repos.d/microsoft.repo >/dev/null
}

install_powershell_7() {
  if has_powershell_7; then
    # shellcheck disable=SC2016
    log "PowerShell 7 already installed: $(pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()')."
    return
  fi

  if [[ "$verify_only" == "true" ]]; then
    warn "PowerShell 7 missing."
    return
  fi

  local package_manager
  package_manager="$(detect_package_manager)"

  case "$package_manager" in
    apt)
      install_powershell_apt
      ;;
    dnf)
      install_powershell_rpm
      "${sudo_cmd[@]}" dnf install -y powershell
      ;;
    yum)
      install_powershell_rpm
      "${sudo_cmd[@]}" yum install -y powershell
      ;;
    zypper)
      log "configuring Microsoft package repository for PowerShell."
      "${sudo_cmd[@]}" rpm --import https://packages.microsoft.com/keys/microsoft.asc
      "${sudo_cmd[@]}" zypper --non-interactive addrepo --refresh https://packages.microsoft.com/config/opensuse/15/prod.repo microsoft
      "${sudo_cmd[@]}" zypper --non-interactive install powershell
      ;;
    *)
      fail "Unsupported package manager. Install PowerShell 7 manually so 'pwsh' is on PATH."
      ;;
  esac

  has_powershell_7 || fail "PowerShell 7 install completed but 'pwsh' is unavailable on PATH."
}

write_environment_exports() {
  local java_home="$1"
  local profile_path="/etc/profile.d/azure-devops-sonarqube-agent.sh"

  if [[ "$write_system_env" != "true" ]]; then
    log "system environment export disabled."
    return
  fi

  if [[ "$verify_only" == "true" ]]; then
    log "verify-only: skipping $profile_path write."
    return
  fi

  log "writing $profile_path."
  "${sudo_cmd[@]}" tee "$profile_path" >/dev/null <<EOF
export JAVA_HOME_17_X64="$java_home"
export JAVA_HOME="$java_home"
export DOTNET_ROOT="$dotnet_install_dir"
export PATH="$dotnet_install_dir:\$PATH"
EOF
  "${sudo_cmd[@]}" chmod 0644 "$profile_path"
}

write_systemd_override() {
  local java_home="$1"
  [[ -n "$agent_service_name" ]] || return 0

  if ! command -v systemctl >/dev/null 2>&1; then
    warn "systemctl not found; skipping agent service override."
    return 0
  fi

  if [[ "$verify_only" == "true" ]]; then
    log "verify-only: skipping systemd override for $agent_service_name."
    return 0
  fi

  local override_dir="/etc/systemd/system/${agent_service_name}.d"
  local override_file="${override_dir}/sonarqube-agent-deps.conf"
  local service_path
  service_path="$dotnet_install_dir:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"

  log "writing systemd override for $agent_service_name."
  "${sudo_cmd[@]}" mkdir -p "$override_dir"
  "${sudo_cmd[@]}" tee "$override_file" >/dev/null <<EOF
[Service]
Environment="JAVA_HOME_17_X64=$java_home"
Environment="JAVA_HOME=$java_home"
Environment="DOTNET_ROOT=$dotnet_install_dir"
Environment="PATH=$service_path"
EOF
  "${sudo_cmd[@]}" systemctl daemon-reload
  warn "restart agent service to pick up environment: sudo systemctl restart $agent_service_name"
}

verify_agent_version() {
  if [[ -z "${AGENT_VERSION:-}" ]]; then
    warn "AGENT_VERSION not set. Run inside an Azure Pipelines job to verify agent version automatically."
    warn "SonarQube@8 tasks require Azure Pipelines agent 3.218.0 or newer."
    return
  fi

  if version_ge "$AGENT_VERSION" "3.218.0"; then
    log "Azure Pipelines agent version OK: $AGENT_VERSION."
  else
    fail "Azure Pipelines agent $AGENT_VERSION is too old. Install 3.218.0 or newer."
  fi
}

verify_sonarqube_url() {
  [[ -n "$sonarqube_url" ]] || return 0

  log "checking SonarQube reachability: $sonarqube_url"
  curl -fsSIL --connect-timeout 10 --max-time 30 "$sonarqube_url" >/dev/null \
    || fail "Cannot reach SonarQube URL: $sonarqube_url"
}

verify_final_state() {
  command -v git >/dev/null 2>&1 || fail "git missing."
  command -v curl >/dev/null 2>&1 || fail "curl missing."
  command -v jq >/dev/null 2>&1 || fail "jq missing."
  command -v unzip >/dev/null 2>&1 || fail "unzip missing."
  has_powershell_7 || fail "PowerShell 7 missing or 'pwsh' is not on PATH."

  local java_home
  java_home="$(find_java_home_17)" || fail "Java 17 missing or JAVA_HOME_17_X64 cannot be resolved."

  local major
  major="$("$java_home/bin/java" -version 2>&1 | awk -F '"' '/version/ { split($2, parts, "."); print parts[1]; exit }')"
  [[ "$major" == "17" ]] || fail "Expected Java 17, found Java $major at $java_home."

  if ! has_dotnet_10; then
    fail ".NET SDK 10.0.x missing."
  fi

  log "git: $(git --version)"
  log "java: $("$java_home/bin/java" -version 2>&1 | head -n 1)"
  log "JAVA_HOME_17_X64: $java_home"
  # shellcheck disable=SC2016
  log "pwsh: $(pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()')"
  if [[ -x "$dotnet_install_dir/dotnet" ]]; then
    log "dotnet: $("$dotnet_install_dir/dotnet" --version)"
  else
    log "dotnet: $(dotnet --version)"
  fi
}

install_os_packages
install_powershell_7
install_dotnet_10

java_home_17="$(find_java_home_17)" || fail "Java 17 install did not produce a usable JAVA_HOME."
write_environment_exports "$java_home_17"
write_systemd_override "$java_home_17"
verify_agent_version
verify_sonarqube_url
verify_final_state

log "done."
warn "Azure DevOps SonarQube Server extension and service connection are configured in Azure DevOps, not on this Linux agent."
