Describe 'Script entrypoints' {
    BeforeAll {
        function New-StatusConfigContent {
            param(
                [Parameter(Mandatory)]
                [string]$StatePath,
                [Parameter(Mandatory)]
                [string]$ReportDirectory
            )
            return @{
                successFactors = @{
                    baseUrl = 'https://example.successfactors.com/odata/v2'
                    oauth = @{
                        tokenUrl = 'https://example.successfactors.com/oauth/token'
                        clientId = 'client-id'
                        clientSecret = 'client-secret'
                    }
                    query = @{
                        entitySet = 'PerPerson'
                        identityField = 'personIdExternal'
                        deltaField = 'lastModifiedDateTime'
                        select = @('personIdExternal')
                        expand = @('employmentNav')
                    }
                }
                ad = @{
                    identityAttribute = 'employeeID'
                    defaultActiveOu = 'OU=Employees,DC=example,DC=com'
                    graveyardOu = 'OU=Graveyard,DC=example,DC=com'
                    defaultPassword = 'password'
                }
                sync = @{
                    enableBeforeStartDays = 7
                    deletionRetentionDays = 90
                }
                state = @{
                    path = $StatePath
                }
                reporting = @{
                    outputDirectory = $ReportDirectory
                }
            } | ConvertTo-Json -Depth 10
        }
    }

    It 'delegates the main sync entry script to Invoke-SfAdSyncRun' {
        Import-Module "$PSScriptRoot/../src/Modules/SfAdSync/Sync.psm1" -Force -DisableNameChecking
        Mock Invoke-SfAdSyncRun { 'report.json' }

        & "$PSScriptRoot/../src/Invoke-SfAdSync.ps1" -ConfigPath 'config.json' -MappingConfigPath 'mapping.json' -Mode Full -DryRun

        Assert-MockCalled Invoke-SfAdSyncRun -Times 1 -Exactly -ParameterFilter {
            $ConfigPath -eq 'config.json' -and
            $MappingConfigPath -eq 'mapping.json' -and
            $Mode -eq 'Full' -and
            $DryRun
        }
    }

    It 'delegates the rollback entry script to Invoke-SfAdRollback' {
        Import-Module "$PSScriptRoot/../src/Modules/SfAdSync/Rollback.psm1" -Force -DisableNameChecking
        Mock Invoke-SfAdRollback {}

        & "$PSScriptRoot/../scripts/Undo-SfAdSyncRun.ps1" -ReportPath 'report.json' -ConfigPath 'config.json' -DryRun

        Assert-MockCalled Invoke-SfAdRollback -Times 1 -Exactly -ParameterFilter {
            $ReportPath -eq 'report.json' -and
            $ConfigPath -eq 'config.json' -and
            $DryRun
        }
    }

    It 'returns preflight details from the preflight script in json mode' {
        Import-Module "$PSScriptRoot/../src/Modules/SfAdSync/Sync.psm1" -Force -DisableNameChecking
        Mock Test-SfAdSyncPreflight {
            [pscustomobject]@{
                success = $true
                configPath = 'config.json'
                mappingConfigPath = 'mapping.json'
                identityField = 'personIdExternal'
                identityAttribute = 'employeeID'
                statePath = 'state.json'
                stateDirectoryExists = $true
                reportDirectory = 'reports'
                reportDirectoryExists = $true
                mappingCount = 3
            }
        }

        $result = & "$PSScriptRoot/../scripts/Invoke-SfAdPreflight.ps1" -ConfigPath 'config.json' -MappingConfigPath 'mapping.json' -AsJson | ConvertFrom-Json

        $result.success | Should -BeTrue
        $result.mappingCount | Should -Be 3
    }

    It 'returns sync status from local state and report files in json mode' {
        $configPath = Join-Path $TestDrive 'status-config.json'
        $statePath = Join-Path $TestDrive 'state.json'
        $reportDir = Join-Path $TestDrive 'reports'
        $reportPath = Join-Path $reportDir 'sf-ad-sync-Delta-20260312-220000.json'
        $olderReportPath = Join-Path $reportDir 'sf-ad-sync-Full-20260312-210000.json'
        $runtimeStatusPath = Join-Path $TestDrive 'runtime-status.json'

        New-Item -Path $reportDir -ItemType Directory -Force | Out-Null
        (New-StatusConfigContent -StatePath $statePath -ReportDirectory $reportDir) | Set-Content -Path $configPath

        @{
            checkpoint = '2026-03-12T21:00:00'
            workers = @{
                '1001' = @{
                    suppressed = $true
                    deleteAfter = (Get-Date).AddDays(-1).ToString('o')
                }
                '1002' = @{
                    suppressed = $false
                }
            }
        } | ConvertTo-Json -Depth 10 | Set-Content -Path $statePath

        @{
            runId = 'run-123'
            mode = 'Delta'
            dryRun = $false
            startedAt = '2026-03-12T21:30:00'
            completedAt = '2026-03-12T21:35:00'
            status = 'Succeeded'
            operations = @(@{ operationType = 'CreateUser' })
            creates = @(@{})
            updates = @()
            enables = @()
            disables = @()
            graveyardMoves = @()
            deletions = @()
            quarantined = @()
            conflicts = @()
            guardrailFailures = @()
            manualReview = @()
            unchanged = @()
        } | ConvertTo-Json -Depth 10 | Set-Content -Path $reportPath
        @{
            runId = 'run-122'
            mode = 'Full'
            dryRun = $true
            startedAt = '2026-03-12T20:30:00'
            completedAt = '2026-03-12T20:40:00'
            status = 'Failed'
            operations = @()
            creates = @()
            updates = @()
            enables = @()
            disables = @(@{})
            graveyardMoves = @()
            deletions = @()
            quarantined = @()
            conflicts = @(@{})
            guardrailFailures = @(@{})
            manualReview = @()
            unchanged = @()
        } | ConvertTo-Json -Depth 10 | Set-Content -Path $olderReportPath
        @{
            runId = 'run-active'
            status = 'InProgress'
            mode = 'Delta'
            dryRun = $true
            stage = 'ProcessingWorkers'
            startedAt = '2026-03-12T21:40:00'
            lastUpdatedAt = '2026-03-12T21:41:00'
            completedAt = $null
            currentWorkerId = '1002'
            lastAction = 'Updated attributes for worker 1002.'
            processedWorkers = 3
            totalWorkers = 5
            creates = 1
            updates = 1
            enables = 0
            disables = 0
            graveyardMoves = 0
            deletions = 0
            quarantined = 0
            conflicts = 0
            guardrailFailures = 0
            manualReview = 0
            unchanged = 1
            errorMessage = $null
        } | ConvertTo-Json -Depth 10 | Set-Content -Path $runtimeStatusPath

        $result = & "$PSScriptRoot/../scripts/Get-SfAdSyncStatus.ps1" -ConfigPath $configPath -AsJson | ConvertFrom-Json -Depth 10

        $result.totalTrackedWorkers | Should -Be 2
        $result.suppressedWorkers | Should -Be 1
        $result.pendingDeletionWorkers | Should -Be 1
        $result.latestReport.status | Should -Be 'Succeeded'
        $result.latestReport.creates | Should -Be 1
        $result.latestRun.mode | Should -Be 'Delta'
        $result.currentRun.status | Should -Be 'InProgress'
        $result.currentRun.stage | Should -Be 'ProcessingWorkers'
        $result.currentRun.currentWorkerId | Should -Be '1002'
        @($result.recentRuns).Count | Should -Be 2
        $result.recentRuns[1].guardrailFailures | Should -Be 1
        $result.paths.runtimeStatusPath | Should -Be $runtimeStatusPath
    }

    It 'returns zeroed latest report details when no report files exist' {
        $configPath = Join-Path $TestDrive 'status-config-empty.json'
        $emptyRoot = Join-Path $TestDrive 'empty-status'
        $statePath = Join-Path $emptyRoot 'state-empty.json'
        $reportDir = Join-Path $emptyRoot 'reports-empty'

        New-Item -Path $emptyRoot -ItemType Directory -Force | Out-Null
        New-Item -Path $reportDir -ItemType Directory -Force | Out-Null
        (New-StatusConfigContent -StatePath $statePath -ReportDirectory $reportDir) | Set-Content -Path $configPath
        @{ checkpoint = $null; workers = @{} } | ConvertTo-Json -Depth 10 | Set-Content -Path $statePath

        $result = & "$PSScriptRoot/../scripts/Get-SfAdSyncStatus.ps1" -ConfigPath $configPath -AsJson | ConvertFrom-Json -Depth 10

        $result.totalTrackedWorkers | Should -Be 0
        $result.latestReport.path | Should -Be $null
        $result.latestReport.creates | Should -Be 0
        $result.latestReport.status | Should -Be $null
        $result.currentRun.status | Should -Be 'Idle'
        @($result.recentRuns).Count | Should -Be 0
    }

    It 'throws when the state json is corrupt' {
        $configPath = Join-Path $TestDrive 'status-config-corrupt-state.json'
        $statePath = Join-Path $TestDrive 'state-corrupt.json'
        $reportDir = Join-Path $TestDrive 'reports-corrupt-state'

        New-Item -Path $reportDir -ItemType Directory -Force | Out-Null
        (New-StatusConfigContent -StatePath $statePath -ReportDirectory $reportDir) | Set-Content -Path $configPath
        '{bad json' | Set-Content -Path $statePath

        { & "$PSScriptRoot/../scripts/Get-SfAdSyncStatus.ps1" -ConfigPath $configPath -AsJson | Out-Null } | Should -Throw
    }

    It 'throws when the latest report json is corrupt' {
        $configPath = Join-Path $TestDrive 'status-config-corrupt-report.json'
        $statePath = Join-Path $TestDrive 'state-valid.json'
        $reportDir = Join-Path $TestDrive 'reports-corrupt-report'
        $reportPath = Join-Path $reportDir 'sf-ad-sync-Delta-corrupt.json'

        New-Item -Path $reportDir -ItemType Directory -Force | Out-Null
        (New-StatusConfigContent -StatePath $statePath -ReportDirectory $reportDir) | Set-Content -Path $configPath
        @{ checkpoint = $null; workers = @{} } | ConvertTo-Json -Depth 10 | Set-Content -Path $statePath
        '{bad json' | Set-Content -Path $reportPath

        { & "$PSScriptRoot/../scripts/Get-SfAdSyncStatus.ps1" -ConfigPath $configPath -AsJson | Out-Null } | Should -Throw
    }

    It 'renders the monitor view in text mode with current and recent run details' {
        $configPath = Join-Path $TestDrive 'monitor-config.json'
        $statePath = Join-Path $TestDrive 'monitor-state.json'
        $reportDir = Join-Path $TestDrive 'monitor-reports'
        $reportPath = Join-Path $reportDir 'sf-ad-sync-Delta-20260312-220000.json'
        $runtimeStatusPath = Join-Path $TestDrive 'runtime-status.json'

        New-Item -Path $reportDir -ItemType Directory -Force | Out-Null
        (New-StatusConfigContent -StatePath $statePath -ReportDirectory $reportDir) | Set-Content -Path $configPath
        @{ checkpoint = '2026-03-12T21:00:00'; workers = @{} } | ConvertTo-Json -Depth 10 | Set-Content -Path $statePath
        @{
            runId = 'run-123'
            mode = 'Delta'
            dryRun = $false
            startedAt = '2026-03-12T21:30:00'
            completedAt = '2026-03-12T21:35:00'
            status = 'Succeeded'
            operations = @(@{ operationType = 'CreateUser' })
            creates = @(@{})
            updates = @()
            enables = @()
            disables = @()
            graveyardMoves = @()
            deletions = @()
            quarantined = @()
            conflicts = @()
            guardrailFailures = @()
            manualReview = @()
            unchanged = @()
        } | ConvertTo-Json -Depth 10 | Set-Content -Path $reportPath
        @{
            runId = 'run-active'
            status = 'InProgress'
            mode = 'Delta'
            dryRun = $false
            stage = 'ProcessingWorkers'
            startedAt = '2026-03-12T21:40:00'
            lastUpdatedAt = '2026-03-12T21:41:00'
            completedAt = $null
            currentWorkerId = '1002'
            lastAction = 'Updated attributes for worker 1002.'
            processedWorkers = 3
            totalWorkers = 5
            creates = 1
            updates = 1
            enables = 0
            disables = 0
            graveyardMoves = 0
            deletions = 0
            quarantined = 0
            conflicts = 0
            guardrailFailures = 0
            manualReview = 0
            unchanged = 1
            errorMessage = $null
        } | ConvertTo-Json -Depth 10 | Set-Content -Path $runtimeStatusPath

        $result = & "$PSScriptRoot/../scripts/Watch-SfAdSyncMonitor.ps1" -ConfigPath $configPath -RunOnce -AsText

        $result | Should -Match 'SuccessFactors AD Sync Monitor'
        $result | Should -Match 'Stage: ProcessingWorkers'
        $result | Should -Match 'Updated attributes for worker 1002'
        $result | Should -Match 'Succeeded'
    }

    It 'renders an error banner when the monitor hits corrupt runtime status json' {
        $configPath = Join-Path $TestDrive 'monitor-error-config.json'
        $statePath = Join-Path $TestDrive 'monitor-error-state.json'
        $reportDir = Join-Path $TestDrive 'monitor-error-reports'
        $runtimeStatusPath = Join-Path $TestDrive 'runtime-status.json'

        New-Item -Path $reportDir -ItemType Directory -Force | Out-Null
        (New-StatusConfigContent -StatePath $statePath -ReportDirectory $reportDir) | Set-Content -Path $configPath
        @{ checkpoint = $null; workers = @{} } | ConvertTo-Json -Depth 10 | Set-Content -Path $statePath
        '{bad json' | Set-Content -Path $runtimeStatusPath

        $result = & "$PSScriptRoot/../scripts/Watch-SfAdSyncMonitor.ps1" -ConfigPath $configPath -RunOnce -AsText

        $result | Should -Match 'Monitor error'
    }

    It 'runs the synthetic dry-run entry script as a bounded smoke test' {
        $outputDirectory = Join-Path $TestDrive 'synthetic-output'
        $expectedReportPath = Join-Path $outputDirectory 'synthetic-report.json'
        $reportPathFile = Join-Path $outputDirectory 'synthetic-report-path.txt'

        New-Item -Path $outputDirectory -ItemType Directory -Force | Out-Null
        @'
{
  "status": "Succeeded",
  "creates": [],
  "updates": [],
  "disables": [],
  "deletions": [],
  "conflicts": [],
  "guardrailFailures": [],
  "quarantined": [],
  "unchanged": []
}
'@ | Set-Content -Path $expectedReportPath
        $expectedReportPath | Set-Content -Path $reportPathFile

        Mock Invoke-Pester { [pscustomobject]@{ FailedCount = 0 } }

        $result = & "$PSScriptRoot/../scripts/Invoke-SyntheticSfAdDryRun.ps1" -UserCount 10 -ManagerCount 2 -OutputDirectory $outputDirectory -AsJson | ConvertFrom-Json -Depth 10

        $result.userCount | Should -Be 10
        $result.managerCount | Should -Be 2
        $result.reportPath | Should -Be $expectedReportPath
        Test-Path -Path (Join-Path $outputDirectory 'SyntheticDryRunHarness.Tests.ps1') | Should -BeTrue
        Assert-MockCalled Invoke-Pester -Times 1 -Exactly
    }
}
