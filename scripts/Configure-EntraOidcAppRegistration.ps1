[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$TenantId,
    [string]$AppDisplayName = 'SyncFactors Local Dev',
    [string]$ApplicationObjectId,
    [string]$ClientId,
    [ValidateSet('oidc', 'hybrid')]
    [string]$AuthMode = 'oidc',
    [string]$ApiBindHost = '127.0.0.1',
    [string]$ApiPublicHost = '127.0.0.1',
    [int]$ApiPort = 5087,
    [int]$ClientSecretMonths = 12,
    [string]$ViewerGroupObjectId,
    [string]$ViewerGroupName,
    [string]$OperatorGroupObjectId,
    [string]$OperatorGroupName,
    [string]$AdminGroupObjectId,
    [string]$AdminGroupName,
    [string]$BootstrapAdminUsername = 'admin',
    [string]$BootstrapAdminPassword,
    [switch]$RequireAssignment,
    [switch]$SkipClientSecret,
    [switch]$InstallModules,
    [string]$EnvFilePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ConsumerTenantId = '9188040d-6c67-4c5b-b112-36a304b66dad'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).ProviderPath

function Assert-UsablePublicHost {
    param(
        [Parameter(Mandatory)]
        [string]$HostName
    )

    if ([string]::IsNullOrWhiteSpace($HostName)) {
        throw 'ApiPublicHost cannot be empty.'
    }

    $normalizedHost = $HostName.Trim()
    if ($normalizedHost -in @('0.0.0.0', '::', '[::]', '*', '+')) {
        throw "ApiPublicHost '$HostName' is not a browser-usable host name. Use the DNS name or IP address that operators will browse to."
    }

    return $normalizedHost
}

function Format-UrlHost {
    param(
        [Parameter(Mandatory)]
        [string]$HostName
    )

    if ($HostName.Contains(':') -and -not ($HostName.StartsWith('[') -and $HostName.EndsWith(']'))) {
        return "[$HostName]"
    }

    return $HostName
}

function Ensure-Module {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    if (Get-Module -ListAvailable -Name $Name) {
        return
    }

    if (-not $InstallModules) {
        throw "Required PowerShell module '$Name' is not installed. Re-run with -InstallModules or install it manually with: Install-Module $Name -Scope CurrentUser"
    }

    Write-Host "Installing PowerShell module $Name for the current user..." -ForegroundColor Cyan
    Install-Module -Name $Name -Scope CurrentUser -Force
}

function Ensure-GraphConnection {
    param(
        [string]$TenantId,
        [string[]]$Scopes
    )

    $context = Get-MgContext -ErrorAction SilentlyContinue
    $missingConnection = $null -eq $context
    $wrongTenant = $false

    if (-not $missingConnection -and -not [string]::IsNullOrWhiteSpace($TenantId)) {
        $wrongTenant = -not [string]::Equals($context.TenantId, $TenantId, [System.StringComparison]::OrdinalIgnoreCase)
    }

    if ($missingConnection -or $wrongTenant) {
        $connectParams = @{
            Scopes    = $Scopes
            NoWelcome = $true
        }

        if (-not [string]::IsNullOrWhiteSpace($TenantId)) {
            $connectParams['TenantId'] = $TenantId
        }

        Write-Host 'Connecting to Microsoft Graph...' -ForegroundColor Cyan
        Connect-MgGraph @connectParams | Out-Null
        if (Get-Command Select-MgProfile -ErrorAction SilentlyContinue) {
            Select-MgProfile -Name 'v1.0'
        }
        return Get-MgContext
    }

    if (Get-Command Select-MgProfile -ErrorAction SilentlyContinue) {
        Select-MgProfile -Name 'v1.0'
    }
    return $context
}

