Import-Module "$PSScriptRoot/../src/Modules/SfAdSync/Rollback.psm1" -Force

Describe 'Rollback helpers' {
    It 'converts PSCustomObject values into hashtables' {
        $value = [pscustomobject]@{
            title = 'Director'
            department = 'IT'
        }

        $result = Convert-SfAdRollbackValueToHashtable -Value $value

        $result['title'] | Should -Be 'Director'
        $result['department'] | Should -Be 'IT'
    }
}
