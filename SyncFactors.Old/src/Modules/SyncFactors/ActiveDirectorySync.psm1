Set-StrictMode -Version Latest

$SyncFactorsRollbackAttributeAllowList = @(
    'company',
    'department',
    'description',
    'displayName',
    'employeeID',
    'employeeNumber',
    'employeeType',
    'extensionAttribute1',
    'extensionAttribute2',
    'extensionAttribute3',
    'extensionAttribute4',
    'extensionAttribute5',
    'extensionAttribute6',
    'extensionAttribute7',
    'extensionAttribute8',
    'extensionAttribute9',
    'extensionAttribute10',
    'extensionAttribute11',
    'extensionAttribute12',
    'extensionAttribute13',
    'extensionAttribute14',
    'extensionAttribute15',
    'facsimileTelephoneNumber',
    'givenName',
    'homePhone',
    'initials',
    'l',
    'mail',
    'manager',
    'mobile',
    'pager',
    'physicalDeliveryOfficeName',
    'postalCode',
    'samAccountName',
    'sn',
    'st',
    'streetAddress',
    'telephoneNumber',
    'title',
    'division',
    'userPrincipalName',
    'wWWHomePage'
)

function Convert-SyncFactorsValueForJson {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [System.Array]) {
        return @($Value | ForEach-Object { Convert-SyncFactorsValueForJson -Value $_ })
    }

    if ($Value -is [datetime]) {
        return $Value.ToString('o')
    }

    if ($Value -is [System.Security.Principal.SecurityIdentifier]) {
        return $Value.Value
    }

    if ($Value -is [guid]) {
        return $Value.Guid
    }

    if ($Value -is [string] -or $Value -is [bool] -or $Value -is [int] -or $Value -is [long]) {
        return $Value
    }

    return "$Value"
}

function Get-SyncFactorsParentOuFromDistinguishedName {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$DistinguishedName
    )

    $parts = $DistinguishedName.Split(',', 2)
    if ($parts.Count -lt 2) {
        return $null
    }

    return $parts[1]
}

function Ensure-ActiveDirectoryModule {
    if (-not (Get-Module -ListAvailable -Name ActiveDirectory)) {
        throw "ActiveDirectory module not found. Install RSAT Active Directory tools."
    }

    if (-not (Get-Module -Name ActiveDirectory)) {
        Import-Module ActiveDirectory -ErrorAction Stop | Out-Null
    }
}

function Get-SyncFactorsDirectoryContextParameters {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [pscustomobject]$Config
    )

    $parameters = @{}
    if (-not $Config -or -not $Config.ad) {
        return $parameters
    }

    $server = if ($Config.ad.PSObject.Properties.Name -contains 'server') { "$($Config.ad.server)" } else { '' }
    $username = if ($Config.ad.PSObject.Properties.Name -contains 'username') { "$($Config.ad.username)" } else { '' }
    $bindPassword = if ($Config.ad.PSObject.Properties.Name -contains 'bindPassword') { "$($Config.ad.bindPassword)" } else { '' }

    if (-not [string]::IsNullOrWhiteSpace($server)) {
        $parameters['Server'] = $server
    }

    if (-not [string]::IsNullOrWhiteSpace($username)) {
        $securePassword = ConvertTo-SyncFactorsSecureString -Value $bindPassword
        $parameters['Credential'] = [pscredential]::new($username, $securePassword)
    }

    return $parameters
}

function ConvertTo-SyncFactorsSecureString {
    [System.Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        'PSAvoidUsingConvertToSecureStringWithPlainText',
        '',
        Justification = 'This module accepts secrets from environment-backed runtime configuration and must convert them before building credentials.'
    )]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Value
    )

    return ConvertTo-SecureString -String $Value -AsPlainText -Force
}

function Get-SyncFactorsUserByObjectGuid {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [pscustomobject]$Config,
        [Parameter(Mandatory)]
        [string]$ObjectGuid
    )

    Ensure-ActiveDirectoryModule
    $directoryContext = Get-SyncFactorsDirectoryContextParameters -Config $Config
    return Get-ADUser -Identity $ObjectGuid -Properties * -ErrorAction SilentlyContinue @directoryContext
}

