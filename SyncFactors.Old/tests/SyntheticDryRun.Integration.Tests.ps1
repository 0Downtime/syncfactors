Describe 'Synthetic dry-run integration' {
    It 'runs the synthetic dry-run entry script end to end and returns a json summary' {
        $outputDirectory = Join-Path $TestDrive 'synthetic-output'

        $result = & "$PSScriptRoot/../scripts/Invoke-SyntheticSyncFactorsDryRun.ps1" -UserCount 10 -ManagerCount 2 -OutputDirectory $outputDirectory -AsJson | ConvertFrom-Json -Depth 10

        $result.userCount | Should -Be 10
        $result.managerCount | Should -Be 2
        $result.reportPath | Should -Not -BeNullOrEmpty
        $result.reportPath | Should -Match '^run:'
        $result.status | Should -Be 'Succeeded'
    }
}
