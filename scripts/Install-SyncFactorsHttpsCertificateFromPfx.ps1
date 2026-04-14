[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PfxPath,
    [Parameter(Mandatory)]
    [string]$PfxPassword,
    [string]$ProjectRoot,
    [switch]$SkipStoreImport,
    [ValidateSet('CurrentUser', 'LocalMachine')]
    [string]$StoreLocation = 'CurrentUser'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Start-SyncFactorsCommon.ps1')

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Resolve-ProjectRoot
}

$resolvedPfxPath = Resolve-RequiredPath -Path $PfxPath -Label 'PFX certificate'
$paths = Get-SyncFactorsTlsAssetPaths -ProjectRoot $ProjectRoot
New-Item -ItemType Directory -Force -Path $paths.CertificateDirectory | Out-Null

$pfxSecurePassword = ConvertTo-SecureString -String $PfxPassword -AsPlainText -Force
$certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2
$certificate.Import($resolvedPfxPath, $PfxPassword, [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable)

Copy-Item -Path $resolvedPfxPath -Destination $paths.CertificatePath -Force
Set-Content -Path $paths.PasswordPath -Value $PfxPassword -NoNewline

if (-not $SkipStoreImport) {
    if (-not [OperatingSystem]::IsWindows()) {
        throw 'Certificate store import is only supported on Windows. Use -SkipStoreImport to only configure the runtime asset files.'
    }

    $storeName = [System.Security.Cryptography.X509Certificates.StoreName]::My
    $storeLocationEnum = [System.Security.Cryptography.X509Certificates.StoreLocation]::$StoreLocation
    $x509Store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, $storeLocationEnum)

    try {
        $x509Store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)

        $existing = $x509Store.Certificates.Find(
            [System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
            $certificate.Thumbprint,
            $false
        )

        if ($existing.Count -eq 0) {
            $x509Store.Add($certificate)
        }
    }
    finally {
        $x509Store.Close()
    }
}

Write-Host 'SyncFactors HTTPS certificate configured from PFX.' -ForegroundColor Cyan
Write-Host "Source PFX: $resolvedPfxPath"
Write-Host "Runtime certificate: $($paths.CertificatePath)"
Write-Host "Password file: $($paths.PasswordPath)"

if ($SkipStoreImport) {
    Write-Host 'Certificate store import skipped.'
}
elseif ([OperatingSystem]::IsWindows()) {
    Write-Host "Store import: $StoreLocation\\My"
}

Write-Host 'Default API URL: https://127.0.0.1:5087'
Write-Host 'Use the hostname or IP from your certificate SAN when enabling remote access.' -ForegroundColor Cyan