function Get-SyncFactorsTargetUser {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config,
        [Parameter(Mandatory)]
        [string]$WorkerId
    )

    Ensure-ActiveDirectoryModule
    $attribute = $Config.ad.identityAttribute
    $ldapFilter = "($attribute=$WorkerId)"
    $directoryContext = Get-SyncFactorsDirectoryContextParameters -Config $Config
    return Get-ADUser -LDAPFilter $ldapFilter -Properties * -ErrorAction SilentlyContinue @directoryContext
}

function Get-SyncFactorsUserBySamAccountName {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [pscustomobject]$Config,
        [Parameter(Mandatory)]
        [string]$SamAccountName
    )

    Ensure-ActiveDirectoryModule
    $directoryContext = Get-SyncFactorsDirectoryContextParameters -Config $Config
    return Get-ADUser -LDAPFilter "(samAccountName=$SamAccountName)" -Properties * -ErrorAction SilentlyContinue @directoryContext
}

function Get-SyncFactorsUserByUserPrincipalName {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [pscustomobject]$Config,
        [Parameter(Mandatory)]
        [string]$UserPrincipalName
    )

    Ensure-ActiveDirectoryModule
    $directoryContext = Get-SyncFactorsDirectoryContextParameters -Config $Config
    return Get-ADUser -LDAPFilter "(userPrincipalName=$UserPrincipalName)" -Properties * -ErrorAction SilentlyContinue @directoryContext
}

function Get-SyncFactorsUserGroupMembershipDns {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [pscustomobject]$Config,
        [Parameter(Mandatory)]
        [pscustomobject]$User
    )

    Ensure-ActiveDirectoryModule
    $directoryContext = Get-SyncFactorsDirectoryContextParameters -Config $Config
    $groups = Get-ADPrincipalGroupMembership -Identity $User -ErrorAction SilentlyContinue @directoryContext
    if (-not $groups) {
        return @()
    }

    return @($groups | ForEach-Object { $_.DistinguishedName })
}

function Get-SyncFactorsUserSnapshot {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [pscustomobject]$Config,
        [Parameter(Mandatory)]
        [pscustomobject]$User
    )

    $allProperties = @{}
    foreach ($property in $User.PSObject.Properties) {
        $allProperties[$property.Name] = Convert-SyncFactorsValueForJson -Value $property.Value
    }

    $restoreAttributes = @{}
    foreach ($name in $SyncFactorsRollbackAttributeAllowList) {
        $property = $User.PSObject.Properties[$name]
        if ($property) {
            $restoreAttributes[$name] = Convert-SyncFactorsValueForJson -Value $property.Value
        }
    }

    $userPrincipalName = $null
    $upnProperty = $User.PSObject.Properties['UserPrincipalName']
    if ($upnProperty) {
        $userPrincipalName = $upnProperty.Value
    }

    return [pscustomobject]@{
        objectGuid = if ($User.ObjectGuid) { $User.ObjectGuid.Guid } else { $null }
        distinguishedName = $User.DistinguishedName
        parentOu = Get-SyncFactorsParentOuFromDistinguishedName -DistinguishedName $User.DistinguishedName
        samAccountName = $User.SamAccountName
        userPrincipalName = $userPrincipalName
        enabled = [bool]$User.Enabled
        restoreAttributes = [pscustomobject]$restoreAttributes
        groupMemberships = @(Get-SyncFactorsUserGroupMembershipDns -Config $Config -User $User)
        rawProperties = [pscustomobject]$allProperties
    }
}