function Test-IsConsumerTenant {
    param(
        [string]$TenantId
    )

    if ([string]::IsNullOrWhiteSpace($TenantId)) {
        return $false
    }

    return [string]::Equals($TenantId, $ConsumerTenantId, [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-UsableTenantContext {
    param(
        [Parameter(Mandatory)]
        $Context,
        [string]$RequestedTenantId
    )

    $account = if ($null -ne $Context -and $null -ne $Context.Account) { [string]$Context.Account } else { '' }
    $resolvedTenantId = if ($null -ne $Context -and $null -ne $Context.TenantId) { [string]$Context.TenantId } else { '' }

    Write-Host 'Graph context' -ForegroundColor Cyan
    Write-Host "Account:  $account"
    Write-Host "TenantId: $resolvedTenantId"
    if (-not [string]::IsNullOrWhiteSpace($RequestedTenantId)) {
        Write-Host "RequestedTenantId: $RequestedTenantId"
    }

    if ([string]::IsNullOrWhiteSpace($resolvedTenantId)) {
        throw 'Microsoft Graph did not return a tenant id for this session. Sign in with a work or school Entra account, or pass -TenantId explicitly.'
    }

    if (Test-IsConsumerTenant -TenantId $resolvedTenantId) {
        throw "Connected to the Microsoft consumer tenant ($resolvedTenantId), not a work or school Entra tenant. Re-run with -TenantId <your-entra-tenant-guid> and sign in with a work or school account that can register applications."
    }

    if (-not [string]::IsNullOrWhiteSpace($RequestedTenantId) -and
        -not [string]::Equals($resolvedTenantId, $RequestedTenantId, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Connected to tenant '$resolvedTenantId', but the requested tenant was '$RequestedTenantId'. Disconnect and reconnect to the intended tenant."
    }
}

function Update-OrInsertEnvValue {
    param(
        [Parameter(Mandatory)]
        [System.Collections.Generic.List[string]]$Lines,
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [string]$Value
    )

    $updated = $false
    for ($index = 0; $index -lt $Lines.Count; $index++) {
        $line = $Lines[$index]
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed)) {
            continue
        }

        $candidate = if ($trimmed.StartsWith('#', [System.StringComparison]::Ordinal)) {
            $trimmed.Substring(1).TrimStart()
        }
        else {
            $trimmed
        }

        if (-not $candidate.StartsWith("$Name=", [System.StringComparison]::Ordinal)) {
            continue
        }

        $Lines[$index] = "$Name=$Value"
        $updated = $true
        break
    }

    if (-not $updated) {
        $Lines.Add("$Name=$Value") | Out-Null
    }
}

function Update-EnvFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [hashtable]$Values
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    if (Test-Path $Path) {
        foreach ($line in Get-Content -Path $Path) {
            $lines.Add([string]$line) | Out-Null
        }
    }

    foreach ($entry in $Values.GetEnumerator()) {
        if ([string]::IsNullOrWhiteSpace([string]$entry.Value)) {
            continue
        }

        Update-OrInsertEnvValue -Lines $lines -Name ([string]$entry.Key) -Value ([string]$entry.Value)
    }

    Set-Content -Path $Path -Value $lines
}

function Resolve-EnvFileValue {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Name
    )

    if (-not (Test-Path $Path)) {
        return $null
    }

    foreach ($line in Get-Content -Path $Path) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed)) {
            continue
        }

        $candidate = if ($trimmed.StartsWith('#', [System.StringComparison]::Ordinal)) {
            $trimmed.Substring(1).TrimStart()
        }
        else {
            $trimmed
        }

        if (-not $candidate.StartsWith("$Name=", [System.StringComparison]::Ordinal)) {
            continue
        }

        return $candidate.Substring($Name.Length + 1)
    }

    return $null
}

function Resolve-KeychainServiceName {
    param(
        [Parameter(Mandatory)]
        [string]$EnvFilePath
    )

    if (-not [string]::IsNullOrWhiteSpace($env:SYNCFACTORS_KEYCHAIN_SERVICE)) {
        return $env:SYNCFACTORS_KEYCHAIN_SERVICE
    }

    $configured = Resolve-EnvFileValue -Path $EnvFilePath -Name 'SYNCFACTORS_KEYCHAIN_SERVICE'
    if (-not [string]::IsNullOrWhiteSpace($configured)) {
        return $configured
    }

    return 'syncfactors'
}

function Store-SecretInLocalCredentialStore {
    param(
        [Parameter(Mandatory)]
        [string]$RepoRoot,
        [Parameter(Mandatory)]
        [string]$EnvFilePath,
        [Parameter(Mandatory)]
        [string]$VariableName,
        [Parameter(Mandatory)]
        [string]$Value
    )

    if ([OperatingSystem]::IsWindows()) {
        . (Join-Path $RepoRoot 'scripts/codex/WorktreeEnv.ps1')
        Set-SyncFactorsCredentialValue -RepoRoot $RepoRoot -VariableName $VariableName -Value $Value
        return 'Windows Credential Manager'
    }

    if ($IsMacOS) {
        $service = Resolve-KeychainServiceName -EnvFilePath $EnvFilePath
        & security add-generic-password -U -s $service -a $VariableName -w $Value | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to store $VariableName in macOS Keychain service '$service'."
        }

        return "macOS Keychain ($service)"
    }

    return $null
}

