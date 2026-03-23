Describe 'Invoke-SyncFactorsRun' {
    BeforeAll {
        Import-Module "$PSScriptRoot/../src/Modules/SyncFactors/Sync.psm1" -Force -DisableNameChecking

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
            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SyncFactorsState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
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
            Mock Get-SyncFactorsTargetUser { $null }
            Mock Get-SyncFactorsUserBySamAccountName { $null }
            Mock Get-SyncFactorsUserByUserPrincipalName { $null }
            Mock Get-SyncFactorsWorkerState { $null }
            Mock Get-SyncFactorsAttributeChanges {
                [pscustomobject]@{
                    Changes = @{
                        UserPrincipalName = 'jamie.doe@example.com'
                        title = 'Engineer'
                    }
                    MissingRequired = @()
                }
            }
            Mock New-SyncFactorsUser {
                [pscustomobject]@{
                    ObjectGuid = [guid]'11111111-1111-1111-1111-111111111111'
                    DistinguishedName = 'CN=Jamie Doe,OU=Employees,DC=example,DC=com'
                    SamAccountName = '1001'
                    Enabled = $false
                }
            }
            Mock Enable-SyncFactorsUser {}
            Mock Add-SyncFactorsUserToConfiguredGroups { @('CN=License,OU=Groups,DC=example,DC=com') }
            Mock Set-SyncFactorsWorkerState {
                param($State, $WorkerId, $WorkerState)
                $State.workers | Add-Member -MemberType NoteProperty -Name $WorkerId -Value $WorkerState -Force
            }
            Mock Save-SyncFactorsState { param($State, $Path) $global:SavedStatePath = $Path }
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Write-SyncFactorsRuntimeStatusSnapshot {
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

            Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.creates.Count | Should -Be 1
            $global:CapturedReport.enables.Count | Should -Be 1
            $global:CapturedReport.status | Should -Be 'Succeeded'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'CreateUser'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'EnableUser'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'AddGroupMembership'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'SetWorkerState'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'SetCheckpoint'
            $global:SavedStatePath | Should -Be $global:SyncTestBaseConfig.state.path
            Assert-MockCalled New-SyncFactorsUser -Times 1 -Exactly -ParameterFilter {
                $WorkerId -eq '1001' -and
                $Attributes['UserPrincipalName'] -eq 'jamie.doe@example.com' -and
                $Attributes['title'] -eq 'Engineer' -and
                $Attributes['employeeID'] -eq '1001'
            }
            Assert-MockCalled Enable-SyncFactorsUser -Times 1 -Exactly -ParameterFilter {
                $User.SamAccountName -eq '1001'
            }
            Assert-MockCalled Add-SyncFactorsUserToConfiguredGroups -Times 1 -Exactly -ParameterFilter {
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

    It 'treats nested emplStatus and nested employment startDate as worker status inputs' {
        InModuleScope Sync {
            $worker = [pscustomobject]@{
                personIdExternal = '1001'
                employmentNav = @(
                    [pscustomobject]@{
                        startDate = (Get-Date).ToString('o')
                        jobInfoNav = @(
                            [pscustomobject]@{
                                emplStatus = 'A'
                            }
                        )
                    }
                )
            }

            (Get-SyncFactorsWorkerStatusValue -Worker $worker) | Should -Be 'A'
            (Get-SyncFactorsWorkerStartDateValue -Worker $worker) | Should -Not -BeNullOrEmpty
            (Test-SyncFactorsWorkerIsActive -Worker $worker) | Should -BeTrue
            (Test-SyncFactorsWorkerIsPrehireEligible -Worker $worker -EnableBeforeDays 7) | Should -BeTrue
        }
    }

    It 'prefers employment status over top-level status when determining whether a worker is active' {
        InModuleScope Sync {
            $worker = [pscustomobject]@{
                personIdExternal = '1001'
                status = 'inactive'
                employmentNav = @(
                    [pscustomobject]@{
                        startDate = (Get-Date).ToString('o')
                        jobInfoNav = @(
                            [pscustomobject]@{
                                emplStatus = 'A'
                            }
                        )
                    }
                )
            }

            (Get-SyncFactorsWorkerStatusValue -Worker $worker) | Should -Be 'A'
            (Test-SyncFactorsWorkerIsActive -Worker $worker) | Should -BeTrue
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

            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SyncFactorsState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
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
            Mock Get-SyncFactorsTargetUser { $user }
            Mock Get-SyncFactorsWorkerState { $null }
            Mock Disable-SyncFactorsUser {}
            Mock Get-SyncFactorsUserByObjectGuid { $user }
            Mock Move-SyncFactorsUser {}
            Mock Set-SyncFactorsWorkerState {
                param($State, $WorkerId, $WorkerState)
                $State.workers | Add-Member -MemberType NoteProperty -Name $WorkerId -Value $WorkerState -Force
            }
            Mock Save-SyncFactorsState {}
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.disables.Count | Should -Be 1
            $global:CapturedReport.graveyardMoves.Count | Should -Be 1
            $global:CapturedReport.status | Should -Be 'Succeeded'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'DisableUser'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'MoveUser'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'SetWorkerState'
            Assert-MockCalled Disable-SyncFactorsUser -Times 1 -Exactly -ParameterFilter {
                $User.SamAccountName -eq 'adoe'
            }
            Assert-MockCalled Move-SyncFactorsUser -Times 1 -Exactly -ParameterFilter {
                $User.SamAccountName -eq 'adoe' -and
                $TargetOu -eq 'OU=Graveyard,DC=example,DC=com'
            }
        }
    }

    It 'queues offboarding for manual approval instead of applying high-risk actions' {
        InModuleScope Sync {
            $user = [pscustomobject]@{
                ObjectGuid = [guid]'33333333-3333-3333-3333-333333333333'
                DistinguishedName = 'CN=Alex Doe,OU=Employees,DC=example,DC=com'
                SamAccountName = 'adoe'
                Enabled = $true
            }

            $global:SyncTestBaseConfig | Add-Member -MemberType NoteProperty -Name approval -Value ([pscustomobject]@{
                enabled = $true
                requireFor = @('DisableUser', 'MoveToGraveyardOu')
            }) -Force

            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SyncFactorsState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
            Mock Get-SfWorkers {
                @(
                    [pscustomobject]@{
                        personIdExternal = '2101'
                        employeeId = '2101'
                        status = 'inactive'
                        startDate = (Get-Date).AddDays(-30).ToString('o')
                    }
                )
            }
            Mock Get-SyncFactorsTargetUser { $user }
            Mock Get-SyncFactorsWorkerState { $null }
            Mock Disable-SyncFactorsUser {}
            Mock Get-SyncFactorsUserByObjectGuid { $user }
            Mock Move-SyncFactorsUser {}
            Mock Set-SyncFactorsWorkerState {
                param($State, $WorkerId, $WorkerState)
                $State.workers | Add-Member -MemberType NoteProperty -Name $WorkerId -Value $WorkerState -Force
            }
            Mock Save-SyncFactorsState {}
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.disables.Count | Should -Be 0
            $global:CapturedReport.graveyardMoves.Count | Should -Be 0
            $global:CapturedReport.manualReview.Count | Should -Be 1
            $global:CapturedReport.manualReview[0].reviewCaseType | Should -Be 'ApprovalRequired'
            $global:CapturedReport.manualReview[0].approvalActions | Should -Be @('DisableUser', 'MoveToGraveyardOu')
            $global:CapturedReport.manualReview[0].targetOu | Should -Be 'OU=Graveyard,DC=example,DC=com'
            Assert-MockCalled Disable-SyncFactorsUser -Times 0 -Exactly
            Assert-MockCalled Move-SyncFactorsUser -Times 0 -Exactly
            Assert-MockCalled Set-SyncFactorsWorkerState -Times 0 -Exactly
        }
    }

    It 'fails the run when create threshold is exceeded' {
        InModuleScope Sync {
            $global:SyncTestBaseConfig.safety.maxCreatesPerRun = 0

            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SyncFactorsState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
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
            Mock Get-SyncFactorsTargetUser { $null }
            Mock Get-SyncFactorsUserBySamAccountName { $null }
            Mock Get-SyncFactorsUserByUserPrincipalName { $null }
            Mock Get-SyncFactorsWorkerState { $null }
            Mock Get-SyncFactorsAttributeChanges {
                [pscustomobject]@{
                    Changes = @{ UserPrincipalName = 'robin.smith@example.com' }
                    MissingRequired = @()
                }
            }
            Mock New-SyncFactorsUser { throw 'should not create user' }
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Write-SyncFactorsRuntimeStatusSnapshot {
                param($Report, $StatePath, $Stage, $Status, $ProcessedWorkers, $TotalWorkers, $CurrentWorkerId, $LastAction, $CompletedAt, $ErrorMessage)
                $global:RuntimeSnapshots += [pscustomobject]@{
                    Stage = $Stage
                    Status = $Status
                    ErrorMessage = $ErrorMessage
                }
            }

            { Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null } | Should -Throw '*maxCreatesPerRun*'

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
            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SyncFactorsState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
            Mock Get-SfWorkers {
                @(
                    [pscustomobject]@{ personIdExternal = '4001'; employeeId = '4001'; status = 'active'; startDate = (Get-Date).ToString('o') },
                    [pscustomobject]@{ personIdExternal = '4001'; employeeId = '4001'; status = 'active'; startDate = (Get-Date).ToString('o') }
                )
            }
            Mock Save-SyncFactorsState {}
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}
            Mock New-SyncFactorsUser {}

            Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.status | Should -Be 'Succeeded'
            $global:CapturedReport.conflicts.Count | Should -Be 2
            @($global:CapturedReport.conflicts.reason | Select-Object -Unique) | Should -Be @('DuplicateWorkerId')
            Assert-MockCalled New-SyncFactorsUser -Times 0 -Exactly
        }
    }

    It 'blocks creates when the target UPN already exists' {
        InModuleScope Sync {
            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SyncFactorsState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
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
            Mock Get-SyncFactorsTargetUser { $null }
            Mock Get-SyncFactorsUserBySamAccountName { $null }
            Mock Get-SyncFactorsUserByUserPrincipalName {
                [pscustomobject]@{ SamAccountName = 'tjones' }
            }
            Mock Get-SyncFactorsWorkerState { $null }
            Mock Get-SyncFactorsAttributeChanges {
                [pscustomobject]@{
                    Changes = @{ UserPrincipalName = 'taylor.jones@example.com' }
                    MissingRequired = @()
                }
            }
            Mock New-SyncFactorsUser {}
            Mock Save-SyncFactorsState {}
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.status | Should -Be 'Succeeded'
            $global:CapturedReport.conflicts.Count | Should -Be 1
            $global:CapturedReport.conflicts[0].reason | Should -Be 'UserPrincipalNameCollision'
            Assert-MockCalled New-SyncFactorsUser -Times 0 -Exactly
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

            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SyncFactorsState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
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
            Mock Get-SyncFactorsTargetUser { $user }
            Mock Get-SyncFactorsAttributeChanges {
                [pscustomobject]@{
                    Changes = @{ title = 'Senior Engineer' }
                    MissingRequired = @()
                }
            }
            Mock Set-SyncFactorsUserAttributes {}
            Mock Move-SyncFactorsUser {}
            Mock Get-SyncFactorsUserByObjectGuid {
                [pscustomobject]@{
                    ObjectGuid = [guid]'44444444-4444-4444-4444-444444444444'
                    DistinguishedName = 'CN=Jamie Doe,OU=IT,DC=example,DC=com'
                    SamAccountName = 'jdoe'
                    Enabled = $true
                }
            }
            Mock Set-SyncFactorsWorkerState {
                param($State, $WorkerId, $WorkerState)
                $State.workers | Add-Member -MemberType NoteProperty -Name $WorkerId -Value $WorkerState -Force
            }
            Mock Save-SyncFactorsState {}
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.updates.Count | Should -Be 1
            $global:CapturedReport.graveyardMoves.Count | Should -Be 1
            @($global:CapturedReport.operations.operationType) | Should -Contain 'UpdateAttributes'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'MoveUser'
            Assert-MockCalled Set-SyncFactorsUserAttributes -Times 1 -Exactly -ParameterFilter {
                $User.SamAccountName -eq 'jdoe' -and
                $Changes['title'] -eq 'Senior Engineer' -and
                $Changes['employeeID'] -eq '6001'
            }
            Assert-MockCalled Move-SyncFactorsUser -Times 1 -Exactly -ParameterFilter {
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

            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SyncFactorsState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
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
            Mock Get-SyncFactorsTargetUser {
                param($Config, $WorkerId)
                if ($WorkerId -eq '2000') { return $manager }
                return $null
            }
            Mock Get-SyncFactorsUserBySamAccountName { $null }
            Mock Get-SyncFactorsUserByUserPrincipalName { $null }
            Mock Get-SyncFactorsWorkerState { $null }
            Mock Get-SyncFactorsAttributeChanges {
                [pscustomobject]@{
                    Changes = @{
                        UserPrincipalName = 'morgan.doe@example.com'
                    }
                    MissingRequired = @()
                }
            }
            Mock New-SyncFactorsUser {
                [pscustomobject]@{
                    ObjectGuid = [guid]'77777777-1111-1111-1111-111111111111'
                    DistinguishedName = 'CN=Morgan Doe,OU=Employees,DC=example,DC=com'
                    SamAccountName = '6050'
                    Enabled = $false
                }
            }
            Mock Set-SyncFactorsWorkerState {
                param($State, $WorkerId, $WorkerState)
                $State.workers | Add-Member -MemberType NoteProperty -Name $WorkerId -Value $WorkerState -Force
            }
            Mock Save-SyncFactorsState {}
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            Assert-MockCalled New-SyncFactorsUser -Times 1 -Exactly -ParameterFilter {
                $Attributes['manager'] -eq 'CN=Manager One,OU=Employees,DC=example,DC=com' -and
                $Attributes['employeeID'] -eq '6050'
            }
            $global:CapturedReport.quarantined.Count | Should -Be 0
        }
    }

    It 'quarantines workers when the manager cannot be resolved and skips creation' {
        InModuleScope Sync {
            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SyncFactorsState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
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
            Mock Get-SyncFactorsTargetUser { $null }
            Mock Get-SyncFactorsUserBySamAccountName { $null }
            Mock Get-SyncFactorsUserByUserPrincipalName { $null }
            Mock Get-SyncFactorsWorkerState { $null }
            Mock Get-SyncFactorsAttributeChanges {
                [pscustomobject]@{
                    Changes = @{
                        UserPrincipalName = 'casey.doe@example.com'
                    }
                    MissingRequired = @()
                }
            }
            Mock New-SyncFactorsUser {}
            Mock Save-SyncFactorsState {}
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.quarantined.Count | Should -Be 1
            $global:CapturedReport.quarantined[0].reason | Should -Be 'ManagerNotResolved'
            $global:CapturedReport.quarantined[0].reviewCaseType | Should -Be 'UnresolvedManager'
            $global:CapturedReport.quarantined[0].operatorActions[0].code | Should -Be 'ResolveManagerIdentity'
            Assert-MockCalled New-SyncFactorsUser -Times 0 -Exactly
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

            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SyncFactorsState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
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
            Mock Get-SyncFactorsTargetUser { $user }
            Mock Get-SyncFactorsAttributeChanges {
                [pscustomobject]@{
                    Changes = @{}
                    MissingRequired = @()
                }
            }
            Mock Set-SyncFactorsUserAttributes {}
            Mock Move-SyncFactorsUser {}
            Mock Set-SyncFactorsWorkerState {
                param($State, $WorkerId, $WorkerState)
                $State.workers | Add-Member -MemberType NoteProperty -Name $WorkerId -Value $WorkerState -Force
            }
            Mock Save-SyncFactorsState {}
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.unchanged.Count | Should -Be 1
            $global:CapturedReport.updates.Count | Should -Be 0
            @($global:CapturedReport.operations.operationType) | Should -Not -Contain 'UpdateAttributes'
            Assert-MockCalled Set-SyncFactorsUserAttributes -Times 0 -Exactly
            Assert-MockCalled Move-SyncFactorsUser -Times 0 -Exactly
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

            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SyncFactorsState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
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
            Mock Get-SyncFactorsTargetUser { $user }
            Mock Get-SyncFactorsAttributeChanges {
                [pscustomobject]@{
                    Changes = @{}
                    MissingRequired = @()
                }
            }
            Mock Enable-SyncFactorsUser {}
            Mock Add-SyncFactorsUserToConfiguredGroups { @('CN=License,OU=Groups,DC=example,DC=com') }
            Mock Set-SyncFactorsWorkerState {
                param($State, $WorkerId, $WorkerState)
                $State.workers | Add-Member -MemberType NoteProperty -Name $WorkerId -Value $WorkerState -Force
            }
            Mock Save-SyncFactorsState {}
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.enables.Count | Should -Be 1
            $global:CapturedReport.enables[0].licensingGroups | Should -Be @('CN=License,OU=Groups,DC=example,DC=com')
            @($global:CapturedReport.operations.operationType) | Should -Contain 'EnableUser'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'AddGroupMembership'
            Assert-MockCalled Enable-SyncFactorsUser -Times 1 -Exactly -ParameterFilter {
                $User.SamAccountName -eq 'tdoe'
            }
            Assert-MockCalled Add-SyncFactorsUserToConfiguredGroups -Times 1 -Exactly -ParameterFilter {
                $User.SamAccountName -eq 'tdoe'
            }
        }
    }

    It 'quarantines workers with missing identity values' {
        InModuleScope Sync {
            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SyncFactorsState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
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
            Mock Save-SyncFactorsState {}
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.quarantined.Count | Should -Be 1
            $global:CapturedReport.quarantined[0].reason | Should -Be 'MissingEmployeeId'
            $global:CapturedReport.quarantined[0].reviewCaseType | Should -Be 'QuarantinedWorker'
            $global:CapturedReport.quarantined[0].operatorActionSummary | Should -Match 'Fix the worker data issue'
        }
    }

    It 'quarantines workers with missing required mapped attributes' {
        InModuleScope Sync {
            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SyncFactorsState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
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
            Mock Get-SyncFactorsTargetUser { $null }
            Mock Get-SyncFactorsAttributeChanges {
                [pscustomobject]@{
                    Changes = @{}
                    MissingRequired = @('firstName', 'lastName')
                }
            }
            Mock Save-SyncFactorsState {}
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.quarantined.Count | Should -Be 1
            $global:CapturedReport.quarantined[0].reason | Should -Be 'MissingRequiredData'
            $global:CapturedReport.quarantined[0].fields | Should -Be @('firstName', 'lastName')
            $global:CapturedReport.quarantined[0].reviewCaseType | Should -Be 'QuarantinedWorker'
            $global:CapturedReport.quarantined[0].operatorActions[1].description | Should -Match 'firstName, lastName'
        }
    }

    It 'flags duplicate AD identity matches as conflicts' {
        InModuleScope Sync {
            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SyncFactorsState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
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
            Mock Get-SyncFactorsTargetUser {
                @(
                    [pscustomobject]@{ SamAccountName = 'a' },
                    [pscustomobject]@{ SamAccountName = 'b' }
                )
            }
            Mock Save-SyncFactorsState {}
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

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

            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SyncFactorsState { $state }
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
            Mock Get-SyncFactorsTargetUser { $null }
            Mock Save-SyncFactorsState {}
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.manualReview.Count | Should -Be 1
            $global:CapturedReport.manualReview[0].reason | Should -Be 'RehireDetected'
            $global:CapturedReport.manualReview[0].reviewCaseType | Should -Be 'RehireCase'
            $global:CapturedReport.manualReview[0].operatorActions.Count | Should -Be 3
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

            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SyncFactorsState { $state }
            Mock Get-SfWorkers { @() }
            Mock Get-SyncFactorsUserByObjectGuid { $user }
            Mock Get-SfWorkerById { $null }
            Mock Get-SyncFactorsUserSnapshot { [pscustomobject]@{ samAccountName = 'sdoe'; objectGuid = '77777777-7777-7777-7777-777777777777' } }
            Mock Remove-SyncFactorsUser {}
            Mock Save-SyncFactorsState {}
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.deletions.Count | Should -Be 1
            @($global:CapturedReport.operations.operationType) | Should -Contain 'DeleteUser'
            Assert-MockCalled Remove-SyncFactorsUser -Times 1 -Exactly -ParameterFilter {
                $User.SamAccountName -eq 'sdoe'
            }
        }
    }

    It 'queues pending deletions for manual approval instead of deleting immediately' {
        InModuleScope Sync {
            $state = [pscustomobject]@{
                checkpoint = '2026-03-05T10:00:00'
                workers = [pscustomobject]@{
                    '9404' = [pscustomobject]@{
                        suppressed = $true
                        deleteAfter = (Get-Date).AddDays(-1).ToString('o')
                        adObjectGuid = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
                    }
                }
            }
            $user = [pscustomobject]@{
                ObjectGuid = [guid]'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
                DistinguishedName = 'CN=Sam Delete,OU=Graveyard,DC=example,DC=com'
                SamAccountName = 'sdelete'
                Enabled = $false
            }

            $global:SyncTestBaseConfig | Add-Member -MemberType NoteProperty -Name approval -Value ([pscustomobject]@{
                enabled = $true
                requireFor = @('DeleteUser')
            }) -Force

            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SyncFactorsState { $state }
            Mock Get-SfWorkers { @() }
            Mock Get-SyncFactorsUserByObjectGuid { $user }
            Mock Get-SfWorkerById { $null }
            Mock Get-SyncFactorsUserSnapshot { [pscustomobject]@{ samAccountName = 'sdelete'; objectGuid = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa' } }
            Mock Remove-SyncFactorsUser {}
            Mock Save-SyncFactorsState {}
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.deletions.Count | Should -Be 0
            $global:CapturedReport.manualReview.Count | Should -Be 1
            $global:CapturedReport.manualReview[0].reviewCaseType | Should -Be 'ApprovalRequired'
            $global:CapturedReport.manualReview[0].approvalActions | Should -Be @('DeleteUser')
            Assert-MockCalled Remove-SyncFactorsUser -Times 0 -Exactly
        }
    }

    It 'allows scoped worker sync to bypass approval mode after operator review' {
        InModuleScope Sync {
            $user = [pscustomobject]@{
                ObjectGuid = [guid]'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
                DistinguishedName = 'CN=Alex Doe,OU=Employees,DC=example,DC=com'
                SamAccountName = 'adoe'
                Enabled = $true
            }

            $global:SyncTestBaseConfig | Add-Member -MemberType NoteProperty -Name approval -Value ([pscustomobject]@{
                enabled = $true
                requireFor = @('DisableUser', 'MoveToGraveyardOu')
            }) -Force

            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SyncFactorsState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
            Mock Get-SfWorkerById {
                [pscustomobject]@{
                    personIdExternal = '2201'
                    employeeId = '2201'
                    status = 'inactive'
                    startDate = (Get-Date).AddDays(-30).ToString('o')
                }
            }
            Mock Get-SyncFactorsTargetUser { $user }
            Mock Get-SyncFactorsWorkerState { $null }
            Mock Disable-SyncFactorsUser {}
            Mock Get-SyncFactorsUserByObjectGuid { $user }
            Mock Move-SyncFactorsUser {}
            Mock Set-SyncFactorsWorkerState {
                param($State, $WorkerId, $WorkerState)
                $State.workers | Add-Member -MemberType NoteProperty -Name $WorkerId -Value $WorkerState -Force
            }
            Mock Save-SyncFactorsState {}
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Full -WorkerId '2201' -BypassApprovalMode | Out-Null

            $global:CapturedReport.disables.Count | Should -Be 1
            $global:CapturedReport.graveyardMoves.Count | Should -Be 1
            $global:CapturedReport.manualReview.Count | Should -Be 0
            Assert-MockCalled Disable-SyncFactorsUser -Times 1 -Exactly
            Assert-MockCalled Move-SyncFactorsUser -Times 1 -Exactly
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

            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SyncFactorsState { $state }
            Mock Get-SfWorkers { @() }
            Mock Get-SyncFactorsUserByObjectGuid {}
            Mock Get-SfWorkerById { $null }
            Mock Remove-SyncFactorsUser {}
            Mock Save-SyncFactorsState {}
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.deletions.Count | Should -Be 0
            $global:CapturedReport.manualReview.Count | Should -Be 0
            Assert-MockCalled Remove-SyncFactorsUser -Times 0 -Exactly
            Assert-MockCalled Get-SyncFactorsUserByObjectGuid -Times 0 -Exactly
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

            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SyncFactorsState { $state }
            Mock Get-SfWorkers { @() }
            Mock Get-SyncFactorsUserByObjectGuid { $user }
            Mock Get-SfWorkerById {
                [pscustomobject]@{
                    personIdExternal = '9403'
                    employeeId = '9403'
                    status = 'active'
                    startDate = (Get-Date).ToString('o')
                }
            }
            Mock Remove-SyncFactorsUser {}
            Mock Save-SyncFactorsState {}
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null

            $global:CapturedReport.manualReview.Count | Should -Be 1
            $global:CapturedReport.manualReview[0].reason | Should -Be 'RehireDetectedBeforeDelete'
            $global:CapturedReport.manualReview[0].reviewCaseType | Should -Be 'RehireCase'
            $global:CapturedReport.manualReview[0].operatorActionSummary | Should -Match 'reuse or restore'
            Assert-MockCalled Remove-SyncFactorsUser -Times 0 -Exactly
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

            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SyncFactorsState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
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
            Mock Get-SyncFactorsTargetUser { $user }
            Mock Get-SyncFactorsAttributeChanges {
                [pscustomobject]@{
                    Changes = @{ title = 'Principal Engineer' }
                    MissingRequired = @()
                }
            }
            Mock Set-SyncFactorsUserAttributes { throw 'AD update failed' }
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            { Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null } | Should -Throw 'AD update failed'

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

            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SyncFactorsState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
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
            Mock Get-SyncFactorsTargetUser { $user }
            Mock Get-SyncFactorsWorkerState { $null }
            Mock Disable-SyncFactorsUser { throw 'should not disable user' }
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            { Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null } | Should -Throw '*maxDisablesPerRun*'

            $global:CapturedReport.status | Should -Be 'Failed'
            $global:CapturedReport.guardrailFailures.Count | Should -Be 1
            $global:CapturedReport.guardrailFailures[0].threshold | Should -Be 'maxDisablesPerRun'
            Assert-MockCalled Disable-SyncFactorsUser -Times 0 -Exactly
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

            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SyncFactorsState { $state }
            Mock Get-SfWorkers { @() }
            Mock Get-SyncFactorsUserByObjectGuid { $user }
            Mock Remove-SyncFactorsUser { throw 'should not delete user' }
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            { Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta | Out-Null } | Should -Throw '*maxDeletionsPerRun*'

            $global:CapturedReport.status | Should -Be 'Failed'
            $global:CapturedReport.guardrailFailures.Count | Should -Be 1
            $global:CapturedReport.guardrailFailures[0].threshold | Should -Be 'maxDeletionsPerRun'
            Assert-MockCalled Remove-SyncFactorsUser -Times 0 -Exactly
        }
    }

    It 'records a first-sync review artifact without mutating AD or sync state' {
        InModuleScope Sync {
            $global:SyncTestBaseConfig.reporting | Add-Member -MemberType NoteProperty -Name reviewOutputDirectory -Value (Join-Path $TestDrive 'reviews') -Force
            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig {
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
            Mock Get-SyncFactorsState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
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
            Mock Get-SyncFactorsTargetUser {
                [pscustomobject]@{
                    ObjectGuid = [guid]'55555555-5555-5555-5555-555555555555'
                    DistinguishedName = 'CN=Jamie Doe,OU=Employees,DC=example,DC=com'
                    SamAccountName = 'jdoe'
                    employeeID = '5001'
                    Enabled = $true
                    GivenName = 'OldJamie'
                }
            }
            Mock Get-SyncFactorsWorkerState { $null }
            Mock Get-SyncFactorsAttributeChanges {
                [pscustomobject]@{
                    Changes = @{ GivenName = 'Jamie' }
                    MissingRequired = @()
                }
            }
            Mock Get-SyncFactorsMappingEvaluation {
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
            Mock Set-SyncFactorsUserAttributes {}
            Mock Save-SyncFactorsState { throw 'state should not be saved in review mode' }
            Mock Set-SyncFactorsWorkerState { throw 'tracked state should not be written in review mode' }
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                $global:CapturedReviewDirectory = $Directory
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Review | Out-Null

            $global:CapturedReviewDirectory | Should -Be $global:SyncTestBaseConfig.reporting.reviewOutputDirectory
            $global:CapturedReport.mode | Should -Be 'Review'
            $global:CapturedReport.artifactType | Should -Be 'FirstSyncReview'
            $global:CapturedReport.dryRun | Should -BeTrue
            $global:CapturedReport.reviewSummary.existingUsersMatched | Should -Be 1
            $global:CapturedReport.reviewSummary.existingUsersWithAttributeChanges | Should -Be 1
            $global:CapturedReport.reviewSummary.deletionPassSkipped | Should -BeTrue
            $global:CapturedReport.updates[0].reviewCategory | Should -Be 'ExistingUserChanges'
            $global:CapturedReport.updates[0].changedAttributeDetails[0].targetAttribute | Should -Be 'GivenName'
            Assert-MockCalled Set-SyncFactorsUserAttributes -Times 1 -Exactly -ParameterFilter { $DryRun }
            Assert-MockCalled Save-SyncFactorsState -Times 0 -Exactly
            Assert-MockCalled Set-SyncFactorsWorkerState -Times 0 -Exactly
            Assert-MockCalled Get-SfWorkers -Times 1 -Exactly
        }
    }

    It 'scopes review mode to one worker when WorkerId is provided' {
        InModuleScope Sync {
            $global:SyncTestBaseConfig.reporting | Add-Member -MemberType NoteProperty -Name reviewOutputDirectory -Value (Join-Path $TestDrive 'reviews') -Force
            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig {
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
            Mock Get-SyncFactorsState { [pscustomobject]@{ checkpoint = '2026-03-05T10:00:00'; workers = [pscustomobject]@{} } }
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
            Mock Get-SyncFactorsTargetUser {
                [pscustomobject]@{
                    ObjectGuid = [guid]'77777777-7777-7777-7777-777777777777'
                    DistinguishedName = 'CN=Jamie Doe,OU=Employees,DC=example,DC=com'
                    SamAccountName = 'jdoe'
                    employeeID = '7001'
                    Enabled = $true
                    GivenName = 'OldJamie'
                }
            }
            Mock Get-SyncFactorsWorkerState { $null }
            Mock Get-SyncFactorsAttributeChanges {
                [pscustomobject]@{
                    Changes = @{ GivenName = 'Jamie' }
                    MissingRequired = @()
                }
            }
            Mock Get-SyncFactorsMappingEvaluation {
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
            Mock Set-SyncFactorsUserAttributes {}
            Mock Invoke-SyncFactorsDeletionPass { throw 'Deletion pass should not run during review preview.' }
            Mock Save-SyncFactorsState { throw 'state should not be saved in scoped preview mode' }
            Mock Set-SyncFactorsWorkerState { throw 'tracked state should not be written in scoped preview mode' }
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                $global:CapturedReviewDirectory = $Directory
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Review -WorkerId '7001' | Out-Null

            $global:CapturedReviewDirectory | Should -Be $global:SyncTestBaseConfig.reporting.reviewOutputDirectory
            $global:CapturedReport.artifactType | Should -Be 'WorkerPreview'
            $global:CapturedReport.workerScope.workerId | Should -Be '7001'
            $global:CapturedReport.workerScope.identityField | Should -Be 'personIdExternal'
            $global:CapturedReport.reviewSummary.existingUsersMatched | Should -Be 1
            $global:CapturedReport.updates[0].changedAttributeDetails[0].targetAttribute | Should -Be 'GivenName'
            Assert-MockCalled Get-SfWorkerById -Times 1 -Exactly -ParameterFilter { $WorkerId -eq '7001' }
            Assert-MockCalled Get-SfWorkers -Times 0 -Exactly
            Assert-MockCalled Invoke-SyncFactorsDeletionPass -Times 0 -Exactly
            Assert-MockCalled Save-SyncFactorsState -Times 0 -Exactly
            Assert-MockCalled Set-SyncFactorsWorkerState -Times 0 -Exactly
            Assert-MockCalled Set-SyncFactorsUserAttributes -Times 1 -Exactly -ParameterFilter { $DryRun }
        }
    }

    It 'allows WorkerId for scoped full sync runs' {
        InModuleScope Sync {
            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SyncFactorsState { [pscustomobject]@{ checkpoint = $null; workers = [pscustomobject]@{} } }
            Mock Get-SfWorkerById {
                [pscustomobject]@{
                    personIdExternal = '7001'
                    status = 'active'
                    startDate = (Get-Date).ToString('o')
                }
            }
            Mock Get-SfWorkers { throw 'Get-SfWorkers should not run for scoped full sync.' }
            Mock Get-SyncFactorsTargetUser { $null }
            Mock Get-SyncFactorsUserBySamAccountName { $null }
            Mock Get-SyncFactorsUserByUserPrincipalName { $null }
            Mock Get-SyncFactorsWorkerState { $null }
            Mock Get-SyncFactorsAttributeChanges { [pscustomobject]@{ Changes = @{}; MissingRequired = @() } }
            Mock New-SyncFactorsUser { [pscustomobject]@{ ObjectGuid = [guid]'11111111-1111-1111-1111-111111111111'; DistinguishedName = 'CN=Jamie Doe,OU=Employees,DC=example,DC=com'; SamAccountName = '7001'; Enabled = $false } }
            Mock Enable-SyncFactorsUser {}
            Mock Add-SyncFactorsUserToConfiguredGroups { @() }
            Mock Set-SyncFactorsWorkerState {}
            Mock Save-SyncFactorsState {}
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Write-SyncFactorsRuntimeStatusSnapshot {}
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Full -WorkerId '7001' | Out-Null

            $global:CapturedReport.artifactType | Should -Be 'WorkerSync'
            $global:CapturedReport.workerScope.workerId | Should -Be '7001'
            Assert-MockCalled Get-SfWorkerById -Times 1 -Exactly -ParameterFilter { $WorkerId -eq '7001' }
            Assert-MockCalled Get-SfWorkers -Times 0 -Exactly
        }
    }

    It 'removes stale tracked worker state when the saved AD object no longer exists' {
        InModuleScope Sync {
            $state = [pscustomobject]@{
                checkpoint = $null
                workers = [pscustomobject]@{
                    '7002' = [pscustomobject]@{
                        adObjectGuid = '88888888-8888-8888-8888-888888888888'
                        distinguishedName = 'CN=Deleted User,OU=Employees,DC=example,DC=com'
                        suppressed = $false
                    }
                }
            }

            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig { [pscustomobject]@{ mappings = @() } }
            Mock Get-SyncFactorsState { $state }
            Mock Get-SfWorkerById {
                [pscustomobject]@{
                    personIdExternal = '7002'
                    status = 'active'
                    startDate = (Get-Date).ToString('o')
                }
            }
            Mock Get-SfWorkers { throw 'Get-SfWorkers should not run for scoped full sync.' }
            Mock Get-SyncFactorsTargetUser { $null }
            Mock Get-SyncFactorsUserByObjectGuid { $null }
            Mock Get-SyncFactorsUserBySamAccountName { $null }
            Mock Get-SyncFactorsUserByUserPrincipalName { $null }
            Mock Get-SyncFactorsAttributeChanges { [pscustomobject]@{ Changes = @{}; MissingRequired = @() } }
            Mock New-SyncFactorsUser {
                [pscustomobject]@{
                    ObjectGuid = [guid]'99999999-9999-9999-9999-999999999999'
                    DistinguishedName = 'CN=Jamie Doe,OU=Employees,DC=example,DC=com'
                    SamAccountName = '7002'
                    Enabled = $false
                }
            }
            Mock Enable-SyncFactorsUser {}
            Mock Add-SyncFactorsUserToConfiguredGroups { @() }
            Mock Save-SyncFactorsState {}
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Write-SyncFactorsRuntimeStatusSnapshot {}
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Full -WorkerId '7002' | Out-Null

            $global:CapturedReport.artifactType | Should -Be 'WorkerSync'
            @($global:CapturedReport.operations.operationType) | Should -Contain 'SetWorkerState'
            Assert-MockCalled Get-SyncFactorsUserByObjectGuid -Times 1 -Exactly -ParameterFilter { $ObjectGuid -eq '88888888-8888-8888-8888-888888888888' }
            $state.workers.PSObject.Properties.Name | Should -Contain '7002'
            $state.workers.'7002'.adObjectGuid | Should -Be '99999999-9999-9999-9999-999999999999'
            $state.workers.'7002'.distinguishedName | Should -Be 'CN=Jamie Doe,OU=Employees,DC=example,DC=com'
        }
    }

    It 'still rejects WorkerId outside full or review mode' {
        InModuleScope Sync {
            { Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Delta -WorkerId '7001' } | Should -Throw '-WorkerId is only supported with -Mode Full or -Mode Review.'
        }
    }

    It 'counts matched review users that land in quarantine or manual review' {
        InModuleScope Sync {
            $global:SyncTestBaseConfig.reporting | Add-Member -MemberType NoteProperty -Name reviewOutputDirectory -Value (Join-Path $TestDrive 'reviews') -Force
            Mock Get-SyncFactorsConfig { $global:SyncTestBaseConfig }
            Mock Get-SyncFactorsMappingConfig {
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
            Mock Get-SyncFactorsState {
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
            Mock Get-SyncFactorsTargetUser {
                [pscustomobject]@{
                    ObjectGuid = [guid]'66666666-6666-6666-6666-666666666666'
                    DistinguishedName = 'CN=Existing User,OU=Employees,DC=example,DC=com'
                    SamAccountName = 'existing.user'
                    employeeID = '6001'
                    Enabled = $true
                    GivenName = 'Existing'
                }
            } -ParameterFilter { $WorkerId -eq '6001' }
            Mock Get-SyncFactorsTargetUser { $null } -ParameterFilter { $WorkerId -eq '6002' }
            Mock Get-SyncFactorsWorkerState { $null } -ParameterFilter { $WorkerId -eq '6001' }
            Mock Get-SyncFactorsWorkerState {
                [pscustomobject]@{
                    suppressed = $true
                    distinguishedName = 'CN=Rehire,OU=Employees,DC=example,DC=com'
                }
            } -ParameterFilter { $WorkerId -eq '6002' }
            Mock Get-SyncFactorsAttributeChanges {
                [pscustomobject]@{
                    Changes = @{}
                    MissingRequired = @('firstName')
                }
            }
            Mock Get-SyncFactorsMappingEvaluation {
                [pscustomobject]@{
                    Changes = @{}
                    MissingRequired = @('firstName')
                    Rows = @()
                }
            }
            Mock Save-SyncFactorsReport {
                param($Report, $Directory, $Mode)
                $global:CapturedReport = $Report
                return (Join-Path $Directory "syncfactors-$Mode.json")
            }
            Mock Ensure-ActiveDirectoryModule {}

            Invoke-SyncFactorsRun -ConfigPath $global:SyncTestConfigPath -MappingConfigPath $global:SyncTestMappingConfigPath -Mode Review | Out-Null

            $global:CapturedReport.reviewSummary.existingUsersMatched | Should -Be 2
            $global:CapturedReport.reviewSummary.operatorActionCases.quarantinedWorkers | Should -Be 1
            $global:CapturedReport.reviewSummary.operatorActionCases.unresolvedManagers | Should -Be 0
            $global:CapturedReport.reviewSummary.operatorActionCases.rehireCases | Should -Be 1
            $global:CapturedReport.quarantined[0].matchedExistingUser | Should -BeTrue
            $global:CapturedReport.manualReview[0].matchedExistingUser | Should -BeTrue
        }
    }
}

Describe 'Test-SyncFactorsPreflight' {
    BeforeAll {
        Import-Module "$PSScriptRoot/../src/Modules/SyncFactors/Sync.psm1" -Force -DisableNameChecking
    }

    It 'loads config and mapping metadata without performing a sync' {
        InModuleScope Sync {
            Mock Get-SyncFactorsConfig {
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
            Mock Get-SyncFactorsMappingConfig {
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

            $result = Test-SyncFactorsPreflight -ConfigPath $configPath -MappingConfigPath $mappingPath

            $result.success | Should -BeTrue
            $result.identityField | Should -Be 'personIdExternal'
            $result.identityAttribute | Should -Be 'employeeID'
            $result.mappingCount | Should -Be 1
        }
    }
}