function Resolve-SyncFactorsTargetOu {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config,
        [Parameter(Mandatory)]
        [pscustomobject]$Worker
    )

    if ($Config.ad.PSObject.Properties.Name -contains 'ouRoutingRules' -and $Config.ad.ouRoutingRules) {
        foreach ($rule in $Config.ad.ouRoutingRules) {
            $allMatch = $true
            foreach ($property in $rule.match.PSObject.Properties) {
                $workerValue = $Worker.($property.Name)
                if ("$workerValue" -ne "$($property.Value)") {
                    $allMatch = $false
                    break
                }
            }

            if ($allMatch) {
                return $rule.targetOu
            }
        }
    }

    return $Config.ad.defaultActiveOu
}

function New-SyncFactorsUser {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config,
        [Parameter(Mandatory)]
        [pscustomobject]$Worker,
        [Parameter(Mandatory)]
        [string]$WorkerId,
        [Parameter(Mandatory)]
        [hashtable]$Attributes,
        [switch]$DryRun
    )

    $targetOu = Resolve-SyncFactorsTargetOu -Config $Config -Worker $Worker
    $samAccountName = if ($Attributes.ContainsKey('SamAccountName')) { $Attributes['SamAccountName'] } else { $WorkerId }

    $securePassword = ConvertTo-SyncFactorsSecureString -Value $Config.ad.defaultPassword
    $newUserParams = @{
        Name                  = "$($Worker.firstName) $($Worker.lastName)"
        SamAccountName        = $samAccountName
        UserPrincipalName     = $Attributes['UserPrincipalName']
        GivenName             = $Worker.firstName
        Surname               = $Worker.lastName
        Enabled               = $false
        Path                  = $targetOu
        AccountPassword       = $securePassword
        ChangePasswordAtLogon = $true
    }

    $otherAttributes = @{}

    foreach ($key in $Attributes.Keys) {
        if ($newUserParams.ContainsKey($key)) {
            continue
        }

        if ([string]::IsNullOrWhiteSpace("$($Attributes[$key])")) {
            continue
        }

        $otherAttributes[$key] = $Attributes[$key]
    }

    if ($otherAttributes.Count -gt 0) {
        $newUserParams['OtherAttributes'] = $otherAttributes
    }

    if ($DryRun) {
        return [pscustomobject]@{
            Action = 'Create'
            SamAccountName = $samAccountName
            TargetOu = $targetOu
            Attributes = $Attributes
            UserParameters = $newUserParams
        }
    }

    Ensure-ActiveDirectoryModule

    $directoryContext = Get-SyncFactorsDirectoryContextParameters -Config $Config
    New-ADUser @newUserParams @directoryContext
    return Get-SyncFactorsTargetUser -Config $Config -WorkerId $WorkerId
}

function Set-SyncFactorsUserAttributes {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [AllowNull()]
        [pscustomobject]$Config,
        [Parameter(Mandatory)]
        [pscustomobject]$User,
        [Parameter(Mandatory)]
        [hashtable]$Changes,
        [switch]$DryRun
    )

    if ($DryRun) {
        return [pscustomobject]@{
            Action = 'Update'
            User   = $User.SamAccountName
            Changes = $Changes
        }
    }

    if ($Changes.Count -eq 0) {
        return $null
    }

    $directoryContext = Get-SyncFactorsDirectoryContextParameters -Config $Config
    Set-ADUser -Identity $User -Replace $Changes @directoryContext
}

function Enable-SyncFactorsUser {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [pscustomobject]$Config,
        [Parameter(Mandatory)]
        [pscustomobject]$User,
        [switch]$DryRun
    )

    if ($DryRun) {
        return
    }

    $directoryContext = Get-SyncFactorsDirectoryContextParameters -Config $Config
    Enable-ADAccount -Identity $User @directoryContext
}

function Disable-SyncFactorsUser {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [pscustomobject]$Config,
        [Parameter(Mandatory)]
        [pscustomobject]$User,
        [switch]$DryRun
    )

    if ($DryRun) {
        return
    }

    $directoryContext = Get-SyncFactorsDirectoryContextParameters -Config $Config
    Disable-ADAccount -Identity $User @directoryContext
}