function New-GeneratedPassword {
    param(
        [int]$Length = 24
    )

    $bytes = New-Object byte[] ($Length)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $base = [Convert]::ToBase64String($bytes).Replace('+', 'A').Replace('/', 'b')
    return ('S!' + $base).Substring(0, [Math]::Min($base.Length + 2, $Length))
}

function Invoke-GraphGet {
    param(
        [Parameter(Mandatory)]
        [string]$Uri
    )

    return Invoke-MgGraphRequest -Method GET -Uri $Uri -OutputType PSObject
}

function Invoke-GraphPost {
    param(
        [Parameter(Mandatory)]
        [string]$Uri,
        [Parameter(Mandatory)]
        [hashtable]$Body
    )

    return Invoke-MgGraphRequest -Method POST -Uri $Uri -Body ($Body | ConvertTo-Json -Depth 10) -ContentType 'application/json' -OutputType PSObject
}

function Invoke-GraphPatch {
    param(
        [Parameter(Mandatory)]
        [string]$Uri,
        [Parameter(Mandatory)]
        [hashtable]$Body
    )

    Invoke-MgGraphRequest -Method PATCH -Uri $Uri -Body ($Body | ConvertTo-Json -Depth 10) -ContentType 'application/json' | Out-Null
}

function Get-GraphCollectionItems {
    param(
        [Parameter(Mandatory)]
        [string]$CollectionPath,
        [string[]]$SelectFields = @()
    )

    $uri = "https://graph.microsoft.com/v1.0/{0}" -f $CollectionPath
    if ($SelectFields.Count -gt 0) {
        $uri = '{0}?$select={1}' -f $uri, ($SelectFields -join ',')
    }

    $items = @()
    do {
        $response = Invoke-GraphGet -Uri $uri
        if ($null -ne $response.value) {
            $items += @($response.value)
        }
        $nextLinkProperty = $response.PSObject.Properties['@odata.nextLink']
        $uri = if ($null -ne $nextLinkProperty -and -not [string]::IsNullOrWhiteSpace([string]$nextLinkProperty.Value)) {
            [string]$nextLinkProperty.Value
        }
        else {
            $null
        }
    } while (-not [string]::IsNullOrWhiteSpace($uri))

    return $items
}

function Get-SingleItemByPropertyValue {
    param(
        [Parameter(Mandatory)]
        [string]$CollectionPath,
        [Parameter(Mandatory)]
        [string]$PropertyName,
        [Parameter(Mandatory)]
        [string]$PropertyValue,
        [string[]]$SelectFields = @()
    )

    $items = @(Get-GraphCollectionItems -CollectionPath $CollectionPath -SelectFields $SelectFields)
    $matched = @($items | Where-Object {
        $candidate = $_.$PropertyName
        -not [string]::IsNullOrWhiteSpace($candidate) -and
        [string]::Equals([string]$candidate, $PropertyValue, [System.StringComparison]::OrdinalIgnoreCase)
    })

    if ($matched.Count -gt 1) {
        throw "Property match '$PropertyName=$PropertyValue' returned multiple objects under '$CollectionPath'. Use a more specific value."
    }

    if ($matched.Count -eq 0) {
        return $null
    }

    return $matched[0]
}

