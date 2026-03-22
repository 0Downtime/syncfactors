Describe 'New-SyncFactorsSyntheticWorkers' {
    BeforeAll {
        Import-Module "$PSScriptRoot/../src/Modules/SyncFactors/SyntheticHarness.psm1" -Force
    }

    It 'generates the requested number of workers with duplicates included in the total count' {
        $syntheticDirectory = New-SyncFactorsSyntheticWorkers -UserCount 25 -DuplicateWorkerIdCount 5
        $workers = @($syntheticDirectory.workers)

        $workers.Count | Should -Be 25
        (@($workers.personIdExternal | Group-Object | Where-Object Count -gt 1)).Count | Should -Be 5
    }

    It 'marks the requested number of inactive workers' {
        $syntheticDirectory = New-SyncFactorsSyntheticWorkers -UserCount 20 -InactiveCount 4
        $workers = @($syntheticDirectory.workers)

        (@($workers | Where-Object status -eq 'inactive')).Count | Should -Be 4
    }

    It 'assigns manager employee ids from the synthetic manager directory' {
        $syntheticDirectory = New-SyncFactorsSyntheticWorkers -UserCount 20 -ManagerCount 4
        $workers = @($syntheticDirectory.workers)
        $managers = @($syntheticDirectory.managers)

        $managers.Count | Should -Be 4
        (@($workers | Where-Object { [string]::IsNullOrWhiteSpace("$($_.managerEmployeeId)") })).Count | Should -Be 0
        (@($workers | Where-Object { $managers.employeeId -contains $_.managerEmployeeId })).Count | Should -Be 20
    }

    It 'builds reusable worker profiles and tracked worker state for demo artifacts' {
        $syntheticDirectory = New-SyncFactorsSyntheticWorkers -UserCount 5 -InactiveCount 1 -ManagerCount 2
        $worker = @($syntheticDirectory.workers)[0]

        $profile = Get-SyncFactorsSyntheticWorkerProfile -Worker $worker
        $trackedState = New-SyncFactorsSyntheticTrackedWorkerState -Worker $worker -Suppressed -PendingDeletion

        $profile.workerId | Should -Be "$($worker.personIdExternal)"
        $profile.samAccountName | Should -Match '^sf\d+$'
        $profile.targetOu | Should -Match '^OU='
        $profile.distinguishedName | Should -Match '^CN='
        $trackedState.suppressed | Should -BeTrue
        $trackedState.deleteAfter | Should -Not -BeNullOrEmpty
        $trackedState.distinguishedName | Should -Be $profile.distinguishedName
    }

    It 'builds changed attribute details and review operator actions for demo entries' {
        $syntheticDirectory = New-SyncFactorsSyntheticWorkers -UserCount 5 -ManagerCount 2
        $worker = @($syntheticDirectory.workers)[0]

        $changes = @(New-SyncFactorsSyntheticChangedAttributeDetails -Worker $worker -Scenario 'RehireCleanup')
        $rows = @(New-SyncFactorsSyntheticAttributeRows -ChangedAttributeDetails $changes)
        $actions = @(New-SyncFactorsSyntheticOperatorActions -ReviewCaseType 'RehireCase')

        $changes.Count | Should -BeGreaterThan 0
        $rows.Count | Should -Be $changes.Count
        (@($rows | Where-Object changed)).Count | Should -Be $rows.Count
        $actions.Count | Should -BeGreaterThan 0
        $actions[0].label | Should -Not -BeNullOrEmpty
    }
}