function Move-SyncFactorsUser {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [pscustomobject]$Config,
        [Parameter(Mandatory)]
        [pscustomobject]$User,
        [Parameter(Mandatory)]
        [string]$TargetOu,
        [switch]$DryRun
    )

    if ($DryRun) {
        return
    }

    $directoryContext = Get-SyncFactorsDirectoryContextParameters -Config $Config
    Move-ADObject -Identity $User.DistinguishedName -TargetPath $TargetOu @directoryContext
}

function Remove-SyncFactorsUser {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [pscustomobject]$Config,
        [Parameter(Mandatory)]
        [pscustomobject]$User,
        [switch]$DryRun
    )

    if ($DryRun) {
        return
    }

    $directoryContext = Get-SyncFactorsDirectoryContextParameters -Config $Config
    Remove-ADUser -Identity $User -Confirm:$false @directoryContext
}

function Get-SyncFactorsManagedOus {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config
    )

    $ous = [System.Collections.Generic.List[string]]::new()
    foreach ($candidate in @(
            $Config.ad.defaultActiveOu,
            $Config.ad.graveyardOu,
            @($Config.ad.ouRoutingRules | ForEach-Object { $_.targetOu })
        )) {
        if ([string]::IsNullOrWhiteSpace("$candidate")) {
            continue
        }

        if (-not $ous.Contains("$candidate")) {
            $ous.Add("$candidate")
        }
    }

    return @($ous)
}

function Get-SyncFactorsUsersInOrganizationalUnits {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config,
        [Parameter(Mandatory)]
        [string[]]$OrganizationalUnits
    )

    Ensure-ActiveDirectoryModule
    $directoryContext = Get-SyncFactorsDirectoryContextParameters -Config $Config
    $seenDns = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $results = [System.Collections.Generic.List[object]]::new()

    foreach ($ou in @($OrganizationalUnits | Where-Object { -not [string]::IsNullOrWhiteSpace("$_") })) {
        $ouUsers = @()
        try {
            $ouUsers = @(Get-ADUser -LDAPFilter '(objectClass=user)' -SearchBase $ou -SearchScope Subtree -Properties * -ErrorAction Stop @directoryContext)
        } catch {
            Write-Warning "Skipping OU '$ou' during AD user discovery: $($_.Exception.Message)"
            continue
        }

        foreach ($user in $ouUsers) {
            $distinguishedName = if ($user.PSObject.Properties.Name -contains 'DistinguishedName') { "$($user.DistinguishedName)" } else { '' }
            $dedupeKey = if (-not [string]::IsNullOrWhiteSpace($distinguishedName)) { $distinguishedName } else { "$($user.SamAccountName)" }
            if ($seenDns.Add($dedupeKey)) {
                $results.Add($user)
            }
        }
    }

    return @($results)
}

function Add-SyncFactorsUserToConfiguredGroups {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config,
        [Parameter(Mandatory)]
        [pscustomobject]$User,
        [switch]$DryRun
    )

    if (-not $Config.ad.licensingGroups) {
        return @()
    }

    $existingMemberships = @()
    if (-not $DryRun) {
        $existingMemberships = @(Get-SyncFactorsUserGroupMembershipDns -Config $Config -User $User)
    }

    $changes = @()
    foreach ($groupDn in $Config.ad.licensingGroups) {
        if (-not $DryRun -and $existingMemberships -contains $groupDn) {
            continue
        }

        $changes += $groupDn
        if (-not $DryRun) {
            $directoryContext = Get-SyncFactorsDirectoryContextParameters -Config $Config
            Add-ADGroupMember -Identity $groupDn -Members $User -ErrorAction Stop @directoryContext
        }
    }

    return $changes
}

function Remove-SyncFactorsUserFromGroups {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [pscustomobject]$Config,
        [Parameter(Mandatory)]
        [pscustomobject]$User,
        [Parameter(Mandatory)]
        [string[]]$Groups,
        [switch]$DryRun
    )

    if ($DryRun) {
        return
    }

    foreach ($groupDn in $Groups) {
        $directoryContext = Get-SyncFactorsDirectoryContextParameters -Config $Config
        Remove-ADGroupMember -Identity $groupDn -Members $User -Confirm:$false -ErrorAction Stop @directoryContext
    }
}

