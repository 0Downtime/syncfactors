Set-StrictMode -Version Latest

function Ensure-ActiveDirectoryModule {
    if (-not (Get-Module -ListAvailable -Name ActiveDirectory)) {
        throw "ActiveDirectory module not found. Install RSAT Active Directory tools."
    }

    if (-not (Get-Module -Name ActiveDirectory)) {
        Import-Module ActiveDirectory -ErrorAction Stop | Out-Null
    }
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

    if ($DryRun) {
        return [pscustomobject]@{
            Action        = 'Create'
            SamAccountName = $samAccountName
            TargetOu      = $targetOu
            Attributes    = $Attributes
        }
    }

    Ensure-ActiveDirectoryModule
    $securePassword = ConvertTo-SecureString -String $Config.ad.defaultPassword -AsPlainText -Force
    $newUserParams = @{
        Name               = "$($Worker.firstName) $($Worker.lastName)"
        SamAccountName     = $samAccountName
        UserPrincipalName  = $Attributes['UserPrincipalName']
        GivenName          = $Worker.firstName
        Surname            = $Worker.lastName
        Enabled            = $false
        Path               = $targetOu
        AccountPassword    = $securePassword
        ChangePasswordAtLogon = $true
    }

    foreach ($key in $Attributes.Keys) {
        if ($newUserParams.ContainsKey($key)) {
            continue
        }
        $newUserParams[$key] = $Attributes[$key]
    }

    New-ADUser @newUserParams
    return Get-SfAdTargetUser -Config $Config -WorkerId $WorkerId
}

function Set-SfAdUserAttributes {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [Microsoft.ActiveDirectory.Management.ADUser]$User,
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
        [Microsoft.ActiveDirectory.Management.ADUser]$User,
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
        [Microsoft.ActiveDirectory.Management.ADUser]$User,
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
        [Microsoft.ActiveDirectory.Management.ADUser]$User,
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
        [Microsoft.ActiveDirectory.Management.ADUser]$User,
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
        [Microsoft.ActiveDirectory.Management.ADUser]$User,
        [switch]$DryRun
    )

    if (-not $Config.ad.licensingGroups) {
        return @()
    }

    $changes = @()
    foreach ($groupDn in $Config.ad.licensingGroups) {
        $changes += $groupDn
        if (-not $DryRun) {
            Add-ADGroupMember -Identity $groupDn -Members $User -ErrorAction Stop
        }
    }

    return $changes
}

Export-ModuleMember -Function Ensure-ActiveDirectoryModule, Get-SfAdTargetUser, Resolve-SfAdTargetOu, New-SfAdUser, Set-SfAdUserAttributes, Enable-SfAdUser, Disable-SfAdUser, Move-SfAdUser, Remove-SfAdUser, Add-SfAdUserToConfiguredGroups
