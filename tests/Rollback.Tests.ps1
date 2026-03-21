Describe 'Rollback helpers' {
    BeforeAll {
        Import-Module "$PSScriptRoot/../src/Modules/SyncFactors/Rollback.psm1" -Force -DisableNameChecking
    }

    It 'converts PSCustomObject values into hashtables' {
        $value = [pscustomobject]@{
            title = 'Director'
            department = 'IT'
        }

        $result = Convert-SyncFactorsRollbackValueToHashtable -Value $value

        $result['title'] | Should -Be 'Director'
        $result['department'] | Should -Be 'IT'
    }
}
