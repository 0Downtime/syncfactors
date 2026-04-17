<#
.SYNOPSIS
Tests an LDAP, LDAPS, or StartTLS bind to an Active Directory domain controller.

.DESCRIPTION
Creates a System.DirectoryServices.Protocols.LdapConnection, binds with the
supplied credentials, and optionally reads rootDSE to confirm the connection
can execute a basic search.

.EXAMPLE
pwsh ./scripts/Test-LdapConnection.ps1 -Server dc01.example.local -Mode ldaps -Username svc_sync@example.local

.EXAMPLE
pwsh ./scripts/Test-LdapConnection.ps1 -Server dc01.example.local -Mode starttls -Username svc_sync@example.local -RequireSigning

.EXAMPLE
pwsh ./scripts/Test-LdapConnection.ps1 -Server dc01.example.local -Mode ldaps -Username svc_sync@example.local -SkipCertificateValidation
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Server,

    [Parameter()]
    [ValidateSet('ldaps', 'starttls', 'ldap')]
    [string]$Mode = 'ldaps',

    [Parameter()]
    [Nullable[int]]$Port,

    [Parameter()]
    [string]$Username,

    [Parameter()]
    [string]$BindPassword,

    [Parameter()]
    [string]$BindPasswordEnv = 'SF_AD_SYNC_AD_BIND_PASSWORD',

    [Parameter()]
    [switch]$RequireSigning,

    [Parameter()]
    [switch]$SkipCertificateValidation,

    [Parameter()]
    [string[]]$TrustedCertificateThumbprints = @(),

    [Parameter()]
    [switch]$SkipRootDseQuery
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-DefaultLdapPort {
    param(
        [Parameter(Mandatory)]
        [string]$RequestedMode
    )

    if ($RequestedMode -eq 'ldaps') {
        return 636
    }

    return 389
}

function Normalize-Thumbprint {
    param(
        [Parameter()]
        [AllowNull()]
        [string]$Thumbprint
    )

    if ([string]::IsNullOrWhiteSpace($Thumbprint)) {
        return [string]::Empty
    }

    return $Thumbprint.Replace(':', '', [System.StringComparison]::Ordinal).
        Replace(' ', '', [System.StringComparison]::Ordinal).
        Trim()
}

