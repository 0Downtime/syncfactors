Describe 'Demo data generator integration' {
    It 'creates demo config, state, runtime status, and mixed-history reports' {
        $outputDirectory = Join-Path $TestDrive 'demo-output'

        $result = & "$PSScriptRoot/../scripts/Invoke-SyncFactorsDemoData.ps1" `
            -OutputDirectory $outputDirectory `
            -UserCount 40 `
            -AsJson | ConvertFrom-Json -Depth 30

        $result.reportCount | Should -BeGreaterOrEqual 5
        Test-Path -Path $result.configPath | Should -BeTrue
        Test-Path -Path $result.statePath | Should -BeTrue
        Test-Path -Path $result.runtimeStatusPath | Should -BeTrue

        $reports = @(
            Get-ChildItem -Path $result.reportDirectory -Filter 'syncfactors-*.json' -File
            Get-ChildItem -Path $result.reviewReportDirectory -Filter 'syncfactors-*.json' -File
        )

        $reports.Count | Should -Be $result.reportCount

        $runtimeStatus = Get-Content -Path $result.runtimeStatusPath -Raw | ConvertFrom-Json -Depth 20
        $runtimeStatus.status | Should -Be 'InProgress'
        $runtimeStatus.stage | Should -Be 'ProcessingWorkers'
        $runtimeStatus.processedWorkers | Should -BeGreaterThan 0

        $state = Get-Content -Path $result.statePath -Raw | ConvertFrom-Json -Depth 20
        $state.checkpoint | Should -Not -BeNullOrEmpty
        @($state.workers.PSObject.Properties).Count | Should -Be 40
        (@($state.workers.PSObject.Properties | Where-Object { $_.Value.suppressed })).Count | Should -BeGreaterThan 0

        $reportDocuments = @($reports | ForEach-Object { Get-Content -Path $_.FullName -Raw | ConvertFrom-Json -Depth 30 })
        (@($reportDocuments | Where-Object { @($_.manualReview).Count -gt 0 })).Count | Should -BeGreaterThan 0
        (@($reportDocuments | Where-Object { @($_.quarantined).Count -gt 0 })).Count | Should -BeGreaterThan 0
        (@($reportDocuments | Where-Object { @($_.conflicts).Count -gt 0 })).Count | Should -BeGreaterThan 0
        (@($reportDocuments | Where-Object { @($_.guardrailFailures).Count -gt 0 })).Count | Should -BeGreaterThan 0

        $workerIdCounts = @{}
        foreach ($report in $reportDocuments) {
            foreach ($bucket in @('creates', 'updates', 'manualReview', 'quarantined', 'conflicts', 'guardrailFailures', 'unchanged')) {
                foreach ($entry in @($report.$bucket)) {
                    if (-not $entry.workerId) {
                        continue
                    }

                    if (-not $workerIdCounts.ContainsKey($entry.workerId)) {
                        $workerIdCounts[$entry.workerId] = 0
                    }

                    $workerIdCounts[$entry.workerId] += 1
                }
            }
        }

        (@($workerIdCounts.GetEnumerator() | Where-Object { $_.Value -gt 1 })).Count | Should -BeGreaterThan 0
    }

    It 'writes an idle runtime snapshot when IncludeActiveRun is disabled' {
        $outputDirectory = Join-Path $TestDrive 'demo-output-idle'

        $result = & "$PSScriptRoot/../scripts/Invoke-SyncFactorsDemoData.ps1" `
            -OutputDirectory $outputDirectory `
            -UserCount 30 `
            -IncludeActiveRun:$false `
            -AsJson | ConvertFrom-Json -Depth 20

        $runtimeStatus = Get-Content -Path $result.runtimeStatusPath -Raw | ConvertFrom-Json -Depth 20

        $runtimeStatus.status | Should -Be 'Idle'
        $runtimeStatus.lastAction | Should -Be 'No active sync run.'
    }
}
