Describe 'New-SfAdSyntheticWorkers' {
    BeforeAll {
        Import-Module "$PSScriptRoot/../src/Modules/SfAdSync/SyntheticHarness.psm1" -Force
    }

    It 'generates the requested number of workers with duplicates included in the total count' {
        $syntheticDirectory = New-SfAdSyntheticWorkers -UserCount 25 -DuplicateWorkerIdCount 5
        $workers = @($syntheticDirectory.workers)

        $workers.Count | Should -Be 25
        (@($workers.personIdExternal | Group-Object | Where-Object Count -gt 1)).Count | Should -Be 5
    }

    It 'marks the requested number of inactive workers' {
        $syntheticDirectory = New-SfAdSyntheticWorkers -UserCount 20 -InactiveCount 4
        $workers = @($syntheticDirectory.workers)

        (@($workers | Where-Object status -eq 'inactive')).Count | Should -Be 4
    }

    It 'assigns manager employee ids from the synthetic manager directory' {
        $syntheticDirectory = New-SfAdSyntheticWorkers -UserCount 20 -ManagerCount 4
        $workers = @($syntheticDirectory.workers)
        $managers = @($syntheticDirectory.managers)

        $managers.Count | Should -Be 4
        (@($workers | Where-Object { [string]::IsNullOrWhiteSpace("$($_.managerEmployeeId)") })).Count | Should -Be 0
        (@($workers | Where-Object { $managers.employeeId -contains $_.managerEmployeeId })).Count | Should -Be 20
    }
}
