Describe 'Reporting journal' {
    BeforeAll {
        Import-Module "$PSScriptRoot/../src/Modules/SyncFactors/Reporting.psm1" -Force
        Import-Module "$PSScriptRoot/../src/Modules/SyncFactors/Persistence.psm1" -Force
    }

    It 'creates a report with run metadata and operation journal' {
        $report = New-SyncFactorsReport -Mode 'Delta' -DryRun -ConfigPath 'config.json' -MappingConfigPath 'mapping.json' -StatePath 'state.json'

        $report.runId | Should -Not -BeNullOrEmpty
        $report.mode | Should -Be 'Delta'
        $report.dryRun | Should -BeTrue
        $report.status | Should -Be 'InProgress'
        $report.operations.Count | Should -Be 0
        $report.conflicts.Count | Should -Be 0
        $report.guardrailFailures.Count | Should -Be 0
    }

    It 'appends ordered operations' {
        $report = New-SyncFactorsReport
        $entry = Add-SyncFactorsReportOperation -Report $report -OperationType 'UpdateAttributes' -WorkerId '1001' -Bucket 'updates' -Target @{ samAccountName = 'jdoe' } -Before ([pscustomobject]@{ title = 'Old' }) -After ([pscustomobject]@{ title = 'New' })

        $entry.sequence | Should -Be 1
        $entry.workerId | Should -Be '1001'
        $report.operations.Count | Should -Be 1
        $report.operations[0].operationType | Should -Be 'UpdateAttributes'
    }

    It 'writes a persisted report with the expected top-level shape' {
        $reportDirectory = Join-Path $TestDrive 'reports'
        $report = New-SyncFactorsReport -Mode 'Full' -DryRun -ConfigPath 'config.json' -MappingConfigPath 'mapping.json' -StatePath 'state.json'
        Add-SyncFactorsReportEntry -Report $report -Bucket 'creates' -Entry @{ workerId = '1001'; samAccountName = 'jdoe' }
        Add-SyncFactorsReportOperation -Report $report -OperationType 'CreateUser' -WorkerId '1001' -Bucket 'creates' -Target @{ samAccountName = 'jdoe' } -Before $null -After ([pscustomobject]@{ samAccountName = 'jdoe' }) | Out-Null

        $reportPath = Save-SyncFactorsReport -Report $report -Directory $reportDirectory -Mode 'Full'
        $reportPath | Should -Match '^run:'
        $persisted = Get-SyncFactorsReportFromReference -Reference $reportPath -StatePath 'state.json'

        $persisted.runId | Should -Not -BeNullOrEmpty
        $persisted.mode | Should -Be 'Full'
        $persisted.dryRun | Should -BeTrue
        $persisted.completedAt | Should -Not -BeNullOrEmpty
        $persisted.operations.Count | Should -Be 1
        $persisted.creates.Count | Should -Be 1
        ($persisted.PSObject.Properties.Name -contains 'operationSequence') | Should -BeFalse
    }

    It 'persists operation entries with rollback-relevant fields intact' {
        $reportDirectory = Join-Path $TestDrive 'reports'
        $report = New-SyncFactorsReport -Mode 'Delta' -ConfigPath 'config.json' -MappingConfigPath 'mapping.json' -StatePath 'state.json'
        Add-SyncFactorsReportOperation -Report $report -OperationType 'MoveUser' -WorkerId '2001' -Bucket 'graveyardMoves' -TargetType 'ADUser' -Target @{ objectGuid = '11111111-1111-1111-1111-111111111111' } -Before ([pscustomobject]@{ parentOu = 'OU=Employees,DC=example,DC=com' }) -After ([pscustomobject]@{ targetOu = 'OU=Graveyard,DC=example,DC=com' }) | Out-Null

        $reportPath = Save-SyncFactorsReport -Report $report -Directory $reportDirectory -Mode 'Delta'
        $reportPath | Should -Match '^run:'
        $persisted = Get-SyncFactorsReportFromReference -Reference $reportPath -StatePath 'state.json'

        $persisted.operations[0].operationType | Should -Be 'MoveUser'
        $persisted.operations[0].workerId | Should -Be '2001'
        $persisted.operations[0].target.objectGuid | Should -Be '11111111-1111-1111-1111-111111111111'
        $persisted.operations[0].before.parentOu | Should -Be 'OU=Employees,DC=example,DC=com'
        $persisted.operations[0].after.targetOu | Should -Be 'OU=Graveyard,DC=example,DC=com'
        $persisted.operations[0].status | Should -Be 'Applied'
    }
}