function Ensure-Application {
    param(
        [string]$ApplicationObjectId,
        [string]$ClientId,
        [Parameter(Mandatory)]
        [string]$DisplayName,
        [Parameter(Mandatory)]
        [string[]]$RedirectUris
    )

    $existing = $null
    if (-not [string]::IsNullOrWhiteSpace($ApplicationObjectId)) {
        $existing = Invoke-GraphGet -Uri ("https://graph.microsoft.com/v1.0/applications/{0}" -f $ApplicationObjectId)
    }
    elseif (-not [string]::IsNullOrWhiteSpace($ClientId)) {
        $existing = Get-SingleItemByPropertyValue `
            -CollectionPath 'applications' `
            -PropertyName 'appId' `
            -PropertyValue $ClientId `
            -SelectFields @('id', 'appId', 'displayName')
    }
    else {
        $existing = Get-SingleItemByPropertyValue `
            -CollectionPath 'applications' `
            -PropertyName 'displayName' `
            -PropertyValue $DisplayName `
            -SelectFields @('id', 'appId', 'displayName')
    }

    $groupMembershipClaims = if ($RequireAssignment -or
        -not [string]::IsNullOrWhiteSpace($ViewerGroupObjectId) -or
        -not [string]::IsNullOrWhiteSpace($ViewerGroupName) -or
        -not [string]::IsNullOrWhiteSpace($OperatorGroupObjectId) -or
        -not [string]::IsNullOrWhiteSpace($OperatorGroupName) -or
        -not [string]::IsNullOrWhiteSpace($AdminGroupObjectId) -or
        -not [string]::IsNullOrWhiteSpace($AdminGroupName)) {
        'ApplicationGroup'
    }
    else {
        'SecurityGroup'
    }

    $groupOptionalClaim = @{
        name                 = 'groups'
        source               = $null
        essential            = $false
        additionalProperties = @()
    }

    $appBody = @{
        displayName            = $DisplayName
        signInAudience         = 'AzureADMyOrg'
        groupMembershipClaims  = $groupMembershipClaims
        optionalClaims         = @{
            idToken     = @($groupOptionalClaim)
            accessToken = @($groupOptionalClaim)
        }
        web                    = @{
            redirectUris = $RedirectUris
            logoutUrl    = $RedirectUris[1]
            implicitGrantSettings = @{
                enableAccessTokenIssuance = $false
                enableIdTokenIssuance     = $false
            }
        }
    }

    if ($null -eq $existing) {
        if (-not $PSCmdlet.ShouldProcess($DisplayName, 'Create Entra application registration')) {
            return $null
        }

        return Invoke-GraphPost -Uri 'https://graph.microsoft.com/v1.0/applications' -Body $appBody
    }

    if ($PSCmdlet.ShouldProcess(($existing.id ?? $DisplayName), 'Update Entra application registration')) {
        Invoke-GraphPatch -Uri ("https://graph.microsoft.com/v1.0/applications/{0}" -f $existing.id) -Body $appBody
    }

    return Invoke-GraphGet -Uri ("https://graph.microsoft.com/v1.0/applications/{0}" -f $existing.id)
}

function Ensure-ServicePrincipal {
    param(
        [Parameter(Mandatory)]
        [string]$AppId,
        [Parameter(Mandatory)]
        [bool]$RequireAssignment
    )

    $existing = Get-SingleItemByPropertyValue `
        -CollectionPath 'servicePrincipals' `
        -PropertyName 'appId' `
        -PropertyValue $AppId `
        -SelectFields @('id', 'appId', 'displayName', 'appRoleAssignmentRequired')

    if ($null -eq $existing) {
        if (-not $PSCmdlet.ShouldProcess($AppId, 'Create enterprise application service principal')) {
            return $null
        }

        $existing = Invoke-GraphPost -Uri 'https://graph.microsoft.com/v1.0/servicePrincipals' -Body @{
            appId = $AppId
        }
    }

    if ($PSCmdlet.ShouldProcess($existing.id, 'Update enterprise application assignment requirement')) {
        Invoke-GraphPatch -Uri ("https://graph.microsoft.com/v1.0/servicePrincipals/{0}" -f $existing.id) -Body @{
            appRoleAssignmentRequired = $RequireAssignment
        }
    }

    return Invoke-GraphGet -Uri ("https://graph.microsoft.com/v1.0/servicePrincipals/{0}" -f $existing.id)
}

function Ensure-GroupAssignment {
    param(
        [Parameter(Mandatory)]
        [string]$GroupObjectId,
        [Parameter(Mandatory)]
        [string]$ServicePrincipalObjectId
    )

    $existingAssignments = Invoke-GraphGet -Uri ("https://graph.microsoft.com/v1.0/groups/{0}/appRoleAssignments" -f $GroupObjectId)
    $matched = @($existingAssignments.value | Where-Object {
        $_.resourceId -eq $ServicePrincipalObjectId -and $_.appRoleId -eq '00000000-0000-0000-0000-000000000000'
    })

    if ($matched.Count -gt 0) {
        return
    }

    if (-not $PSCmdlet.ShouldProcess($GroupObjectId, 'Assign group to enterprise application')) {
        return
    }

    Invoke-GraphPost -Uri ("https://graph.microsoft.com/v1.0/groups/{0}/appRoleAssignments" -f $GroupObjectId) -Body @{
        principalId = $GroupObjectId
        resourceId  = $ServicePrincipalObjectId
        appRoleId   = '00000000-0000-0000-0000-000000000000'
    } | Out-Null
}

