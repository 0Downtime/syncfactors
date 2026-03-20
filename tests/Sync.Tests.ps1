Describe 'Invoke-SfAdSyncRun' {
    BeforeAll {
        Import-Module "$PSScriptRoot/../src/Modules/SfAdSync/Sync.psm1" -Force -DisableNameChecking

        function New-SyncTestBaseConfig {
            param(
                [Parameter(Mandatory)]
                [string]$RootPath
            )

            return [pscustomobject]@{
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
                safety = [pscustomobject]@{
                    maxCreatesPerRun = 5
                    maxDisablesPerRun = 5
                    maxDeletionsPerRun = 5
                }
                state = [pscustomobject]@{
                    path = (Join-Path $RootPath 'state.json')
                }
                reporting = [pscustomobject]@{
                    outputDirectory = (Join-Path $RootPath 'reports')
                }
            }
        }
    }

    BeforeEach {
        $global:CapturedReport = $null
        $global:SavedStatePath = $null
        $global:RuntimeSnapshots = @()
        $configFile = Join-Path $TestDrive 'sync-config.json'
        $mappingFile = Join-Path $TestDrive 'mapping-config.json'
        Set-Content -Path $configFile -Value '{}'
        Set-Content -Path $mappingFile -Value '{}'

        $global:SyncTestConfigPath = $configFile
        $global:SyncTestMappingConfigPath = $mappingFile
        $global:SyncTestBaseConfig = New-SyncTestBaseConfig -RootPath $TestDrive
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
            Mock Get-SfAdUserBySamAccountName { $null }
            Mock Get-SfAdUserByUserPrincipalName { $null }
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
            Mock Write-SfAdRuntimeStatusSnapshot {
                param($Report, $StatePath, $Stage, $Status, $ProcessedWorkers, $TotalWorkers, $CurrentWorkerId, $LastAction, $CompletedAt, $ErrorMessage)
                $global:RuntimeSnapshots += [pscustomobject]@{
                    Stage = $Stage
                    Status = $Status
                    ProcessedWorkers = $ProcessedWorkers
                    TotalWorkers = $TotalWorkers
                    CurrentWorkerId = $CurrentWorkerId
                    LastAction = $LastAction
                    CompletedAt = $CompletedAt
                    ErrorMessage = $ErrorMessage
                }
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SfAdSyncRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.creates.Count | Should -Be 1
            $global:CapturedReport.enables.Count | Should -Be 1
            $global:CapturedReport.status | Should -Be 'Succeeded'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'CreateUser'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'EnableUser'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'AddGroupMembership'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'SetWorkerState'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'SetCheckpoint'
            $global:SavedStatePath | Should -Be $global:SyncTestBaseConfig.state.path
            Assert-MockCalled New-SfAdUser -Times 1 -Exactly -ParameterFilter {
                $WorkerId -eq '1001' -and
                $Attributes['UserPrincipalName'] -eq 'jamie.doe@example.com' -and
                $Attributes['title'] -eq 'Engineer' -and
                $Attributes['employeeID'] -eq '1001'
            }
            Assert-MockCalled Enable-SfAdUser -Times 1 -Exactly -ParameterFilter {
                $User.SamAccountName -eq '1001'
            }
            Assert-MockCalled Add-SfAdUserToConfiguredGroups -Times 1 -Exactly -ParameterFilter {
                $User.SamAccountName -eq '1001'
            }
            @($global:RuntimeSnapshots.Stage) | Should -Contain 'FetchingWorkers'
            @($global:RuntimeSnapshots.Stage) | Should -Contain 'ProcessingWorkers'
            @($global:RuntimeSnapshots.Stage) | Should -Contain 'SavingState'
            @($global:RuntimeSnapshots.Stage) | Should -Contain 'WritingReport'
            $global:RuntimeSnapshots[-1].Stage | Should -Be 'Completed'
            $global:RuntimeSnapshots[-1].Status | Should -Be 'Succeeded'
            $global:RuntimeSnapshots[-1].ProcessedWorkers | Should -Be 1
            $global:RuntimeSnapshots[-1].TotalWorkers | Should -Be 1
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
            $global:CapturedReport.status | Should -Be 'Succeeded'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'DisableUser'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'MoveUser'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'SetWorkerState'
            Assert-MockCalled Disable-SfAdUser -Times 1 -Exactly -ParameterFilter {
                $User.SamAccountName -eq 'adoe'
            }
            Assert-MockCalled Move-SfAdUser -Times 1 -Exactly -ParameterFilter {
                $User.SamAccountName -eq 'adoe' -and
                $TargetOu -eq 'OU=Graveyard,DC=example,DC=com'
            }
        }
    }

    It 'fails the run when create threshold is exceeded' {
        InModuleScope Sync {
            $global:SyncTestBaseConfig.safety.maxCreatesPerRun = 0

            Mock Get-SfAdSyncConfig { $global:SyncTestBaseConfig }
            Mock Get-SfAdSyncMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SfAdSyncState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
            Mock Get-SfWorkers {
                @(
                    [pscustomobject]@{
                        personIdExternal = '3001'
                        employeeId = '3001'
                        firstName = 'Robin'
                        lastName = 'Smith'
                        status = 'active'
                        startDate = (Get-Date).ToString('o')
                    }
                )
            }
            Mock Get-SfAdTargetUser { $null }
            Mock Get-SfAdUserBySamAccountName { $null }
            Mock Get-SfAdUserByUserPrincipalName { $null }
            Mock Get-SfAdWorkerState { $null }
            Mock Get-SfAdAttributeChanges {
                [pscustomobject]@{
                    Changes = @{ UserPrincipalName = 'robin.smith@example.com' }
                    MissingRequired = @()
                }
            }
            Mock New-SfAdUser { throw 'should not create user' }
            Mock Save-SfAdSyncReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "sf-ad-sync-$Mode.json")
            }
            Mock Write-SfAdRuntimeStatusSnapshot {
                param($Report, $StatePath, $Stage, $Status, $ProcessedWorkers, $TotalWorkers, $CurrentWorkerId, $LastAction, $CompletedAt, $ErrorMessage)
                $global:RuntimeSnapshots += [pscustomobject]@{
                    Stage = $Stage
                    Status = $Status
                    ErrorMessage = $ErrorMessage
                }
            }

            { Invoke-SfAdSyncRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null } | Should -Throw '*maxCreatesPerRun*'

            $global:CapturedReport.status | Should -Be 'Failed'
            $global:CapturedReport.guardrailFailures.Count | Should -Be 1
            $global:CapturedReport.guardrailFailures[0].threshold | Should -Be 'maxCreatesPerRun'
            $global:RuntimeSnapshots[-1].Stage | Should -Be 'Failed'
            $global:RuntimeSnapshots[-1].Status | Should -Be 'Failed'
            $global:RuntimeSnapshots[-1].ErrorMessage | Should -Be "Safety threshold 'maxCreatesPerRun' exceeded."
        }
    }

    It 'quarantines duplicate worker identities as conflicts' {
        InModuleScope Sync {
            Mock Get-SfAdSyncConfig { $global:SyncTestBaseConfig }
            Mock Get-SfAdSyncMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SfAdSyncState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
            Mock Get-SfWorkers {
                @(
                    [pscustomobject]@{ personIdExternal = '4001'; employeeId = '4001'; status = 'active'; startDate = (Get-Date).ToString('o') },
                    [pscustomobject]@{ personIdExternal = '4001'; employeeId = '4001'; status = 'active'; startDate = (Get-Date).ToString('o') }
                )
            }
            Mock Save-SfAdSyncState {}
            Mock Save-SfAdSyncReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "sf-ad-sync-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}
            Mock New-SfAdUser {}

            Invoke-SfAdSyncRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.status | Should -Be 'Succeeded'
            $global:CapturedReport.conflicts.Count | Should -Be 2
            @($global:CapturedReport.conflicts.reason | Select-Object -Unique) | Should -Be @('DuplicateWorkerId')
            Assert-MockCalled New-SfAdUser -Times 0 -Exactly
        }
    }

    It 'blocks creates when the target UPN already exists' {
        InModuleScope Sync {
            Mock Get-SfAdSyncConfig { $global:SyncTestBaseConfig }
            Mock Get-SfAdSyncMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SfAdSyncState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
            Mock Get-SfWorkers {
                @(
                    [pscustomobject]@{
                        personIdExternal = '5001'
                        employeeId = '5001'
                        firstName = 'Taylor'
                        lastName = 'Jones'
                        status = 'active'
                        startDate = (Get-Date).ToString('o')
                    }
                )
            }
            Mock Get-SfAdTargetUser { $null }
            Mock Get-SfAdUserBySamAccountName { $null }
            Mock Get-SfAdUserByUserPrincipalName {
                [pscustomobject]@{ SamAccountName = 'tjones' }
            }
            Mock Get-SfAdWorkerState { $null }
            Mock Get-SfAdAttributeChanges {
                [pscustomobject]@{
                    Changes = @{ UserPrincipalName = 'taylor.jones@example.com' }
                    MissingRequired = @()
                }
            }
            Mock New-SfAdUser {}
            Mock Save-SfAdSyncState {}
            Mock Save-SfAdSyncReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "sf-ad-sync-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SfAdSyncRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.status | Should -Be 'Succeeded'
            $global:CapturedReport.conflicts.Count | Should -Be 1
            $global:CapturedReport.conflicts[0].reason | Should -Be 'UserPrincipalNameCollision'
            Assert-MockCalled New-SfAdUser -Times 0 -Exactly
        }
    }

    It 'updates attributes and moves an existing active user to the routed OU' {
        InModuleScope Sync {
            $user = [pscustomobject]@{
                ObjectGuid = [guid]'44444444-4444-4444-4444-444444444444'
                DistinguishedName = 'CN=Jamie Doe,OU=Old,DC=example,DC=com'
                SamAccountName = 'jdoe'
                Enabled = $true
                title = 'Engineer'
            }

            $global:SyncTestBaseConfig.ad | Add-Member -MemberType NoteProperty -Name ouRoutingRules -Value @(
                [pscustomobject]@{
                    match = [pscustomobject]@{ department = 'IT' }
                    targetOu = 'OU=IT,DC=example,DC=com'
                }
            ) -Force

            Mock Get-SfAdSyncConfig { $global:SyncTestBaseConfig }
            Mock Get-SfAdSyncMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SfAdSyncState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
            Mock Get-SfWorkers {
                @(
                    [pscustomobject]@{
                        personIdExternal = '6001'
                        employeeId = '6001'
                        status = 'active'
                        department = 'IT'
                        startDate = (Get-Date).ToString('o')
                    }
                )
            }
            Mock Get-SfAdTargetUser { $user }
            Mock Get-SfAdAttributeChanges {
                [pscustomobject]@{
                    Changes = @{ title = 'Senior Engineer' }
                    MissingRequired = @()
                }
            }
            Mock Set-SfAdUserAttributes {}
            Mock Move-SfAdUser {}
            Mock Get-SfAdUserByObjectGuid {
                [pscustomobject]@{
                    ObjectGuid = [guid]'44444444-4444-4444-4444-444444444444'
                    DistinguishedName = 'CN=Jamie Doe,OU=IT,DC=example,DC=com'
                    SamAccountName = 'jdoe'
                    Enabled = $true
                }
            }
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

            $global:CapturedReport.updates.Count | Should -Be 1
            $global:CapturedReport.graveyardMoves.Count | Should -Be 1
            @($global:CapturedReport.operations.operationType) | Should -Contain 'UpdateAttributes'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'MoveUser'
            Assert-MockCalled Set-SfAdUserAttributes -Times 1 -Exactly -ParameterFilter {
                $User.SamAccountName -eq 'jdoe' -and
                $Changes['title'] -eq 'Senior Engineer' -and
                $Changes['employeeID'] -eq '6001'
            }
            Assert-MockCalled Move-SfAdUser -Times 1 -Exactly -ParameterFilter {
                $User.SamAccountName -eq 'jdoe' -and
                $TargetOu -eq 'OU=IT,DC=example,DC=com'
            }
        }
    }

    It 'adds the resolved manager distinguished name to create attributes' {
        InModuleScope Sync {
            $manager = [pscustomobject]@{
                SamAccountName = 'mgr01'
                DistinguishedName = 'CN=Manager One,OU=Employees,DC=example,DC=com'
            }

            Mock Get-SfAdSyncConfig { $global:SyncTestBaseConfig }
            Mock Get-SfAdSyncMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SfAdSyncState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
            Mock Get-SfWorkers {
                @(
                    [pscustomobject]@{
                        personIdExternal = '6050'
                        employeeId = '6050'
                        firstName = 'Morgan'
                        lastName = 'Doe'
                        status = 'active'
                        startDate = (Get-Date).AddDays(30).ToString('o')
                        managerEmployeeId = '2000'
                    }
                )
            }
            Mock Get-SfAdTargetUser {
                param($Config, $WorkerId)
                if ($WorkerId -eq '2000') { return $manager }
                return $null
            }
            Mock Get-SfAdUserBySamAccountName { $null }
            Mock Get-SfAdUserByUserPrincipalName { $null }
            Mock Get-SfAdWorkerState { $null }
            Mock Get-SfAdAttributeChanges {
                [pscustomobject]@{
                    Changes = @{
                        UserPrincipalName = 'morgan.doe@example.com'
                    }
                    MissingRequired = @()
                }
            }
            Mock New-SfAdUser {
                [pscustomobject]@{
                    ObjectGuid = [guid]'77777777-1111-1111-1111-111111111111'
                    DistinguishedName = 'CN=Morgan Doe,OU=Employees,DC=example,DC=com'
                    SamAccountName = '6050'
                    Enabled = $false
                }
            }
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

            Assert-MockCalled New-SfAdUser -Times 1 -Exactly -ParameterFilter {
                $Attributes['manager'] -eq 'CN=Manager One,OU=Employees,DC=example,DC=com' -and
                $Attributes['employeeID'] -eq '6050'
            }
            $global:CapturedReport.quarantined.Count | Should -Be 0
        }
    }

    It 'quarantines workers when the manager cannot be resolved and skips creation' {
        InModuleScope Sync {
            Mock Get-SfAdSyncConfig { $global:SyncTestBaseConfig }
            Mock Get-SfAdSyncMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SfAdSyncState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
            Mock Get-SfWorkers {
                @(
                    [pscustomobject]@{
                        personIdExternal = '6051'
                        employeeId = '6051'
                        firstName = 'Casey'
                        lastName = 'Doe'
                        status = 'active'
                        startDate = (Get-Date).AddDays(30).ToString('o')
                        managerEmployeeId = '9999'
                    }
                )
            }
            Mock Get-SfAdTargetUser { $null }
            Mock Get-SfAdUserBySamAccountName { $null }
            Mock Get-SfAdUserByUserPrincipalName { $null }
            Mock Get-SfAdWorkerState { $null }
            Mock Get-SfAdAttributeChanges {
                [pscustomobject]@{
                    Changes = @{
                        UserPrincipalName = 'casey.doe@example.com'
                    }
                    MissingRequired = @()
                }
            }
            Mock New-SfAdUser {}
            Mock Save-SfAdSyncState {}
            Mock Save-SfAdSyncReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "sf-ad-sync-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SfAdSyncRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.quarantined.Count | Should -Be 1
            $global:CapturedReport.quarantined[0].reason | Should -Be 'ManagerNotResolved'
            Assert-MockCalled New-SfAdUser -Times 0 -Exactly
        }
    }

    It 'records unchanged workers when no attributes or state transitions are needed' {
        InModuleScope Sync {
            $user = [pscustomobject]@{
                ObjectGuid = [guid]'55555555-5555-5555-5555-555555555555'
                DistinguishedName = 'CN=Pat Doe,OU=Employees,DC=example,DC=com'
                SamAccountName = 'pdoe'
                Enabled = $true
                employeeID = '7001'
            }

            Mock Get-SfAdSyncConfig { $global:SyncTestBaseConfig }
            Mock Get-SfAdSyncMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SfAdSyncState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
            Mock Get-SfWorkers {
                @(
                    [pscustomobject]@{
                        personIdExternal = '7001'
                        employeeId = '7001'
                        status = 'active'
                        startDate = (Get-Date).AddDays(30).ToString('o')
                    }
                )
            }
            Mock Get-SfAdTargetUser { $user }
            Mock Get-SfAdAttributeChanges {
                [pscustomobject]@{
                    Changes = @{}
                    MissingRequired = @()
                }
            }
            Mock Set-SfAdUserAttributes {}
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

            $global:CapturedReport.unchanged.Count | Should -Be 1
            $global:CapturedReport.updates.Count | Should -Be 0
            @($global:CapturedReport.operations.operationType) | Should -Not -Contain 'UpdateAttributes'
            Assert-MockCalled Set-SfAdUserAttributes -Times 0 -Exactly
            Assert-MockCalled Move-SfAdUser -Times 0 -Exactly
        }
    }

    It 're-enables existing eligible users and records group additions' {
        InModuleScope Sync {
            $user = [pscustomobject]@{
                ObjectGuid = [guid]'66666666-6666-6666-6666-666666666666'
                DistinguishedName = 'CN=Taylor Doe,OU=Employees,DC=example,DC=com'
                SamAccountName = 'tdoe'
                Enabled = $false
                employeeID = '8001'
            }

            Mock Get-SfAdSyncConfig { $global:SyncTestBaseConfig }
            Mock Get-SfAdSyncMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SfAdSyncState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
            Mock Get-SfWorkers {
                @(
                    [pscustomobject]@{
                        personIdExternal = '8001'
                        employeeId = '8001'
                        status = 'active'
                        startDate = (Get-Date).ToString('o')
                    }
                )
            }
            Mock Get-SfAdTargetUser { $user }
            Mock Get-SfAdAttributeChanges {
                [pscustomobject]@{
                    Changes = @{}
                    MissingRequired = @()
                }
            }
            Mock Enable-SfAdUser {}
            Mock Add-SfAdUserToConfiguredGroups { @('CN=License,OU=Groups,DC=example,DC=com') }
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

            $global:CapturedReport.enables.Count | Should -Be 1
            $global:CapturedReport.enables[0].licensingGroups | Should -Be @('CN=License,OU=Groups,DC=example,DC=com')
            @($global:CapturedReport.operations.operationType) | Should -Contain 'EnableUser'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'AddGroupMembership'
            Assert-MockCalled Enable-SfAdUser -Times 1 -Exactly -ParameterFilter {
                $User.SamAccountName -eq 'tdoe'
            }
            Assert-MockCalled Add-SfAdUserToConfiguredGroups -Times 1 -Exactly -ParameterFilter {
                $User.SamAccountName -eq 'tdoe'
            }
        }
    }

    It 'quarantines workers with missing identity values' {
        InModuleScope Sync {
            Mock Get-SfAdSyncConfig { $global:SyncTestBaseConfig }
            Mock Get-SfAdSyncMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SfAdSyncState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
            Mock Get-SfWorkers {
                @(
                    [pscustomobject]@{
                        personIdExternal = $null
                        employeeId = '9001'
                        status = 'active'
                        startDate = (Get-Date).ToString('o')
                    }
                )
            }
            Mock Save-SfAdSyncState {}
            Mock Save-SfAdSyncReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "sf-ad-sync-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SfAdSyncRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.quarantined.Count | Should -Be 1
            $global:CapturedReport.quarantined[0].reason | Should -Be 'MissingEmployeeId'
        }
    }

    It 'quarantines workers with missing required mapped attributes' {
        InModuleScope Sync {
            Mock Get-SfAdSyncConfig { $global:SyncTestBaseConfig }
            Mock Get-SfAdSyncMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SfAdSyncState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
            Mock Get-SfWorkers {
                @(
                    [pscustomobject]@{
                        personIdExternal = '9101'
                        employeeId = '9101'
                        status = 'active'
                        startDate = (Get-Date).ToString('o')
                    }
                )
            }
            Mock Get-SfAdTargetUser { $null }
            Mock Get-SfAdAttributeChanges {
                [pscustomobject]@{
                    Changes = @{}
                    MissingRequired = @('firstName', 'lastName')
                }
            }
            Mock Save-SfAdSyncState {}
            Mock Save-SfAdSyncReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "sf-ad-sync-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SfAdSyncRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.quarantined.Count | Should -Be 1
            $global:CapturedReport.quarantined[0].reason | Should -Be 'MissingRequiredData'
            $global:CapturedReport.quarantined[0].fields | Should -Be @('firstName', 'lastName')
        }
    }

    It 'flags duplicate AD identity matches as conflicts' {
        InModuleScope Sync {
            Mock Get-SfAdSyncConfig { $global:SyncTestBaseConfig }
            Mock Get-SfAdSyncMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SfAdSyncState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
            Mock Get-SfWorkers {
                @(
                    [pscustomobject]@{
                        personIdExternal = '9201'
                        employeeId = '9201'
                        status = 'active'
                        startDate = (Get-Date).ToString('o')
                    }
                )
            }
            Mock Get-SfAdTargetUser {
                @(
                    [pscustomobject]@{ SamAccountName = 'a' },
                    [pscustomobject]@{ SamAccountName = 'b' }
                )
            }
            Mock Save-SfAdSyncState {}
            Mock Save-SfAdSyncReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "sf-ad-sync-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SfAdSyncRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.conflicts.Count | Should -Be 1
            $global:CapturedReport.conflicts[0].reason | Should -Be 'DuplicateAdIdentityMatch'
        }
    }

    It 'sends active workers already marked suppressed to manual review' {
        InModuleScope Sync {
            $state = [pscustomobject]@{
                checkpoint = '2026-03-05T10:00:00'
                workers = [pscustomobject]@{
                    '9301' = [pscustomobject]@{
                        suppressed = $true
                        distinguishedName = 'CN=Former User,OU=Graveyard,DC=example,DC=com'
                    }
                }
            }

            Mock Get-SfAdSyncConfig { $global:SyncTestBaseConfig }
            Mock Get-SfAdSyncMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SfAdSyncState { $state }
            Mock Get-SfWorkers {
                @(
                    [pscustomobject]@{
                        personIdExternal = '9301'
                        employeeId = '9301'
                        status = 'active'
                        startDate = (Get-Date).ToString('o')
                    }
                )
            }
            Mock Get-SfAdTargetUser { $null }
            Mock Save-SfAdSyncState {}
            Mock Save-SfAdSyncReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "sf-ad-sync-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SfAdSyncRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.manualReview.Count | Should -Be 1
            $global:CapturedReport.manualReview[0].reason | Should -Be 'RehireDetected'
        }
    }

    It 'deletes suppressed workers whose retention window has expired' {
        InModuleScope Sync {
            $state = [pscustomobject]@{
                checkpoint = '2026-03-05T10:00:00'
                workers = [pscustomobject]@{
                    '9401' = [pscustomobject]@{
                        suppressed = $true
                        deleteAfter = (Get-Date).AddDays(-1).ToString('o')
                        adObjectGuid = '77777777-7777-7777-7777-777777777777'
                    }
                }
            }
            $user = [pscustomobject]@{
                ObjectGuid = [guid]'77777777-7777-7777-7777-777777777777'
                DistinguishedName = 'CN=Sam Doe,OU=Graveyard,DC=example,DC=com'
                SamAccountName = 'sdoe'
                Enabled = $false
            }

            Mock Get-SfAdSyncConfig { $global:SyncTestBaseConfig }
            Mock Get-SfAdSyncMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SfAdSyncState { $state }
            Mock Get-SfWorkers { @() }
            Mock Get-SfAdUserByObjectGuid { $user }
            Mock Get-SfWorkerById { $null }
            Mock Get-SfAdUserSnapshot { [pscustomobject]@{ samAccountName = 'sdoe'; objectGuid = '77777777-7777-7777-7777-777777777777' } }
            Mock Remove-SfAdUser {}
            Mock Save-SfAdSyncState {}
            Mock Save-SfAdSyncReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "sf-ad-sync-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SfAdSyncRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.deletions.Count | Should -Be 1
            @($global:CapturedReport.operations.operationType) | Should -Contain 'DeleteUser'
            Assert-MockCalled Remove-SfAdUser -Times 1 -Exactly -ParameterFilter {
                $User.SamAccountName -eq 'sdoe'
            }
        }
    }

    It 'skips deletion when the retention window has not expired' {
        InModuleScope Sync {
            $state = [pscustomobject]@{
                checkpoint = '2026-03-05T10:00:00'
                workers = [pscustomobject]@{
                    '9402' = [pscustomobject]@{
                        suppressed = $true
                        deleteAfter = (Get-Date).AddDays(2).ToString('o')
                        adObjectGuid = '88888888-8888-8888-8888-888888888888'
                    }
                }
            }

            Mock Get-SfAdSyncConfig { $global:SyncTestBaseConfig }
            Mock Get-SfAdSyncMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SfAdSyncState { $state }
            Mock Get-SfWorkers { @() }
            Mock Get-SfAdUserByObjectGuid {}
            Mock Get-SfWorkerById { $null }
            Mock Remove-SfAdUser {}
            Mock Save-SfAdSyncState {}
            Mock Save-SfAdSyncReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "sf-ad-sync-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SfAdSyncRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.deletions.Count | Should -Be 0
            $global:CapturedReport.manualReview.Count | Should -Be 0
            Assert-MockCalled Remove-SfAdUser -Times 0 -Exactly
            Assert-MockCalled Get-SfAdUserByObjectGuid -Times 0 -Exactly
        }
    }

    It 'sends suppressed workers to manual review when they reactivate before deletion' {
        InModuleScope Sync {
            $state = [pscustomobject]@{
                checkpoint = '2026-03-05T10:00:00'
                workers = [pscustomobject]@{
                    '9403' = [pscustomobject]@{
                        suppressed = $true
                        deleteAfter = (Get-Date).AddDays(-1).ToString('o')
                        adObjectGuid = '99999999-9999-9999-9999-999999999999'
                    }
                }
            }
            $user = [pscustomobject]@{
                ObjectGuid = [guid]'99999999-9999-9999-9999-999999999999'
                DistinguishedName = 'CN=Rehire Doe,OU=Graveyard,DC=example,DC=com'
                SamAccountName = 'rdoe'
                Enabled = $false
            }

            Mock Get-SfAdSyncConfig { $global:SyncTestBaseConfig }
            Mock Get-SfAdSyncMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SfAdSyncState { $state }
            Mock Get-SfWorkers { @() }
            Mock Get-SfAdUserByObjectGuid { $user }
            Mock Get-SfWorkerById {
                [pscustomobject]@{
                    personIdExternal = '9403'
                    employeeId = '9403'
                    status = 'active'
                    startDate = (Get-Date).ToString('o')
                }
            }
            Mock Remove-SfAdUser {}
            Mock Save-SfAdSyncState {}
            Mock Save-SfAdSyncReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "sf-ad-sync-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SfAdSyncRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.manualReview.Count | Should -Be 1
            $global:CapturedReport.manualReview[0].reason | Should -Be 'RehireDetectedBeforeDelete'
            Assert-MockCalled Remove-SfAdUser -Times 0 -Exactly
        }
    }

    It 'marks the report failed when an AD mutation throws' {
        InModuleScope Sync {
            $user = [pscustomobject]@{
                ObjectGuid = [guid]'12121212-1212-1212-1212-121212121212'
                DistinguishedName = 'CN=Error Doe,OU=Employees,DC=example,DC=com'
                SamAccountName = 'edoe'
                Enabled = $true
                employeeID = '9501'
            }

            Mock Get-SfAdSyncConfig { $global:SyncTestBaseConfig }
            Mock Get-SfAdSyncMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SfAdSyncState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
            Mock Get-SfWorkers {
                @(
                    [pscustomobject]@{
                        personIdExternal = '9501'
                        employeeId = '9501'
                        status = 'active'
                        startDate = (Get-Date).ToString('o')
                    }
                )
            }
            Mock Get-SfAdTargetUser { $user }
            Mock Get-SfAdAttributeChanges {
                [pscustomobject]@{
                    Changes = @{ title = 'Principal Engineer' }
                    MissingRequired = @()
                }
            }
            Mock Set-SfAdUserAttributes { throw 'AD update failed' }
            Mock Save-SfAdSyncReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "sf-ad-sync-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            { Invoke-SfAdSyncRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null } | Should -Throw 'AD update failed'

            $global:CapturedReport.status | Should -Be 'Failed'
            $global:CapturedReport.errorMessage | Should -Be 'AD update failed'
        }
    }

    It 'fails the run when disable threshold is exceeded' {
        InModuleScope Sync {
            $user = [pscustomobject]@{
                ObjectGuid = [guid]'13131313-1313-1313-1313-131313131313'
                DistinguishedName = 'CN=Alex Doe,OU=Employees,DC=example,DC=com'
                SamAccountName = 'adoe'
                Enabled = $true
            }
            $global:SyncTestBaseConfig.safety.maxDisablesPerRun = 0

            Mock Get-SfAdSyncConfig { $global:SyncTestBaseConfig }
            Mock Get-SfAdSyncMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SfAdSyncState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
            Mock Get-SfWorkers {
                @(
                    [pscustomobject]@{
                        personIdExternal = '9601'
                        employeeId = '9601'
                        status = 'inactive'
                        startDate = (Get-Date).AddDays(-30).ToString('o')
                    }
                )
            }
            Mock Get-SfAdTargetUser { $user }
            Mock Get-SfAdWorkerState { $null }
            Mock Disable-SfAdUser { throw 'should not disable user' }
            Mock Save-SfAdSyncReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "sf-ad-sync-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            { Invoke-SfAdSyncRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null } | Should -Throw '*maxDisablesPerRun*'

            $global:CapturedReport.status | Should -Be 'Failed'
            $global:CapturedReport.guardrailFailures.Count | Should -Be 1
            $global:CapturedReport.guardrailFailures[0].threshold | Should -Be 'maxDisablesPerRun'
            Assert-MockCalled Disable-SfAdUser -Times 0 -Exactly
        }
    }

    It 'fails the run when delete threshold is exceeded' {
        InModuleScope Sync {
            $state = [pscustomobject]@{
                checkpoint = '2026-03-05T10:00:00'
                workers = [pscustomobject]@{
                    '9701' = [pscustomobject]@{
                        suppressed = $true
                        deleteAfter = (Get-Date).AddDays(-1).ToString('o')
                        adObjectGuid = '14141414-1414-1414-1414-141414141414'
                    }
                }
            }
            $user = [pscustomobject]@{
                ObjectGuid = [guid]'14141414-1414-1414-1414-141414141414'
                DistinguishedName = 'CN=Delete Doe,OU=Graveyard,DC=example,DC=com'
                SamAccountName = 'ddoe'
                Enabled = $false
            }
            $global:SyncTestBaseConfig.safety.maxDeletionsPerRun = 0

            Mock Get-SfAdSyncConfig { $global:SyncTestBaseConfig }
            Mock Get-SfAdSyncMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SfAdSyncState { $state }
            Mock Get-SfWorkers { @() }
            Mock Get-SfAdUserByObjectGuid { $user }
            Mock Remove-SfAdUser { throw 'should not delete user' }
            Mock Save-SfAdSyncReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "sf-ad-sync-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            { Invoke-SfAdSyncRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null } | Should -Throw '*maxDeletionsPerRun*'

            $global:CapturedReport.status | Should -Be 'Failed'
            $global:CapturedReport.guardrailFailures.Count | Should -Be 1
            $global:CapturedReport.guardrailFailures[0].threshold | Should -Be 'maxDeletionsPerRun'
            Assert-MockCalled Remove-SfAdUser -Times 0 -Exactly
        }
    }

    It 'records a first-sync review artifact without mutating AD or sync state' {
        InModuleScope Sync {
            $global:SyncTestBaseConfig.reporting | Add-Member -MemberType NoteProperty -Name reviewOutputDirectory -Value (Join-Path $TestDrive 'reviews') -Force
            Mock Get-SfAdSyncConfig { $global:SyncTestBaseConfig }
            Mock Get-SfAdSyncMappingConfig {
                [pscustomobject]@{
                    mappings = @(
                        [pscustomobject]@{
                            source = 'firstName'
                            target = 'GivenName'
                            enabled = $true
                            required = $true
                            transform = 'Trim'
                        }
                    )
                }
            }
            Mock Get-SfAdSyncState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
            Mock Get-SfWorkers {
                @(
                    [pscustomobject]@{
                        personIdExternal = '5001'
                        firstName = 'Jamie'
                        status = 'active'
                        startDate = (Get-Date).ToString('o')
                        managerEmployeeId = $null
                    }
                )
            }
            Mock Get-SfAdTargetUser {
                [pscustomobject]@{
                    ObjectGuid = [guid]'55555555-5555-5555-5555-555555555555'
                    DistinguishedName = 'CN=Jamie Doe,OU=Employees,DC=example,DC=com'
                    SamAccountName = 'jdoe'
                    employeeID = '5001'
                    Enabled = $true
                    GivenName = 'OldJamie'
                }
            }
            Mock Get-SfAdWorkerState { $null }
            Mock Get-SfAdAttributeChanges {
                [pscustomobject]@{
                    Changes = @{ GivenName = 'Jamie' }
                    MissingRequired = @()
                }
            }
            Mock Get-SfAdMappingEvaluation {
                [pscustomobject]@{
                    Changes = @{ GivenName = 'Jamie' }
                    MissingRequired = @()
                    Rows = @(
                        [pscustomobject]@{
                            sourceField = 'firstName'
                            targetAttribute = 'GivenName'
                            transform = 'Trim'
                            required = $true
                            sourceValue = 'Jamie'
                            currentAdValue = 'OldJamie'
                            proposedValue = 'Jamie'
                            changed = $true
                        }
                    )
                }
            }
            Mock Set-SfAdUserAttributes {}
            Mock Save-SfAdSyncState { throw 'state should not be saved in review mode' }
            Mock Set-SfAdWorkerState { throw 'tracked state should not be written in review mode' }
            Mock Save-SfAdSyncReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                $global:CapturedReviewDirectory = $Directory
                return (Join-Path $Directory "sf-ad-sync-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SfAdSyncRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Review | Out-Null

            $global:CapturedReviewDirectory | Should -Be $global:SyncTestBaseConfig.reporting.reviewOutputDirectory
            $global:CapturedReport.mode | Should -Be 'Review'
            $global:CapturedReport.artifactType | Should -Be 'FirstSyncReview'
            $global:CapturedReport.dryRun | Should -BeTrue
            $global:CapturedReport.reviewSummary.existingUsersMatched | Should -Be 1
            $global:CapturedReport.reviewSummary.existingUsersWithAttributeChanges | Should -Be 1
            $global:CapturedReport.reviewSummary.deletionPassSkipped | Should -BeTrue
            $global:CapturedReport.updates[0].reviewCategory | Should -Be 'ExistingUserChanges'
            $global:CapturedReport.updates[0].changedAttributeDetails[0].targetAttribute | Should -Be 'GivenName'
            Assert-MockCalled Set-SfAdUserAttributes -Times 1 -Exactly -ParameterFilter { $DryRun }
            Assert-MockCalled Save-SfAdSyncState -Times 0 -Exactly
            Assert-MockCalled Set-SfAdWorkerState -Times 0 -Exactly
            Assert-MockCalled Get-SfWorkers -Times 1 -Exactly
        }
    }

    It 'scopes review mode to one worker when WorkerId is provided' {
        InModuleScope Sync {
            $global:SyncTestBaseConfig.reporting | Add-Member -MemberType NoteProperty -Name reviewOutputDirectory -Value (Join-Path $TestDrive 'reviews') -Force
            Mock Get-SfAdSyncConfig { $global:SyncTestBaseConfig }
            Mock Get-SfAdSyncMappingConfig {
                [pscustomobject]@{
                    mappings = @(
                        [pscustomobject]@{
                            source = 'firstName'
                            target = 'GivenName'
                            enabled = $true
                            required = $true
                            transform = 'Trim'
                        }
                    )
                }
            }
            Mock Get-SfAdSyncState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
            Mock Get-SfWorkers { throw 'Get-SfWorkers should not be used for scoped preview.' }
            Mock Get-SfWorkerById {
                [pscustomobject]@{
                    personIdExternal = '7001'
                    firstName = 'Jamie'
                    lastName = 'Doe'
                    status = 'active'
                    startDate = (Get-Date).ToString('o')
                    managerEmployeeId = $null
                }
            }
            Mock Get-SfAdTargetUser {
                [pscustomobject]@{
                    ObjectGuid = [guid]'77777777-7777-7777-7777-777777777777'
                    DistinguishedName = 'CN=Jamie Doe,OU=Employees,DC=example,DC=com'
                    SamAccountName = 'jdoe'
                    employeeID = '7001'
                    Enabled = $true
                    GivenName = 'OldJamie'
                }
            }
            Mock Get-SfAdWorkerState { $null }
            Mock Get-SfAdAttributeChanges {
                [pscustomobject]@{
                    Changes = @{ GivenName = 'Jamie' }
                    MissingRequired = @()
                }
            }
            Mock Get-SfAdMappingEvaluation {
                [pscustomobject]@{
                    Changes = @{ GivenName = 'Jamie' }
                    MissingRequired = @()
                    Rows = @(
                        [pscustomobject]@{
                            sourceField = 'firstName'
                            targetAttribute = 'GivenName'
                            transform = 'Trim'
                            required = $true
                            sourceValue = 'Jamie'
                            currentAdValue = 'OldJamie'
                            proposedValue = 'Jamie'
                            changed = $true
                        }
                    )
                }
            }
            Mock Set-SfAdUserAttributes {}
            Mock Invoke-SfAdDeletionPass { throw 'Deletion pass should not run during review preview.' }
            Mock Save-SfAdSyncState { throw 'state should not be saved in scoped preview mode' }
            Mock Set-SfAdWorkerState { throw 'tracked state should not be written in scoped preview mode' }
            Mock Save-SfAdSyncReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                $global:CapturedReviewDirectory = $Directory
                return (Join-Path $Directory "sf-ad-sync-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SfAdSyncRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Review -WorkerId '7001' | Out-Null

            $global:CapturedReviewDirectory | Should -Be $global:SyncTestBaseConfig.reporting.reviewOutputDirectory
            $global:CapturedReport.artifactType | Should -Be 'WorkerPreview'
            $global:CapturedReport.workerScope.workerId | Should -Be '7001'
            $global:CapturedReport.workerScope.identityField | Should -Be 'personIdExternal'
            $global:CapturedReport.reviewSummary.existingUsersMatched | Should -Be 1
            $global:CapturedReport.updates[0].changedAttributeDetails[0].targetAttribute | Should -Be 'GivenName'
            Assert-MockCalled Get-SfWorkerById -Times 1 -Exactly -ParameterFilter { $WorkerId -eq '7001' }
            Assert-MockCalled Get-SfWorkers -Times 0 -Exactly
            Assert-MockCalled Invoke-SfAdDeletionPass -Times 0 -Exactly
            Assert-MockCalled Save-SfAdSyncState -Times 0 -Exactly
            Assert-MockCalled Set-SfAdWorkerState -Times 0 -Exactly
            Assert-MockCalled Set-SfAdUserAttributes -Times 1 -Exactly -ParameterFilter { $DryRun }
        }
    }

    It 'rejects WorkerId outside review mode' {
        InModuleScope Sync {
            { Invoke-SfAdSyncRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta -WorkerId '7001' } | Should -Throw '-WorkerId is only supported with -Mode Review.'
        }
    }

    It 'counts matched review users that land in quarantine or manual review' {
        InModuleScope Sync {
            $global:SyncTestBaseConfig.reporting | Add-Member -MemberType NoteProperty -Name reviewOutputDirectory -Value (Join-Path $TestDrive 'reviews') -Force
            Mock Get-SfAdSyncConfig { $global:SyncTestBaseConfig }
            Mock Get-SfAdSyncMappingConfig {
                [pscustomobject]@{
                    mappings = @(
                        [pscustomobject]@{
                            source = 'firstName'
                            target = 'GivenName'
                            enabled = $true
                            required = $true
                            transform = 'Trim'
                        }
                    )
                }
            }
            Mock Get-SfAdSyncState {
                [pscustomobject]@{
                    checkpoint = '2026-03-05T10:00:00'
                    workers = [pscustomobject]@{
                        '6002' = [pscustomobject]@{
                            suppressed = $true
                            distinguishedName = 'CN=Rehire,OU=Employees,DC=example,DC=com'
                        }
                    }
                }
            }
            Mock Get-SfWorkers {
                @(
                    [pscustomobject]@{
                        personIdExternal = '6001'
                        firstName = $null
                        status = 'active'
                        startDate = (Get-Date).ToString('o')
                        managerEmployeeId = $null
                    }
                    [pscustomobject]@{
                        personIdExternal = '6002'
                        firstName = 'Rehire'
                        status = 'active'
                        startDate = (Get-Date).ToString('o')
                        managerEmployeeId = $null
                    }
                )
            }
            Mock Get-SfAdTargetUser {
                [pscustomobject]@{
                    ObjectGuid = [guid]'66666666-6666-6666-6666-666666666666'
                    DistinguishedName = 'CN=Existing User,OU=Employees,DC=example,DC=com'
                    SamAccountName = 'existing.user'
                    employeeID = '6001'
                    Enabled = $true
                    GivenName = 'Existing'
                }
            } -ParameterFilter { $WorkerId -eq '6001' }
            Mock Get-SfAdTargetUser { $null } -ParameterFilter { $WorkerId -eq '6002' }
            Mock Get-SfAdWorkerState { $null } -ParameterFilter { $WorkerId -eq '6001' }
            Mock Get-SfAdWorkerState {
                [pscustomobject]@{
                    suppressed = $true
                    distinguishedName = 'CN=Rehire,OU=Employees,DC=example,DC=com'
                }
            } -ParameterFilter { $WorkerId -eq '6002' }
            Mock Get-SfAdAttributeChanges {
                [pscustomobject]@{
                    Changes = @{}
                    MissingRequired = @('firstName')
                }
            }
            Mock Get-SfAdMappingEvaluation {
                [pscustomobject]@{
                    Changes = @{}
                    MissingRequired = @('firstName')
                    Rows = @()
                }
            }
            Mock Save-SfAdSyncReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "sf-ad-sync-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SfAdSyncRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Review | Out-Null

            $global:CapturedReport.reviewSummary.existingUsersMatched | Should -Be 2
            $global:CapturedReport.quarantined[0].matchedExistingUser | Should -BeTrue
            $global:CapturedReport.manualReview[0].matchedExistingUser | Should -BeTrue
        }
    }
}

Describe 'Test-SfAdSyncPreflight' {
    BeforeAll {
        Import-Module "$PSScriptRoot/../src/Modules/SfAdSync/Sync.psm1" -Force -DisableNameChecking
    }

    It 'loads config and mapping metadata without performing a sync' {
        InModuleScope Sync {
            Mock Get-SfAdSyncConfig {
                [pscustomobject]@{
                    successFactors = [pscustomobject]@{
                        query = [pscustomobject]@{
                            identityField = 'personIdExternal'
                        }
                    }
                    ad = [pscustomobject]@{
                        identityAttribute = 'employeeID'
                    }
                    state = [pscustomobject]@{
                        path = (Join-Path $TestDrive 'state.json')
                    }
                    reporting = [pscustomobject]@{
                        outputDirectory = (Join-Path $TestDrive 'reports')
                    }
                }
            }
            Mock Get-SfAdSyncMappingConfig {
                [pscustomobject]@{
                    mappings = @(
                        [pscustomobject]@{ source = 'firstName'; target = 'GivenName'; enabled = $true; required = $true; transform = 'Trim' }
                    )
                }
            }
            Mock Ensure-ActiveDirectoryModule {}

            $configPath = Join-Path $TestDrive 'config.json'
            $mappingPath = Join-Path $TestDrive 'mapping.json'
            '{}' | Set-Content -Path $configPath
            '{}' | Set-Content -Path $mappingPath

            $result = Test-SfAdSyncPreflight -ConfigPath $configPath -MappingConfigPath $mappingPath

            $result.success | Should -BeTrue
            $result.identityField | Should -Be 'personIdExternal'
            $result.identityAttribute | Should -Be 'employeeID'
            $result.mappingCount | Should -Be 1
        }
    }
}