function Restore-SyncFactorsUserFromSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config,
        [Parameter(Mandatory)]
        [pscustomobject]$Snapshot,
        [switch]$DryRun
    )

    $restoreAttributes = @{}
    if ($Snapshot.restoreAttributes) {
        foreach ($property in $Snapshot.restoreAttributes.PSObject.Properties) {
            $restoreAttributes[$property.Name] = $property.Value
        }
    }

    $samAccountName = $restoreAttributes['samAccountName']
    $userPrincipalName = $restoreAttributes['userPrincipalName']
    $givenName = $restoreAttributes['givenName']
    $surname = $restoreAttributes['sn']
    $displayName = if ($restoreAttributes.ContainsKey('displayName')) { $restoreAttributes['displayName'] } else { "$givenName $surname".Trim() }
    $path = if ($Snapshot.parentOu) { $Snapshot.parentOu } else { $Config.ad.defaultActiveOu }
    $securePassword = ConvertTo-SyncFactorsSecureString -Value $Config.ad.defaultPassword

    $newUserParams = @{
        Name = $displayName
        SamAccountName = $samAccountName
        UserPrincipalName = $userPrincipalName
        GivenName = $givenName
        Surname = $surname
        DisplayName = $displayName
        Enabled = $false
        Path = $path
        AccountPassword = $securePassword
        ChangePasswordAtLogon = $false
    }

    if ($DryRun) {
        return [pscustomobject]@{
            Action = 'Restore'
            SamAccountName = $samAccountName
            Path = $path
        }
    }

    Ensure-ActiveDirectoryModule
    $directoryContext = Get-SyncFactorsDirectoryContextParameters -Config $Config
    New-ADUser @newUserParams @directoryContext
    $user = Get-ADUser -Identity $samAccountName -Properties * -ErrorAction Stop @directoryContext

    $replaceAttributes = @{}
    foreach ($key in $restoreAttributes.Keys) {
        if ($key -in @('samAccountName', 'userPrincipalName', 'givenName', 'sn', 'displayName')) {
            continue
        }

        if ($null -eq $restoreAttributes[$key] -or "$($restoreAttributes[$key])" -eq '') {
            continue
        }

        $replaceAttributes[$key] = $restoreAttributes[$key]
    }

    if ($replaceAttributes.Count -gt 0) {
        Set-ADUser -Identity $user -Replace $replaceAttributes @directoryContext
    }

    if ($Snapshot.enabled) {
        Enable-ADAccount -Identity $user @directoryContext
    }

    if ($Snapshot.groupMemberships) {
        foreach ($groupDn in @($Snapshot.groupMemberships)) {
            Add-ADGroupMember -Identity $groupDn -Members $user -ErrorAction Stop @directoryContext
        }
    }

    return Get-ADUser -Identity $user -Properties * -ErrorAction Stop @directoryContext
}

Export-ModuleMember -Function Ensure-ActiveDirectoryModule, Get-SyncFactorsUserByObjectGuid, Get-SyncFactorsTargetUser, Get-SyncFactorsUserBySamAccountName, Get-SyncFactorsUserByUserPrincipalName, Get-SyncFactorsUserGroupMembershipDns, Get-SyncFactorsUserSnapshot, Get-SyncFactorsParentOuFromDistinguishedName, Resolve-SyncFactorsTargetOu, New-SyncFactorsUser, Set-SyncFactorsUserAttributes, Enable-SyncFactorsUser, Disable-SyncFactorsUser, Move-SyncFactorsUser, Remove-SyncFactorsUser, Get-SyncFactorsManagedOus, Get-SyncFactorsUsersInOrganizationalUnits, Add-SyncFactorsUserToConfiguredGroups, Remove-SyncFactorsUserFromGroups, Restore-SyncFactorsUserFromSnapshot