function New-GroupMailNickname {
    param(
        [Parameter(Mandatory)]
        [string]$DisplayName
    )

    $base = ($DisplayName -replace '[^A-Za-z0-9]', '').ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($base)) {
        $base = 'syncfactorsgroup'
    }

    if ($base.Length -gt 40) {
        $base = $base.Substring(0, 40)
    }

    return '{0}{1}' -f $base, ([System.Guid]::NewGuid().ToString('N').Substring(0, 8))
}

function Ensure-Group {
    param(
        [string]$GroupObjectId,
        [Parameter(Mandatory)]
        [string]$DisplayName
    )

    if (-not [string]::IsNullOrWhiteSpace($GroupObjectId)) {
        return Invoke-GraphGet -Uri ("https://graph.microsoft.com/v1.0/groups/{0}?`$select=id,displayName" -f $GroupObjectId)
    }

    $existing = Get-SingleItemByPropertyValue `
        -CollectionPath 'groups' `
        -PropertyName 'displayName' `
        -PropertyValue $DisplayName `
        -SelectFields @('id', 'displayName')
    if ($null -ne $existing) {
        return Invoke-GraphGet -Uri ("https://graph.microsoft.com/v1.0/groups/{0}?`$select=id,displayName" -f $existing.id)
    }

    if (-not $PSCmdlet.ShouldProcess($DisplayName, 'Create security group')) {
        return $null
    }

    return Invoke-GraphPost -Uri 'https://graph.microsoft.com/v1.0/groups' -Body @{
        displayName     = $DisplayName
        description     = "SyncFactors OIDC role group for $DisplayName"
        mailEnabled     = $false
        mailNickname    = (New-GroupMailNickname -DisplayName $DisplayName)
        securityEnabled = $true
    }
}

function New-ClientSecret {
    param(
        [Parameter(Mandatory)]
        [string]$ApplicationObjectId,
        [Parameter(Mandatory)]
        [int]$LifetimeMonths
    )

    if (-not $PSCmdlet.ShouldProcess($ApplicationObjectId, 'Create application client secret')) {
        return $null
    }

    return Invoke-GraphPost -Uri ("https://graph.microsoft.com/v1.0/applications/{0}/addPassword" -f $ApplicationObjectId) -Body @{
        passwordCredential = @{
            displayName = 'SyncFactors local OIDC'
            endDateTime = (Get-Date).ToUniversalTime().AddMonths($LifetimeMonths).ToString('o')
        }
    }
}

function Get-GroupDisplayName {
    param(
        [string]$GroupObjectId
    )

    if ([string]::IsNullOrWhiteSpace($GroupObjectId)) {
        return $null
    }

    try {
        $group = Invoke-GraphGet -Uri ("https://graph.microsoft.com/v1.0/groups/{0}?`$select=id,displayName" -f $GroupObjectId)
        return $group.displayName
    }
    catch {
        return $null
    }
}

Ensure-Module -Name Microsoft.Graph.Authentication

$scopes = @(
    'Application.ReadWrite.All',
    'Directory.Read.All',
    'Group.Read.All'
)

if ($RequireAssignment -or
    -not [string]::IsNullOrWhiteSpace($ViewerGroupObjectId) -or
    -not [string]::IsNullOrWhiteSpace($ViewerGroupName) -or
    -not [string]::IsNullOrWhiteSpace($OperatorGroupObjectId) -or
    -not [string]::IsNullOrWhiteSpace($OperatorGroupName) -or
    -not [string]::IsNullOrWhiteSpace($AdminGroupObjectId) -or
    -not [string]::IsNullOrWhiteSpace($AdminGroupName)) {
    $scopes += 'AppRoleAssignment.ReadWrite.All'
    $scopes += 'Group.ReadWrite.All'
}

$context = Ensure-GraphConnection -TenantId $TenantId -Scopes $scopes
$null = Assert-UsableTenantContext -Context $context -RequestedTenantId $TenantId
$effectiveTenantId = if ([string]::IsNullOrWhiteSpace($TenantId)) { $context.TenantId } else { $TenantId }

