Describe 'State helpers' {
    BeforeAll {
        Import-Module "$PSScriptRoot/../src/Modules/SfAdSync/State.psm1" -Force
    }

    It 'enumerates hashtable worker entries' {
        $entries = Get-SfAdWorkerEntries -Workers @{
            '1001' = [pscustomobject]@{ suppressed = $true }
        }

        $entries.Count | Should -Be 1
        $entries[0].Name | Should -Be '1001'
        $entries[0].Value.suppressed | Should -BeTrue
    }

    It 'removes worker state entries' {
        $state = [pscustomobject]@{
            checkpoint = $null
            workers = [pscustomobject]@{
                '1001' = [pscustomobject]@{ suppressed = $true }
            }
        }

        Remove-SfAdWorkerState -State $state -WorkerId '1001'
        (Get-SfAdWorkerState -State $state -WorkerId '1001') | Should -Be $null
    }
}
