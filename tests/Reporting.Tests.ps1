Import-Module "$PSScriptRoot/../src/Modules/SfAdSync/Reporting.psm1" -Force

Describe 'Reporting journal' {
    It 'creates a report with run metadata and operation journal' {
        $report = New-SfAdSyncReport -Mode 'Delta' -DryRun -ConfigPath 'config.json' -MappingConfigPath 'mapping.json' -StatePath 'state.json'

        $report.runId | Should -Not -BeNullOrEmpty
        $report.mode | Should -Be 'Delta'
        $report.dryRun | Should -BeTrue
        $report.operations.Count | Should -Be 0
    }

    It 'appends ordered operations' {
        $report = New-SfAdSyncReport
        $entry = Add-SfAdReportOperation -Report $report -OperationType 'UpdateAttributes' -WorkerId '1001' -Bucket 'updates' -Target @{ samAccountName = 'jdoe' } -Before ([pscustomobject]@{ title = 'Old' }) -After ([pscustomobject]@{ title = 'New' })

        $entry.sequence | Should -Be 1
        $entry.workerId | Should -Be '1001'
        $report.operations.Count | Should -Be 1
        $report.operations[0].operationType | Should -Be 'UpdateAttributes'
    }
}