if ([string]::IsNullOrWhiteSpace($ViewerGroupName)) {
    $ViewerGroupName = "$AppDisplayName Viewers"
}

if ([string]::IsNullOrWhiteSpace($OperatorGroupName)) {
    $OperatorGroupName = "$AppDisplayName Operators"
}

if ([string]::IsNullOrWhiteSpace($AdminGroupName)) {
    $AdminGroupName = "$AppDisplayName Admins"
}

if ([string]::IsNullOrWhiteSpace($EnvFilePath)) {
    $EnvFilePath = Join-Path $RepoRoot '.env.worktree'
}

$ApiPublicHost = Assert-UsablePublicHost -HostName $ApiPublicHost
$formattedApiPublicHost = Format-UrlHost -HostName $ApiPublicHost

$redirectUris = @(
    "https://$formattedApiPublicHost`:$ApiPort/signin-oidc",
    "https://$formattedApiPublicHost`:$ApiPort/signout-callback-oidc"
)

$application = Ensure-Application `
    -ApplicationObjectId $ApplicationObjectId `
    -ClientId $ClientId `
    -DisplayName $AppDisplayName `
    -RedirectUris $redirectUris
if ($null -eq $application) {
    Write-Warning 'No changes were applied because ShouldProcess declined the action.'
    return
}

$servicePrincipal = Ensure-ServicePrincipal -AppId $application.appId -RequireAssignment:$RequireAssignment.IsPresent
if ($null -eq $servicePrincipal) {
    Write-Warning 'The service principal was not created because ShouldProcess declined the action.'
    return
}

$viewerGroup = Ensure-Group -GroupObjectId $ViewerGroupObjectId -DisplayName $ViewerGroupName
$operatorGroup = Ensure-Group -GroupObjectId $OperatorGroupObjectId -DisplayName $OperatorGroupName
$adminGroup = Ensure-Group -GroupObjectId $AdminGroupObjectId -DisplayName $AdminGroupName

foreach ($group in @($viewerGroup, $operatorGroup, $adminGroup) | Where-Object { $null -ne $_ }) {
    Ensure-GroupAssignment -GroupObjectId $group.id -ServicePrincipalObjectId $servicePrincipal.id
}

$secret = $null
if (-not $SkipClientSecret) {
    $secret = New-ClientSecret -ApplicationObjectId $application.id -LifetimeMonths $ClientSecretMonths
}

$viewerGroupId = if ($null -ne $viewerGroup) { $viewerGroup.id } else { $ViewerGroupObjectId }
$operatorGroupId = if ($null -ne $operatorGroup) { $operatorGroup.id } else { $OperatorGroupObjectId }
$adminGroupId = if ($null -ne $adminGroup) { $adminGroup.id } else { $AdminGroupObjectId }

$viewerGroupNameResolved = if ($null -ne $viewerGroup) { $viewerGroup.displayName } else { Get-GroupDisplayName -GroupObjectId $ViewerGroupObjectId }
$operatorGroupNameResolved = if ($null -ne $operatorGroup) { $operatorGroup.displayName } else { Get-GroupDisplayName -GroupObjectId $OperatorGroupObjectId }
$adminGroupNameResolved = if ($null -ne $adminGroup) { $adminGroup.displayName } else { Get-GroupDisplayName -GroupObjectId $AdminGroupObjectId }

$envValues = @{
    'SYNCFACTORS_API_BIND_HOST' = $ApiBindHost
    'SYNCFACTORS_API_PUBLIC_HOST' = $ApiPublicHost
    'SYNCFACTORS_API_PORT' = [string]$ApiPort
    'SYNCFACTORS__AUTH__MODE' = $AuthMode
    'SYNCFACTORS__AUTH__LOCALBREAKGLASS__ENABLED' = if ([string]::Equals($AuthMode, 'hybrid', [System.StringComparison]::OrdinalIgnoreCase)) { 'true' } else { 'false' }
    'SYNCFACTORS__AUTH__OIDC__AUTHORITY' = "https://login.microsoftonline.com/$effectiveTenantId/v2.0"
    'SYNCFACTORS__AUTH__OIDC__CLIENTID' = [string]$application.appId
    'SYNCFACTORS__AUTH__OIDC__VIEWERGROUPS__0' = [string]$viewerGroupId
    'SYNCFACTORS__AUTH__OIDC__OPERATORGROUPS__0' = [string]$operatorGroupId
    'SYNCFACTORS__AUTH__OIDC__ADMINGROUPS__0' = [string]$adminGroupId
}

