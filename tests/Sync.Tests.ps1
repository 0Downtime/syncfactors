Describe 'Invoke-SfAdSyncRun' {
    BeforeAll {
        Import-Module "$PSScriptRoot/../src/Modules/SfAdSync/Sync.psm1" -Force
    }

    BeforeEach {
        $global:CapturedReport = $null
        $global:SavedStatePath = $null
        $configFile = Join-Path $TestDrive 'sync-config.json'
        $mappingFile = Join-Path $TestDrive 'mapping-config.json'
        Set-Content -Path $configFile -Value '{}'
        Set-Content -Path $mappingFile -Value '{}'

        $global:SyncTestConfigPath = $configFile
        $global:SyncTestMappingConfigPath = $mappingFile
        $global:SyncTestBaseConfig = [pscustomobject]@{
            successFactors = [pscustomobject]@{
                query = [pscustomobject]@{
                    identityField = 'personIdExternal'
                }
            }
            ad = [pscustomobject]@{
                identityAttribute = 'employeeID'
                graveyardOu = 'OU=Graveyard,DC=example,DC=com'
                defaultActiveOu = 'OU=Employees,DC=example,DC=com'
                licensingGroups = @('CN=License,OU=Groups,DC=example,DC=com')
            }
            sync = [pscustomobject]@{
                enableBeforeStartDays = 7
                deletionRetentionDays = 90
            }
            state = [pscustomobject]@{
                path = (Join-Path $TestDrive 'state.json')
            }
            reporting = [pscustomobject]@{
                outputDirectory = (Join-Path $TestDrive 'reports')
            }
        }
    }

    It 'records a reversible create flow for an active prehire' {
        InModuleScope Sync {
            Mock Get-SfAdSyncConfig { $global:SyncTestBaseConfig }
            Mock Get-SfAdSyncMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SfAdSyncState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
            Mock Get-SfWorkers {
                @(
                    [pscustomobject]@{
                        personIdExternal = '1001'
                        employeeId = '1001'
                        firstName = 'Jamie'
                        lastName = 'Doe'
                        status = 'active'
                        startDate = (Get-Date).ToString('o')
                        managerEmployeeId = $null
                    }
                )
            }
            Mock Get-SfAdTargetUser { $null }
            Mock Get-SfAdWorkerState { $null }
            Mock Get-SfAdAttributeChanges {
                [pscustomobject]@{
                    Changes = @{
                        UserPrincipalName = 'jamie.doe@example.com'
                        title = 'Engineer'
                    }
                    MissingRequired = @()
                }
            }
            Mock New-SfAdUser {
                [pscustomobject]@{
                    ObjectGuid = [guid]'11111111-1111-1111-1111-111111111111'
                    DistinguishedName = 'CN=Jamie Doe,OU=Employees,DC=example,DC=com'
                    SamAccountName = '1001'
                    Enabled = $false
                }
            }
            Mock Enable-SfAdUser {}
            Mock Add-SfAdUserToConfiguredGroups { @('CN=License,OU=Groups,DC=example,DC=com') }
            Mock Set-SfAdWorkerState {
                param($State, $WorkerId, $WorkerState)
                $State.workers | Add-Member -MemberType NoteProperty -Name $WorkerId -Value $WorkerState -Force
            }
            Mock Save-SfAdSyncState { param($State, $Path) $global:SavedStatePath = $Path }
            Mock Save-SfAdSyncReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "sf-ad-sync-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SfAdSyncRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.creates.Count | Should -Be 1
            $global:CapturedReport.enables.Count | Should -Be 1
            @($global:CapturedReport.operations.operationType) | Should -Contain 'CreateUser'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'EnableUser'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'AddGroupMembership'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'SetWorkerState'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'SetCheckpoint'
            $global:SavedStatePath | Should -Be $global:SyncTestBaseConfig.state.path
        }
    }

    It 'records disable and move operations for offboarding' {
        InModuleScope Sync {
            $user = [pscustomobject]@{
                ObjectGuid = [guid]'22222222-2222-2222-2222-222222222222'
                DistinguishedName = 'CN=Alex Doe,OU=Employees,DC=example,DC=com'
                SamAccountName = 'adoe'
                Enabled = $true
            }

            Mock Get-SfAdSyncConfig { $global:SyncTestBaseConfig }
            Mock Get-SfAdSyncMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SfAdSyncState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
            Mock Get-SfWorkers {
                @(
                    [pscustomobject]@{
                        personIdExternal = '2001'
                        employeeId = '2001'
                        status = 'inactive'
                        startDate = (Get-Date).AddDays(-30).ToString('o')
                    }
                )
            }
            Mock Get-SfAdTargetUser { $user }
            Mock Get-SfAdWorkerState { $null }
            Mock Disable-SfAdUser {}
            Mock Get-SfAdUserByObjectGuid { $user }
            Mock Move-SfAdUser {}
            Mock Set-SfAdWorkerState {
                param($State, $WorkerId, $WorkerState)
                $State.workers | Add-Member -MemberType NoteProperty -Name $WorkerId -Value $WorkerState -Force
            }
            Mock Save-SfAdSyncState {}
            Mock Save-SfAdSyncReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "sf-ad-sync-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SfAdSyncRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.disables.Count | Should -Be 1
            $global:CapturedReport.graveyardMoves.Count | Should -Be 1
            @($global:CapturedReport.operations.operationType) | Should -Contain 'DisableUser'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'MoveUser'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'SetWorkerState'
        }
    }
}
