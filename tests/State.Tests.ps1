Describe 'State helpers' {
    BeforeAll {
        Import-Module "$PSScriptRoot/../src/Modules/SfAdSync/State.psm1" -Force
    }

    It 'preserves timestamp strings when DateKind is supported' {
        $state = ConvertFrom-SfAdJsonDocument -Json '{"checkpoint":"2026-03-12T21:00:00","workers":{}}'

        $state.checkpoint | Should -BeOfType ([string])
        $state.checkpoint | Should -Be '2026-03-12T21:00:00'
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

    It 'falls back when ConvertFrom-Json lacks DateKind' {
        InModuleScope State {
            Mock Get-Command {
                [pscustomobject]@{
                    Parameters = @{
                        Depth = $true
                    }
                }
            } -ParameterFilter { $Name -eq 'ConvertFrom-Json' -and $CommandType -eq 'Cmdlet' }

            $state = ConvertFrom-SfAdJsonDocument -Json '{"checkpoint":"2026-03-12T21:00:00","workers":{}}'

            (Get-Date $state.checkpoint).ToString('s') | Should -Be '2026-03-12T21:00:00'
            Assert-MockCalled Get-Command -Times 1 -Exactly
        }
    }
}
