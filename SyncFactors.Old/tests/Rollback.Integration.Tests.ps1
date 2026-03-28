Describe 'Invoke-SyncFactorsRollback' {
    BeforeAll {
        Import-Module "$PSScriptRoot/../src/Modules/SyncFactors/State.psm1" -Force -DisableNameChecking
        Import-Module "$PSScriptRoot/../src/Modules/SyncFactors/ActiveDirectorySync.psm1" -Force -DisableNameChecking
        Import-Module "$PSScriptRoot/../src/Modules/SyncFactors/Rollback.psm1" -Force -DisableNameChecking
    }

    BeforeEach {
        $global:RollbackCalls = @()
        $configPath = Join-Path $TestDrive 'sync-config.json'
        $reportPath = Join-Path $TestDrive 'report.json'
        $statePath = Join-Path $TestDrive 'state.json'

        Set-Content -Path $configPath -Value '{}'
        Set-Content -Path $statePath -Value (@{
            checkpoint = '2026-03-06T09:00:00'
            workers = @{
                '1001' = @{
                    suppressed = $true
                }
            }
        } | ConvertTo-Json -Depth 10)

        $report = [ordered]@{
            runId = 'run-123'
            configPath = $configPath
            statePath = $statePath
            operations = @(
                [pscustomobject]@{
                    sequence = 1
                    operationType = 'SetCheckpoint'
                    workerId = '__checkpoint__'
                    status = 'Applied'
                    before = '2026-03-05T09:00:00'
                    after = '2026-03-06T09:00:00'
                },
                [pscustomobject]@{
                    sequence = 2
                    operationType = 'SetWorkerState'
                    workerId = '1001'
                    status = 'Applied'
                    before = $null
                    after = [pscustomobject]@{ suppressed = $true }
                },
                [pscustomobject]@{
                    sequence = 3
                    operationType = 'UpdateAttributes'
                    workerId = '1001'
                    status = 'Applied'
                    target = [pscustomobject]@{ objectGuid = '33333333-3333-3333-3333-333333333333' }
                    before = [pscustomobject]@{ title = 'Old Title' }
                    after = [pscustomobject]@{ title = 'New Title' }
                },
                [pscustomobject]@{
                    sequence = 4
                    operationType = 'AddGroupMembership'
                    workerId = '1001'
                    status = 'Applied'
                    target = [pscustomobject]@{ objectGuid = '33333333-3333-3333-3333-333333333333' }
                    after = [pscustomobject]@{ groupsAdded = @('CN=License,OU=Groups,DC=example,DC=com') }
                }
            )
        }

        $report | ConvertTo-Json -Depth 20 | Set-Content -Path $reportPath

        $global:RollbackTestConfigPath = $configPath
        $global:RollbackTestReportPath = $reportPath
        $global:RollbackTestStatePath = $statePath
        $global:RollbackTestConfig = [pscustomobject]@{
            ad = [pscustomobject]@{
                identityAttribute = 'employeeID'
                defaultActiveOu = 'OU=Employees,DC=example,DC=com'
                defaultPassword = 'Password123!'
            }
            state = [pscustomobject]@{
                path = $statePath
            }
        }
    }

    It 'replays operations in reverse order and restores state' {
        InModuleScope Rollback {
            $user = [pscustomobject]@{
                ObjectGuid = [guid]'33333333-3333-3333-3333-333333333333'
                DistinguishedName = 'CN=Jamie Doe,OU=Employees,DC=example,DC=com'
                SamAccountName = '1001'
                Enabled = $true
            }

            Mock Get-SyncFactorsConfig { $global:RollbackTestConfig }
            Mock Get-SyncFactorsState { Get-Content -Path $global:RollbackTestStatePath -Raw | ConvertFrom-Json -Depth 20 }
            Mock Get-SyncFactorsUserByObjectGuid { $user }
            Mock Get-SyncFactorsTargetUser { $user }
            Mock Remove-SyncFactorsUserFromGroups { param($User, $Groups) $global:RollbackCalls += "remove-group:$($Groups[0])" }
            Mock Set-SyncFactorsUserAttributes { param($User, $Changes) $global:RollbackCalls += "restore-title:$($Changes['title'])" }
            Mock Remove-SyncFactorsWorkerState { param($State, $WorkerId) $global:RollbackCalls += "remove-state:$WorkerId"; [void]$State.workers.PSObject.Properties.Remove($WorkerId) }
            Mock Save-SyncFactorsState { param($State, $Path) $global:RollbackCalls += "save-state:$($State.checkpoint)"; $State | ConvertTo-Json -Depth 20 | Set-Content -Path $Path }

            Invoke-SyncFactorsRollback -ReportPath $global:RollbackTestReportPath -ConfigPath $global:RollbackTestConfigPath

            $global:RollbackCalls[0] | Should -Be 'remove-group:CN=License,OU=Groups,DC=example,DC=com'
            $global:RollbackCalls[1] | Should -Be 'restore-title:Old Title'
            $global:RollbackCalls[2] | Should -Be 'remove-state:1001'
            $global:RollbackCalls[3] | Should -Be 'save-state:2026-03-05T09:00:00'

            $savedState = Get-Content -Path $global:RollbackTestStatePath -Raw | ConvertFrom-Json -Depth 20
            $savedCheckpoint = if ($savedState.checkpoint -is [datetime]) {
                $savedState.checkpoint.ToString('yyyy-MM-ddTHH:mm:ss')
            } else {
                "$($savedState.checkpoint)"
            }
            $savedCheckpoint | Should -Be '2026-03-05T09:00:00'
            $savedWorkerIds = if ($savedState.workers -is [System.Collections.IDictionary]) {
                @($savedState.workers.Keys)
            } else {
                @($savedState.workers.PSObject.Properties | ForEach-Object { $_.Name })
            }
            ($savedWorkerIds -contains '1001') | Should -BeFalse
        }
    }

    It 'stops rollback and does not save state when an operation fails' {
        InModuleScope Rollback {
            $user = [pscustomobject]@{
                ObjectGuid = [guid]'33333333-3333-3333-3333-333333333333'
                DistinguishedName = 'CN=Jamie Doe,OU=Employees,DC=example,DC=com'
                SamAccountName = '1001'
                Enabled = $true
            }

            Mock Get-SyncFactorsConfig { $global:RollbackTestConfig }
            Mock Get-SyncFactorsState { Get-Content -Path $global:RollbackTestStatePath -Raw | ConvertFrom-Json -Depth 20 }
            Mock Get-SyncFactorsUserByObjectGuid { $user }
            Mock Get-SyncFactorsTargetUser { $user }
            Mock Remove-SyncFactorsUserFromGroups { throw 'rollback group removal failed' }
            Mock Save-SyncFactorsState { throw 'should not save state' }

            { Invoke-SyncFactorsRollback -ReportPath $global:RollbackTestReportPath -ConfigPath $global:RollbackTestConfigPath } | Should -Throw 'rollback group removal failed'

            Assert-MockCalled Save-SyncFactorsState -Times 0 -Exactly
        }
    }

    It 'requires a config path when neither the argument nor report provides one' {
        InModuleScope Rollback {
            $reportPath = Join-Path $TestDrive 'report-missing-config.json'
            @{
                runId = 'run-missing-config'
                operations = @()
                statePath = $global:RollbackTestStatePath
            } | ConvertTo-Json -Depth 20 | Set-Content -Path $reportPath

            { Invoke-SyncFactorsRollback -ReportPath $reportPath } | Should -Throw 'ConfigPath is required when the report does not include it.'
        }
    }
}
