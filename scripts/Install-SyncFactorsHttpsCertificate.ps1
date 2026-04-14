[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [switch]$Force,
    [switch]$SkipTrust
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Start-SyncFactorsCommon.ps1')

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Resolve-ProjectRoot
}

$paths = Get-SyncFactorsTlsAssetPaths -ProjectRoot $ProjectRoot
New-Item -ItemType Directory -Force -Path $paths.CertificateDirectory | Out-Null

if ($Force) {
    if (Test-Path $paths.CertificatePath) {
        Remove-Item -Force $paths.CertificatePath
    }

    if (Test-Path $paths.PasswordPath) {
        Remove-Item -Force $paths.PasswordPath
    }
}

$password = if (Test-Path $paths.PasswordPath) {
    (Get-Content -Path $paths.PasswordPath -Raw).Trim()
}
else {
    (
        [Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Minimum 0 -Maximum 256 }))
    ).Replace('/', 'A').Replace('+', 'B')
}

Set-Content -Path $paths.PasswordPath -Value $password -NoNewline

$arguments = @('dev-certs', 'https', '--export-path', $paths.CertificatePath, '--password', $password)
if (-not $SkipTrust) {
    $arguments += '--trust'
}

dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet dev-certs https failed."
}

Write-Host 'SyncFactors HTTPS certificate ready.' -ForegroundColor Cyan
Write-Host "Certificate: $($paths.CertificatePath)"
Write-Host "Password file: $($paths.PasswordPath)"
Write-Host 'Default local API URL: https://127.0.0.1:5087'
Write-Host 'Remote access requires a certificate whose SAN matches the external DNS name or IP.' -ForegroundColor Yellow
Write-Host ''
Write-Host 'Optional .env.worktree overrides:' -ForegroundColor Cyan
Write-Host "SYNCFACTORS_TLS_CERT_PATH=$($paths.CertificatePath)"
Write-Host "SYNCFACTORS_TLS_CERT_PASSWORD=$password"
