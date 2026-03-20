Describe 'ActiveDirectorySync module' {
    BeforeAll {
        Import-Module "$PSScriptRoot/../src/Modules/SfAdSync/ActiveDirectorySync.psm1" -Force -DisableNameChecking
        function global:Add-ADGroupMember {}
        function global:Enable-ADAccount {}
        function global:Disable-ADAccount {}
        function global:Move-ADObject {}
        function global:Remove-ADUser {}
        function global:New-ADUser {}
        function global:Set-ADUser {}
        function global:Get-ADUser {}
    }

    BeforeEach {
        $global:AdTestConfig = [pscustomobject]@{
            ad = [pscustomobject]@{
                server = 'dc01.example.com'
                username = 'EXAMPLE\svc_sync'
                bindPassword = 'Password123!'
                identityAttribute = 'employeeID'
                defaultActiveOu = 'OU=Employees,DC=example,DC=com'
                graveyardOu = 'OU=Graveyard,DC=example,DC=com'
                defaultPassword = 'Password123!'
                licensingGroups = @(
                    'CN=LicenseA,OU=Groups,DC=example,DC=com',
                    'CN=LicenseB,OU=Groups,DC=example,DC=com'
                )
                ouRoutingRules = @(
                    [pscustomobject]@{
                        match = [pscustomobject]@{
                            department = 'IT'
                        }
                        targetOu = 'OU=IT,DC=example,DC=com'
                    }
                )
            }
        }
    }

    It 'builds AD directory context parameters from explicit bind settings' {
        InModuleScope ActiveDirectorySync {
            $context = Get-SfAdDirectoryContextParameters -Config $global:AdTestConfig

            $context.Server | Should -Be 'dc01.example.com'
            $context.Credential.UserName | Should -Be 'EXAMPLE\svc_sync'
        }
    }

    It 'routes workers to a matching OU and falls back to the default OU' {
        $itWorker = [pscustomobject]@{ department = 'IT' }
        $otherWorker = [pscustomobject]@{ department = 'HR' }

        (Resolve-SfAdTargetOu -Config $global:AdTestConfig -Worker $itWorker) | Should -Be 'OU=IT,DC=example,DC=com'
        (Resolve-SfAdTargetOu -Config $global:AdTestConfig -Worker $otherWorker) | Should -Be 'OU=Employees,DC=example,DC=com'
    }

    It 'returns a detailed dry-run payload for new user creation' {
        $worker = [pscustomobject]@{
            firstName = 'Jamie'
            lastName = 'Doe'
            department = 'IT'
        }

        $result = New-SfAdUser -Config $global:AdTestConfig -Worker $worker -WorkerId '1001' -Attributes @{
            UserPrincipalName = 'jamie.doe@example.com'
            title = 'Engineer'
        } -DryRun

        $result.Action | Should -Be 'Create'
        $result.SamAccountName | Should -Be '1001'
        $result.TargetOu | Should -Be 'OU=IT,DC=example,DC=com'
        $result.UserParameters.UserPrincipalName | Should -Be 'jamie.doe@example.com'
    }

    It 'passes non-native AD attributes through OtherAttributes on user creation' {
        $worker = [pscustomobject]@{
            firstName = 'Jamie'
            lastName = 'Doe'
            department = 'IT'
        }

        $result = New-SfAdUser -Config $global:AdTestConfig -Worker $worker -WorkerId '1001' -Attributes @{
            UserPrincipalName = 'jamie.doe@example.com'
            mail = 'jamie.doe@example.com'
            physicalDeliveryOfficeName = 'New York'
            department = 'IT'
            company = 'CORP'
        } -DryRun

        $result.UserParameters.ContainsKey('mail') | Should -BeFalse
        $result.UserParameters.ContainsKey('physicalDeliveryOfficeName') | Should -BeFalse
        $result.UserParameters.OtherAttributes['mail'] | Should -Be 'jamie.doe@example.com'
        $result.UserParameters.OtherAttributes['physicalDeliveryOfficeName'] | Should -Be 'New York'
        $result.UserParameters.OtherAttributes['department'] | Should -Be 'IT'
        $result.UserParameters.OtherAttributes['company'] | Should -Be 'CORP'
    }

    It 'returns dry-run metadata for attribute updates and restores' {
        $user = [pscustomobject]@{ SamAccountName = 'jdoe' }
        $update = Set-SfAdUserAttributes -Config $global:AdTestConfig -User $user -Changes @{ title = 'Lead' } -DryRun
        $snapshot = [pscustomobject]@{
            parentOu = 'OU=Employees,DC=example,DC=com'
            enabled = $true
            restoreAttributes = [pscustomobject]@{
                samAccountName = 'jdoe'
                userPrincipalName = 'jdoe@example.com'
                givenName = 'Jamie'
                sn = 'Doe'
                displayName = 'Jamie Doe'
                title = 'Director'
            }
            groupMemberships = @('CN=LicenseA,OU=Groups,DC=example,DC=com')
        }

        $restore = Restore-SfAdUserFromSnapshot -Config $global:AdTestConfig -Snapshot $snapshot -DryRun

        $update.Action | Should -Be 'Update'
        $update.Changes.title | Should -Be 'Lead'
        $restore.Action | Should -Be 'Restore'
        $restore.SamAccountName | Should -Be 'jdoe'
        $restore.Path | Should -Be 'OU=Employees,DC=example,DC=com'
    }

    It 'adds only missing configured groups when not in dry-run mode' {
        InModuleScope ActiveDirectorySync {
            $user = [pscustomobject]@{ SamAccountName = 'jdoe' }
            Mock Get-SfAdUserGroupMembershipDns { @('CN=LicenseA,OU=Groups,DC=example,DC=com') }
            Mock Add-ADGroupMember {} -ModuleName ActiveDirectorySync

            $groups = @(Add-SfAdUserToConfiguredGroups -Config $global:AdTestConfig -User $user)

            $groups | Should -Be @('CN=LicenseB,OU=Groups,DC=example,DC=com')
        }
    }

    It 'captures rollback snapshots with serializable values' {
        InModuleScope ActiveDirectorySync {
            $objectSid = if ($IsWindows) {
                New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-21-1000-1000-1000-1000'
            }
            else {
                'S-1-5-21-1000-1000-1000-1000'
            }

            $user = [pscustomobject]@{
                ObjectGuid = [guid]'11111111-1111-1111-1111-111111111111'
                DistinguishedName = 'CN=Jamie Doe,OU=Employees,DC=example,DC=com'
                SamAccountName = 'jdoe'
                UserPrincipalName = 'jdoe@example.com'
                Enabled = $true
                title = 'Engineer'
                objectSid = $objectSid
                whenCreated = [datetime]'2026-03-01T10:15:00'
            }

            Mock Get-SfAdUserGroupMembershipDns { @('CN=LicenseA,OU=Groups,DC=example,DC=com') }

            $snapshot = Get-SfAdUserSnapshot -Config $global:AdTestConfig -User $user

            $snapshot.objectGuid | Should -Be '11111111-1111-1111-1111-111111111111'
            $snapshot.parentOu | Should -Be 'OU=Employees,DC=example,DC=com'
            $snapshot.restoreAttributes.title | Should -Be 'Engineer'
            $snapshot.rawProperties.objectSid | Should -Be 'S-1-5-21-1000-1000-1000-1000'
            $snapshot.groupMemberships | Should -Be @('CN=LicenseA,OU=Groups,DC=example,DC=com')
        }
    }

    It 'delegates enable, disable, move, and remove operations to AD cmdlets' {
        InModuleScope ActiveDirectorySync {
            $user = [pscustomobject]@{
                SamAccountName = 'jdoe'
                DistinguishedName = 'CN=Jamie Doe,OU=Employees,DC=example,DC=com'
            }

            $script:EnabledIdentity = $null
            $script:DisabledIdentity = $null
            $script:MovedIdentity = $null
            $script:MovedTarget = $null
            $script:RemovedIdentity = $null
            Mock Enable-ADAccount {
                param($Identity)
                $script:EnabledIdentity = $Identity
            } -ModuleName ActiveDirectorySync
            Mock Disable-ADAccount {
                param($Identity)
                $script:DisabledIdentity = $Identity
            } -ModuleName ActiveDirectorySync
            Mock Move-ADObject {
                param($Identity, $TargetPath)
                $script:MovedIdentity = $Identity
                $script:MovedTarget = $TargetPath
            } -ModuleName ActiveDirectorySync
            Mock Remove-ADUser {
                param($Identity)
                $script:RemovedIdentity = $Identity
            } -ModuleName ActiveDirectorySync

            Enable-SfAdUser -Config $global:AdTestConfig -User $user
            Disable-SfAdUser -Config $global:AdTestConfig -User $user
            Move-SfAdUser -Config $global:AdTestConfig -User $user -TargetOu 'OU=Graveyard,DC=example,DC=com'
            Remove-SfAdUser -Config $global:AdTestConfig -User $user

            Assert-MockCalled Enable-ADAccount -Times 1 -Exactly -ModuleName ActiveDirectorySync
            Assert-MockCalled Disable-ADAccount -Times 1 -Exactly -ModuleName ActiveDirectorySync
            Assert-MockCalled Move-ADObject -Times 1 -Exactly -ModuleName ActiveDirectorySync
            Assert-MockCalled Remove-ADUser -Times 1 -Exactly -ModuleName ActiveDirectorySync
            $script:EnabledIdentity.SamAccountName | Should -Be 'jdoe'
            $script:DisabledIdentity.SamAccountName | Should -Be 'jdoe'
            $script:MovedIdentity | Should -Be 'CN=Jamie Doe,OU=Employees,DC=example,DC=com'
            $script:MovedTarget | Should -Be 'OU=Graveyard,DC=example,DC=com'
            $script:RemovedIdentity.SamAccountName | Should -Be 'jdoe'
        }
    }

    It 'returns unique managed sync OUs from the config' {
        InModuleScope ActiveDirectorySync {
            $ous = @(Get-SfAdManagedOus -Config $global:AdTestConfig)

            $ous | Should -Be @(
                'OU=Employees,DC=example,DC=com',
                'OU=Graveyard,DC=example,DC=com',
                'OU=IT,DC=example,DC=com'
            )
        }
    }

    It 'deduplicates users discovered across managed OUs' {
        InModuleScope ActiveDirectorySync {
            Mock Ensure-ActiveDirectoryModule {} -ModuleName ActiveDirectorySync
            Mock Get-ADUser {
                @(
                    [pscustomobject]@{
                        SamAccountName = 'jdoe'
                        DistinguishedName = 'CN=Jamie Doe,OU=Employees,DC=example,DC=com'
                    }
                )
            } -ModuleName ActiveDirectorySync

            $users = @(Get-SfAdUsersInOrganizationalUnits -Config $global:AdTestConfig -OrganizationalUnits @(
                    'OU=Employees,DC=example,DC=com',
                    'OU=IT,DC=example,DC=com'
                ))

            $users.Count | Should -Be 1
            $users[0].SamAccountName | Should -Be 'jdoe'
            Assert-MockCalled Get-ADUser -Times 2 -Exactly -ModuleName ActiveDirectorySync
        }
    }

    It 'restores a user snapshot by replaying AD attributes, state, and groups' {
        InModuleScope ActiveDirectorySync {
            $snapshot = [pscustomobject]@{
                parentOu = 'OU=Employees,DC=example,DC=com'
                enabled = $true
                restoreAttributes = [pscustomobject]@{
                    samAccountName = 'jdoe'
                    userPrincipalName = 'jdoe@example.com'
                    givenName = 'Jamie'
                    sn = 'Doe'
                    displayName = 'Jamie Doe'
                    title = 'Director'
                    manager = 'CN=Manager One,OU=Employees,DC=example,DC=com'
                }
                groupMemberships = @(
                    'CN=LicenseA,OU=Groups,DC=example,DC=com',
                    'CN=LicenseB,OU=Groups,DC=example,DC=com'
                )
            }
            $restoredUser = [pscustomobject]@{
                SamAccountName = 'jdoe'
                DistinguishedName = 'CN=Jamie Doe,OU=Employees,DC=example,DC=com'
            }

            $script:CreatedSamAccountName = $null
            $script:CreatedPath = $null
            $script:ReplacedAttributes = $null
            $script:EnabledRestoredIdentity = $null
            $script:AddedGroups = @()
            Mock Ensure-ActiveDirectoryModule {}
            Mock New-ADUser {
                param($SamAccountName, $Path)
                $script:CreatedSamAccountName = $SamAccountName
                $script:CreatedPath = $Path
            } -ModuleName ActiveDirectorySync
            Mock Get-ADUser { $restoredUser } -ModuleName ActiveDirectorySync
            Mock Set-ADUser {
                param($Identity, $Replace)
                $script:ReplacedAttributes = $Replace
            } -ModuleName ActiveDirectorySync
            Mock Enable-ADAccount {
                param($Identity)
                $script:EnabledRestoredIdentity = $Identity
            } -ModuleName ActiveDirectorySync
            Mock Add-ADGroupMember {
                param($Identity)
                $script:AddedGroups += $Identity
            } -ModuleName ActiveDirectorySync

            $result = Restore-SfAdUserFromSnapshot -Config $global:AdTestConfig -Snapshot $snapshot

            $result.SamAccountName | Should -Be 'jdoe'
            Assert-MockCalled New-ADUser -Times 1 -Exactly -ModuleName ActiveDirectorySync
            Assert-MockCalled Set-ADUser -Times 1 -Exactly -ModuleName ActiveDirectorySync
            Assert-MockCalled Enable-ADAccount -Times 1 -Exactly -ModuleName ActiveDirectorySync
            Assert-MockCalled Add-ADGroupMember -Times 2 -Exactly -ModuleName ActiveDirectorySync
            $script:CreatedSamAccountName | Should -Be 'jdoe'
            $script:CreatedPath | Should -Be 'OU=Employees,DC=example,DC=com'
            $script:ReplacedAttributes['title'] | Should -Be 'Director'
            $script:ReplacedAttributes['manager'] | Should -Be 'CN=Manager One,OU=Employees,DC=example,DC=com'
            $script:EnabledRestoredIdentity.SamAccountName | Should -Be 'jdoe'
            $script:AddedGroups | Should -Be @(
                'CN=LicenseA,OU=Groups,DC=example,DC=com',
                'CN=LicenseB,OU=Groups,DC=example,DC=com'
            )
        }
    }
}