$secretStoreLabel = $null
$bootstrapSecretStoreLabel = $null

if ($null -ne $secret -and -not [string]::IsNullOrWhiteSpace([string]$secret.secretText)) {
    $secretStoreLabel = Store-SecretInLocalCredentialStore `
        -RepoRoot $RepoRoot `
        -EnvFilePath $EnvFilePath `
        -VariableName 'SYNCFACTORS__AUTH__OIDC__CLIENTSECRET' `
        -Value ([string]$secret.secretText)

    if ([string]::IsNullOrWhiteSpace($secretStoreLabel)) {
        $envValues['SYNCFACTORS__AUTH__OIDC__CLIENTSECRET'] = [string]$secret.secretText
    }
    else {
        $envValues['SYNCFACTORS__AUTH__OIDC__CLIENTSECRET'] = ''
    }
}

if ([string]::Equals($AuthMode, 'hybrid', [System.StringComparison]::OrdinalIgnoreCase)) {
    if ([string]::IsNullOrWhiteSpace($BootstrapAdminPassword)) {
        $BootstrapAdminPassword = New-GeneratedPassword
    }

    $envValues['SYNCFACTORS__AUTH__BOOTSTRAPADMIN__USERNAME'] = $BootstrapAdminUsername

    $bootstrapSecretStoreLabel = Store-SecretInLocalCredentialStore `
        -RepoRoot $RepoRoot `
        -EnvFilePath $EnvFilePath `
        -VariableName 'SYNCFACTORS__AUTH__BOOTSTRAPADMIN__PASSWORD' `
        -Value $BootstrapAdminPassword

    if ([string]::IsNullOrWhiteSpace($bootstrapSecretStoreLabel)) {
        $envValues['SYNCFACTORS__AUTH__BOOTSTRAPADMIN__PASSWORD'] = $BootstrapAdminPassword
    }
    else {
        $envValues['SYNCFACTORS__AUTH__BOOTSTRAPADMIN__PASSWORD'] = ''
    }
}
else {
    $envValues['SYNCFACTORS__AUTH__BOOTSTRAPADMIN__USERNAME'] = ''
    $envValues['SYNCFACTORS__AUTH__BOOTSTRAPADMIN__PASSWORD'] = ''
}

if ($PSCmdlet.ShouldProcess($EnvFilePath, 'Update SyncFactors OIDC environment config')) {
    Update-EnvFile -Path $EnvFilePath -Values $envValues
}

Write-Host ''
Write-Host 'Entra OIDC app registration is ready.' -ForegroundColor Green
Write-Host ''
Write-Host 'Registration summary' -ForegroundColor Cyan
Write-Host "TenantId:                 $effectiveTenantId"
Write-Host "ApplicationObjectId:      $($application.id)"
Write-Host "ClientId:                 $($application.appId)"
Write-Host "ServicePrincipalObjectId: $($servicePrincipal.id)"
Write-Host "DisplayName:              $($application.displayName)"
Write-Host "AuthMode:                 $AuthMode"
Write-Host "ApiBindHost:              $ApiBindHost"
Write-Host "ApiPublicHost:            $ApiPublicHost"
Write-Host "ApiPort:                  $ApiPort"
Write-Host "AssignmentRequired:       $($servicePrincipal.appRoleAssignmentRequired)"
Write-Host "GroupMembershipClaims:    $($application.groupMembershipClaims)"
Write-Host "RedirectSignIn:           $($redirectUris[0])"
Write-Host "RedirectSignOut:          $($redirectUris[1])"
Write-Host "EnvFileUpdated:           $EnvFilePath"
if (-not [string]::IsNullOrWhiteSpace($secretStoreLabel)) {
    Write-Host "OidcSecretStoredIn:       $secretStoreLabel"
}
if (-not [string]::IsNullOrWhiteSpace($bootstrapSecretStoreLabel)) {
    Write-Host "BootstrapSecretStoredIn:  $bootstrapSecretStoreLabel"
}
if ($null -ne $secret) {
    Write-Host "ClientSecretExpiresUtc:   $($secret.endDateTime)"
}

