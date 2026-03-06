Set-StrictMode -Version Latest

$SfAdRollbackAttributeAllowList = @(
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

function Convert-SfAdValueForJson {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [System.Array]) {
        return @($Value | ForEach-Object { Convert-SfAdValueForJson -Value $_ })
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

function Get-SfAdParentOuFromDistinguishedName {
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

function Get-SfAdUserByObjectGuid {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ObjectGuid
    )

    Ensure-ActiveDirectoryModule
    return Get-ADUser -Identity $ObjectGuid -Properties * -ErrorAction SilentlyContinue
}

function Get-SfAdTargetUser {
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
    return Get-ADUser -LDAPFilter $ldapFilter -Properties * -ErrorAction SilentlyContinue
}

function Get-SfAdUserGroupMembershipDns {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$User
    )

    Ensure-ActiveDirectoryModule
    $groups = Get-ADPrincipalGroupMembership -Identity $User -ErrorAction SilentlyContinue
    if (-not $groups) {
        return @()
    }

    return @($groups | ForEach-Object { $_.DistinguishedName })
}

function Get-SfAdUserSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$User
    )

    $allProperties = @{}
    foreach ($property in $User.PSObject.Properties) {
        $allProperties[$property.Name] = Convert-SfAdValueForJson -Value $property.Value
    }

    $restoreAttributes = @{}
    foreach ($name in $SfAdRollbackAttributeAllowList) {
        $property = $User.PSObject.Properties[$name]
        if ($property) {
            $restoreAttributes[$name] = Convert-SfAdValueForJson -Value $property.Value
        }
    }

    return [pscustomobject]@{
        objectGuid = if ($User.ObjectGuid) { $User.ObjectGuid.Guid } else { $null }
        distinguishedName = $User.DistinguishedName
        parentOu = Get-SfAdParentOuFromDistinguishedName -DistinguishedName $User.DistinguishedName
        samAccountName = $User.SamAccountName
        userPrincipalName = $User.UserPrincipalName
        enabled = [bool]$User.Enabled
        restoreAttributes = [pscustomobject]$restoreAttributes
        groupMemberships = @(Get-SfAdUserGroupMembershipDns -User $User)
        rawProperties = [pscustomobject]$allProperties
    }
}

function Resolve-SfAdTargetOu {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config,
        [Parameter(Mandatory)]
        [pscustomobject]$Worker
    )

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

    return $Config.ad.defaultActiveOu
}

function New-SfAdUser {
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

    $targetOu = Resolve-SfAdTargetOu -Config $Config -Worker $Worker
    $samAccountName = if ($Attributes.ContainsKey('SamAccountName')) { $Attributes['SamAccountName'] } else { $WorkerId }

    $securePassword = ConvertTo-SecureString -String $Config.ad.defaultPassword -AsPlainText -Force
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

    foreach ($key in $Attributes.Keys) {
        if ($newUserParams.ContainsKey($key)) {
            continue
        }
        $newUserParams[$key] = $Attributes[$key]
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

    New-ADUser @newUserParams
    return Get-SfAdTargetUser -Config $Config -WorkerId $WorkerId
}

function Set-SfAdUserAttributes {
    [CmdletBinding(SupportsShouldProcess)]
    param(
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

    Set-ADUser -Identity $User -Replace $Changes
}

function Enable-SfAdUser {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$User,
        [switch]$DryRun
    )

    if ($DryRun) {
        return
    }

    Enable-ADAccount -Identity $User
}

function Disable-SfAdUser {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$User,
        [switch]$DryRun
    )

    if ($DryRun) {
        return
    }

    Disable-ADAccount -Identity $User
}

function Move-SfAdUser {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$User,
        [Parameter(Mandatory)]
        [string]$TargetOu,
        [switch]$DryRun
    )

    if ($DryRun) {
        return
    }

    Move-ADObject -Identity $User.DistinguishedName -TargetPath $TargetOu
}

function Remove-SfAdUser {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$User,
        [switch]$DryRun
    )

    if ($DryRun) {
        return
    }

    Remove-ADUser -Identity $User -Confirm:$false
}

function Add-SfAdUserToConfiguredGroups {
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
        $existingMemberships = @(Get-SfAdUserGroupMembershipDns -User $User)
    }

    $changes = @()
    foreach ($groupDn in $Config.ad.licensingGroups) {
        if (-not $DryRun -and $existingMemberships -contains $groupDn) {
            continue
        }

        $changes += $groupDn
        if (-not $DryRun) {
            Add-ADGroupMember -Identity $groupDn -Members $User -ErrorAction Stop
        }
    }

    return $changes
}

function Remove-SfAdUserFromGroups {
    [CmdletBinding()]
    param(
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
        Remove-ADGroupMember -Identity $groupDn -Members $User -Confirm:$false -ErrorAction Stop
    }
}

function Restore-SfAdUserFromSnapshot {
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
    $securePassword = ConvertTo-SecureString -String $Config.ad.defaultPassword -AsPlainText -Force

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
    New-ADUser @newUserParams
    $user = Get-ADUser -Identity $samAccountName -Properties * -ErrorAction Stop

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
        Set-ADUser -Identity $user -Replace $replaceAttributes
    }

    if ($Snapshot.enabled) {
        Enable-ADAccount -Identity $user
    }

    if ($Snapshot.groupMemberships) {
        foreach ($groupDn in @($Snapshot.groupMemberships)) {
            Add-ADGroupMember -Identity $groupDn -Members $user -ErrorAction Stop
        }
    }

    return Get-ADUser -Identity $user -Properties * -ErrorAction Stop
}

Export-ModuleMember -Function Ensure-ActiveDirectoryModule, Get-SfAdUserByObjectGuid, Get-SfAdTargetUser, Get-SfAdUserGroupMembershipDns, Get-SfAdUserSnapshot, Get-SfAdParentOuFromDistinguishedName, Resolve-SfAdTargetOu, New-SfAdUser, Set-SfAdUserAttributes, Enable-SfAdUser, Disable-SfAdUser, Move-SfAdUser, Remove-SfAdUser, Add-SfAdUserToConfiguredGroups, Remove-SfAdUserFromGroups, Restore-SfAdUserFromSnapshot