function Resolve-BindPassword {
    if (-not [string]::IsNullOrWhiteSpace($BindPassword)) {
        return $BindPassword
    }

    if (-not [string]::IsNullOrWhiteSpace($BindPasswordEnv)) {
        $envValue = [Environment]::GetEnvironmentVariable($BindPasswordEnv)
        if (-not [string]::IsNullOrWhiteSpace($envValue)) {
            return $envValue
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($Username)) {
        $secureString = Read-Host -Prompt 'Bind password' -AsSecureString
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureString)
        try {
            return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
        }
        finally {
            if ($bstr -ne [IntPtr]::Zero) {
                [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
            }
        }
    }

    return $null
}

function Test-LdapServerCertificate {
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate]$Certificate
    )

    if ($SkipCertificateValidation) {
        return $true
    }

    $certificate2 = if ($Certificate -is [System.Security.Cryptography.X509Certificates.X509Certificate2]) {
        $Certificate
    }
    else {
        [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($Certificate)
    }

    $configuredThumbprints = @($TrustedCertificateThumbprints |
        ForEach-Object { Normalize-Thumbprint -Thumbprint $_ } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

    if ($configuredThumbprints.Count -gt 0) {
        $certificateThumbprint = Normalize-Thumbprint -Thumbprint $certificate2.Thumbprint
        return $configuredThumbprints -contains $certificateThumbprint
    }

    $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
    try {
        $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
        $chain.ChainPolicy.VerificationFlags = [System.Security.Cryptography.X509Certificates.X509VerificationFlags]::NoFlag
        return $chain.Build($certificate2)
    }
    finally {
        $chain.Dispose()
        if ($certificate2 -isnot [System.Security.Cryptography.X509Certificates.X509Certificate2]) {
            $certificate2.Dispose()
        }
    }
}

function New-LdapConnection {
    param(
        [Parameter(Mandatory)]
        [string]$RequestedMode,
        [Parameter(Mandatory)]
        [string]$DirectoryServer,
        [Parameter(Mandatory)]
        [int]$DirectoryPort,
        [Parameter()]
        [string]$DirectoryUsername,
        [Parameter()]
        [string]$DirectoryPassword
    )

    $identifier = [System.DirectoryServices.Protocols.LdapDirectoryIdentifier]::new($DirectoryServer, $DirectoryPort)
    $connection = [System.DirectoryServices.Protocols.LdapConnection]::new($identifier)
    $connection.Timeout = [TimeSpan]::FromSeconds(15)
    $connection.AuthType = if ([string]::IsNullOrWhiteSpace($DirectoryUsername)) {
        [System.DirectoryServices.Protocols.AuthType]::Anonymous
    }
    else {
        [System.DirectoryServices.Protocols.AuthType]::Basic
    }

    if (-not [string]::IsNullOrWhiteSpace($DirectoryUsername)) {
        $connection.Credential = [System.Net.NetworkCredential]::new($DirectoryUsername, $DirectoryPassword)
    }

    $connection.SessionOptions.ProtocolVersion = 3
    $connection.SessionOptions.ReferralChasing = [System.DirectoryServices.Protocols.ReferralChasingOptions]::None

    if ($RequireSigning) {
        $connection.SessionOptions.Signing = $true
        $connection.SessionOptions.Sealing = $true
    }

    if ($RequestedMode -ne 'ldap') {
        $callbackScriptBlock = {
            param($ldapConnection, $certificate)
            return (Test-LdapServerCertificate -Certificate $certificate)
        }.GetNewClosure()
        $callback = [System.DirectoryServices.Protocols.VerifyServerCertificateCallback]$callbackScriptBlock
        $connection.SessionOptions.VerifyServerCertificate = $callback
    }

    if ($RequestedMode -eq 'ldaps') {
        $connection.SessionOptions.SecureSocketLayer = $true
    }
    elseif ($RequestedMode -eq 'starttls') {
        $connection.SessionOptions.StartTransportLayerSecurity($null)
    }

    $connection.Bind()
    return $connection
}

function Get-RootDseSummary {
    param(
        [Parameter(Mandatory)]
        [System.DirectoryServices.Protocols.LdapConnection]$Connection
    )

    $request = [System.DirectoryServices.Protocols.SearchRequest]::new(
        '',
        '(objectClass=*)',
        [System.DirectoryServices.Protocols.SearchScope]::Base,
        @('defaultNamingContext', 'dnsHostName', 'supportedLDAPVersion'))
    $response = [System.DirectoryServices.Protocols.SearchResponse]$Connection.SendRequest($request)
    if ($response.Entries.Count -eq 0) {
        throw 'rootDSE query returned no entries.'
    }

    $entry = [System.DirectoryServices.Protocols.SearchResultEntry]$response.Entries[0]
    $getValue = {
        param([string]$AttributeName)
        if (-not $entry.Attributes.Contains($AttributeName)) {
            return $null
        }

        $values = $entry.Attributes[$AttributeName]
        if ($null -eq $values -or $values.Count -eq 0) {
            return $null
        }

        if ($values.Count -eq 1) {
            return [string]$values[0]
        }

        return @($values | ForEach-Object { [string]$_ })
    }

    [pscustomobject]@{
        DnsHostName          = & $getValue 'dnsHostName'
        DefaultNamingContext = & $getValue 'defaultNamingContext'
        SupportedLdapVersion = (& $getValue 'supportedLDAPVersion') -join ','
    }
}

$resolvedPort = if ($null -ne $Port) { $Port.Value } else { Get-DefaultLdapPort -RequestedMode $Mode }
$resolvedPassword = Resolve-BindPassword
$startedAt = Get-Date

try {
    $connection = New-LdapConnection `
        -RequestedMode $Mode `
        -DirectoryServer $Server `
        -DirectoryPort $resolvedPort `
        -DirectoryUsername $Username `
        -DirectoryPassword $resolvedPassword

    try {
        $rootDse = if ($SkipRootDseQuery) { $null } else { Get-RootDseSummary -Connection $connection }
        $result = [pscustomobject]@{
            Succeeded            = $true
            Server               = $Server
            Port                 = $resolvedPort
            Mode                 = $Mode
            Username             = if ([string]::IsNullOrWhiteSpace($Username)) { '(anonymous)' } else { $Username }
            CheckedAt            = $startedAt
            DefaultNamingContext = $rootDse.DefaultNamingContext
            DnsHostName          = $rootDse.DnsHostName
            SupportedLdapVersion = $rootDse.SupportedLdapVersion
        }

        $result | Format-List
        exit 0
    }
    finally {
        $connection.Dispose()
    }
}
catch [System.DirectoryServices.Protocols.LdapException] {
    $exception = $_.Exception
    $errorCode = if ($null -ne $exception -and $exception.PSObject.Properties.Name -contains 'ErrorCode') {
        $exception.ErrorCode
    }
    elseif ($null -ne $exception -and $exception.PSObject.Properties.Name -contains 'HResult') {
        $exception.HResult
    }
    else {
        '(unavailable)'
    }
    $serverDetail = if ($null -ne $exception -and $exception.PSObject.Properties.Name -contains 'ServerErrorMessage') {
        $exception.ServerErrorMessage
    }
    else {
        $null
    }

    Write-Error ("LDAP bind failed. Server='{0}' Port={1} Mode={2} ErrorCode={3} Message='{4}' ServerDetail='{5}'" -f `
        $Server, $resolvedPort, $Mode, $errorCode, $exception.Message, $serverDetail)
    exit 1
}
catch {
    Write-Error $_
    exit 1
}
