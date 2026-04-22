<#
.SYNOPSIS
Creates a self-signed LDAPS certificate for a domain controller.

.DESCRIPTION
Run this script on the domain controller as an administrator. It creates a
self-signed certificate with the Server Authentication EKU in
Cert:\LocalMachine\My, optionally copies the certificate into
Cert:\LocalMachine\Root, exports a .cer for clients, and can attempt a light
service restart.

This is intended for lab and test environments where no internal CA exists.
Clients should either trust the exported .cer, pin the thumbprint, or disable
certificate validation temporarily.

.EXAMPLE
pwsh ./scripts/Enable-DomainControllerLdapsSelfSigned.ps1

.EXAMPLE
pwsh ./scripts/Enable-DomainControllerLdapsSelfSigned.ps1 `
  -ExportDirectory C:\Temp\ldaps `
  -AddToTrustedRoot `
  -RestartNetlogon
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string[]]$DnsName,
    [ValidateRange(1, 20)]
    [int]$YearsValid = 5,
    [string]$FriendlyName = 'SyncFactors LDAPS (self-signed)',
    [string]$ExportDirectory,
    [switch]$AddToTrustedRoot,
    [switch]$RestartNetlogon,
    [switch]$SkipPortCheck,
    [switch]$ExportPfx,
    [string]$PfxPassword
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-DefaultDnsNames {
    $names = [System.Collections.Generic.List[string]]::new()

    if (-not [string]::IsNullOrWhiteSpace($env:COMPUTERNAME)) {
        $names.Add($env:COMPUTERNAME)
    }

    if (-not [string]::IsNullOrWhiteSpace($env:USERDNSDOMAIN)) {
        $names.Add("$($env:COMPUTERNAME).$($env:USERDNSDOMAIN)")
    }

    try {
        $hostEntry = [System.Net.Dns]::GetHostEntry($env:COMPUTERNAME)
        if (-not [string]::IsNullOrWhiteSpace($hostEntry.HostName)) {
            $names.Add($hostEntry.HostName)
        }
    }
    catch {
    }

    return $names |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $_.Trim() } |
        Select-Object -Unique
}

function Assert-Administrator {
    if (-not (Test-IsAdministrator)) {
        throw 'Run this script from an elevated PowerShell session on the domain controller.'
    }
}

function Ensure-Directory {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        [System.IO.Directory]::CreateDirectory($Path) | Out-Null
    }
}

function Export-CertificateFile {
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [Parameter(Mandatory)]
        [string]$Path
    )

    Export-Certificate -Cert $Certificate -FilePath $Path -Force | Out-Null
    return $Path
}

function Import-CertificateToRoot {
    param(
        [Parameter(Mandatory)]
        [string]$CertificatePath
    )

    Import-Certificate -FilePath $CertificatePath -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
}

function Export-PfxFile {
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Password
    )

    $securePassword = ConvertTo-SecureString -String $Password -AsPlainText -Force
    Export-PfxCertificate -Cert $Certificate -FilePath $Path -Password $securePassword -Force | Out-Null
    return $Path
}

function Test-LdapsListener {
    try {
        $connection = Get-NetTCPConnection -LocalPort 636 -State Listen -ErrorAction Stop |
            Select-Object -First 1
        return $null -ne $connection
    }
    catch {
        return $false
    }
}

Assert-Administrator

$resolvedDnsNames = if ($DnsName -and $DnsName.Count -gt 0) {
    $DnsName |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $_.Trim() } |
        Select-Object -Unique
}
else {
    Get-DefaultDnsNames
}

if (-not $resolvedDnsNames -or $resolvedDnsNames.Count -eq 0) {
    throw 'Could not determine any DNS names for the certificate. Pass -DnsName explicitly.'
}

if ($ExportPfx -and [string]::IsNullOrWhiteSpace($PfxPassword)) {
    throw 'Pass -PfxPassword when using -ExportPfx.'
}

$notAfter = (Get-Date).AddYears($YearsValid)
$serverAuthEku = '1.3.6.1.5.5.7.3.1'

if ($PSCmdlet.ShouldProcess(($resolvedDnsNames -join ', '), 'Create self-signed LDAPS certificate')) {
    $certificate = New-SelfSignedCertificate `
        -DnsName $resolvedDnsNames `
        -FriendlyName $FriendlyName `
        -CertStoreLocation 'Cert:\LocalMachine\My' `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -KeyExportPolicy Exportable `
        -KeySpec KeyExchange `
        -Provider 'Microsoft RSA SChannel Cryptographic Provider' `
        -NotAfter $notAfter `
        -TextExtension @("2.5.29.37={text}$serverAuthEku")
}

$exportedCerPath = $null
$exportedPfxPath = $null

if (-not [string]::IsNullOrWhiteSpace($ExportDirectory)) {
    Ensure-Directory -Path $ExportDirectory
    $baseName = "ldaps-$($env:COMPUTERNAME)-$($certificate.Thumbprint)"
    $exportedCerPath = Export-CertificateFile -Certificate $certificate -Path (Join-Path $ExportDirectory "$baseName.cer")

    if ($ExportPfx) {
        $exportedPfxPath = Export-PfxFile -Certificate $certificate -Path (Join-Path $ExportDirectory "$baseName.pfx") -Password $PfxPassword
    }
}

if ($AddToTrustedRoot) {
    if (-not $exportedCerPath) {
        $tempCerPath = Join-Path ([System.IO.Path]::GetTempPath()) "ldaps-$($certificate.Thumbprint).cer"
        $exportedCerPath = Export-CertificateFile -Certificate $certificate -Path $tempCerPath
    }

    Import-CertificateToRoot -CertificatePath $exportedCerPath
}

if ($RestartNetlogon -and $PSCmdlet.ShouldProcess('Netlogon', 'Restart service')) {
    Restart-Service -Name Netlogon -Force
}

$ldapsListening = $null
if (-not $SkipPortCheck) {
    Start-Sleep -Seconds 3
    $ldapsListening = Test-LdapsListener
}

Write-Host 'LDAPS certificate created.' -ForegroundColor Cyan
Write-Host "Thumbprint: $($certificate.Thumbprint)"
Write-Host "Subject: $($certificate.Subject)"
Write-Host "DNS names: $($resolvedDnsNames -join ', ')"
Write-Host "Not after: $($certificate.NotAfter.ToString('u'))"
Write-Host "Certificate store: Cert:\\LocalMachine\\My"

if ($AddToTrustedRoot) {
    Write-Host 'Trusted root store: added to Cert:\LocalMachine\Root'
}

if ($exportedCerPath) {
    Write-Host "Exported CER: $exportedCerPath"
}

if ($exportedPfxPath) {
    Write-Host "Exported PFX: $exportedPfxPath"
}

if ($null -ne $ldapsListening) {
    if ($ldapsListening) {
        Write-Host 'Port 636 is listening.' -ForegroundColor Green
    }
    else {
        Write-Warning 'Port 636 is not listening yet. A reboot may be required before Schannel/AD DS starts presenting the new certificate.'
    }
}

Write-Host ''
Write-Host 'Client follow-up options:' -ForegroundColor Cyan
Write-Host "1. Import the exported CER into each client's trusted root store."
Write-Host "2. Pin this thumbprint in SyncFactors ad.transport.trustedCertificateThumbprints."
Write-Host '3. For lab-only use, disable certificate validation temporarily in the app config.'