Write-Host ''
Write-Host 'Paste these into .env.worktree' -ForegroundColor Cyan
Write-Host "SYNCFACTORS_API_BIND_HOST=$ApiBindHost"
Write-Host "SYNCFACTORS_API_PUBLIC_HOST=$ApiPublicHost"
Write-Host "SYNCFACTORS_API_PORT=$ApiPort"
Write-Host "SYNCFACTORS__AUTH__MODE=$AuthMode"
Write-Host "SYNCFACTORS__AUTH__LOCALBREAKGLASS__ENABLED=$(if ([string]::Equals($AuthMode, 'hybrid', [System.StringComparison]::OrdinalIgnoreCase)) { 'true' } else { 'false' })"
Write-Host "SYNCFACTORS__AUTH__OIDC__AUTHORITY=https://login.microsoftonline.com/$effectiveTenantId/v2.0"
Write-Host "SYNCFACTORS__AUTH__OIDC__CLIENTID=$($application.appId)"
if ($null -ne $secret) {
    Write-Host "SYNCFACTORS__AUTH__OIDC__CLIENTSECRET=$($secret.secretText)"
}
else {
    Write-Host 'SYNCFACTORS__AUTH__OIDC__CLIENTSECRET=<existing-secret-not-printed>'
}

if (-not [string]::IsNullOrWhiteSpace($viewerGroupId)) {
    Write-Host "SYNCFACTORS__AUTH__OIDC__VIEWERGROUPS__0=$viewerGroupId"
}
else {
    Write-Host 'SYNCFACTORS__AUTH__OIDC__VIEWERGROUPS__0=<viewer-group-object-id>'
}

if (-not [string]::IsNullOrWhiteSpace($operatorGroupId)) {
    Write-Host "SYNCFACTORS__AUTH__OIDC__OPERATORGROUPS__0=$operatorGroupId"
}
else {
    Write-Host 'SYNCFACTORS__AUTH__OIDC__OPERATORGROUPS__0=<operator-group-object-id>'
}

if (-not [string]::IsNullOrWhiteSpace($adminGroupId)) {
    Write-Host "SYNCFACTORS__AUTH__OIDC__ADMINGROUPS__0=$adminGroupId"
}
else {
    Write-Host 'SYNCFACTORS__AUTH__OIDC__ADMINGROUPS__0=<admin-group-object-id>'
}

if ([string]::Equals($AuthMode, 'hybrid', [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "SYNCFACTORS__AUTH__BOOTSTRAPADMIN__USERNAME=$BootstrapAdminUsername"
    Write-Host "SYNCFACTORS__AUTH__BOOTSTRAPADMIN__PASSWORD=$BootstrapAdminPassword"
}

Write-Host ''
Write-Host 'Recommended next steps' -ForegroundColor Cyan
Write-Host '1. Store the client secret in your secret store instead of committing it to a file.'
Write-Host '2. If you use macOS Keychain, run:'
Write-Host '   ./scripts/codex/set-macos-keychain-secret.sh SYNCFACTORS__AUTH__OIDC__CLIENTSECRET'
Write-Host '3. Uncomment the OIDC block in .env.worktree and set the values printed above.'
Write-Host '4. Start the API over HTTPS on the same port used for the redirect URIs.'

if (-not [string]::IsNullOrWhiteSpace($viewerGroupId) -or
    -not [string]::IsNullOrWhiteSpace($operatorGroupId) -or
    -not [string]::IsNullOrWhiteSpace($adminGroupId)) {
    Write-Host ''
    Write-Host 'Resolved groups' -ForegroundColor Cyan
    if (-not [string]::IsNullOrWhiteSpace($viewerGroupId)) {
        Write-Host "Viewer:   $viewerGroupId$([string]::IsNullOrWhiteSpace($viewerGroupNameResolved) ? '' : " ($viewerGroupNameResolved)")"
    }
    if (-not [string]::IsNullOrWhiteSpace($operatorGroupId)) {
        Write-Host "Operator: $operatorGroupId$([string]::IsNullOrWhiteSpace($operatorGroupNameResolved) ? '' : " ($operatorGroupNameResolved)")"
    }
    if (-not [string]::IsNullOrWhiteSpace($adminGroupId)) {
        Write-Host "Admin:    $adminGroupId$([string]::IsNullOrWhiteSpace($adminGroupNameResolved) ? '' : " ($adminGroupNameResolved)")"
    }
}
