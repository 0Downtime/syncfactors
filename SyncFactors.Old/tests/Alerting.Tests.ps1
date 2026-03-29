Describe 'Alerting module' {
    BeforeAll {
        Import-Module "$PSScriptRoot/../src/Modules/SyncFactors/Alerting.psm1" -Force
    }

    It 'returns reasons for failed runs, guardrails, and manual-review events' {
        $config = [pscustomobject]@{
            alerts = [pscustomobject]@{
                enabled = $true
                triggers = [pscustomobject]@{
                    failedRuns = $true
                    guardrailBreaches = $true
                    manualReviewEvents = $true
                }
            }
        }
        $report = [pscustomobject]@{
            status = 'Failed'
            guardrailFailures = @(@{ threshold = 'maxCreatesPerRun' })
            manualReview = @(@{ workerId = '1001' })
        }

        $reasons = @(Get-SyncFactorsAlertReasons -Config $config -Report $report)

        $reasons | Should -Be @('failed-run', 'guardrail-breach', 'manual-review')
    }

    It 'uses unauthenticated smtp by default when credentials are not configured' {
        InModuleScope Alerting {
            $config = [pscustomobject]@{
                alerts = [pscustomobject]@{
                    enabled = $true
                    subjectPrefix = '[SyncFactors]'
                    smtp = [pscustomobject]@{
                        host = 'mail.example.com'
                        port = 25
                        from = 'syncfactors@example.com'
                        to = @('ops@example.com')
                    }
                }
            }
            $report = [pscustomobject]@{
                runId = 'run-123'
                status = 'Failed'
                mode = 'Delta'
                artifactType = 'SyncReport'
                startedAt = '2026-03-22T10:00:00Z'
                completedAt = '2026-03-22T10:05:00Z'
                guardrailFailures = @()
                manualReview = @()
                creates = @()
                updates = @()
                enables = @()
                disables = @()
                graveyardMoves = @()
                deletions = @()
                quarantined = @()
                conflicts = @()
                errorMessage = 'Boom'
            }

            Mock Send-SyncFactorsSmtpMessage { }

            $sent = Send-SyncFactorsRunAlert -Config $config -Report $report -ReportReference 'run:run-123'

            $sent | Should -BeTrue
            Assert-MockCalled Send-SyncFactorsSmtpMessage -Times 1 -Exactly -ParameterFilter {
                $SmtpConfig.host -eq 'mail.example.com' -and
                $SmtpConfig.port -eq 25 -and
                $SmtpConfig.from -eq 'syncfactors@example.com' -and
                $SmtpConfig.to[0] -eq 'ops@example.com' -and
                -not $SmtpConfig.PSObject.Properties['username'] -and
                -not $SmtpConfig.PSObject.Properties['password'] -and
                $Subject -match 'Failed'
            }
        }
    }

    It 'does not send when no configured alert trigger matches the run' {
        InModuleScope Alerting {
            $config = [pscustomobject]@{
                alerts = [pscustomobject]@{
                    enabled = $true
                    triggers = [pscustomobject]@{
                        failedRuns = $false
                        guardrailBreaches = $false
                        manualReviewEvents = $false
                    }
                    smtp = [pscustomobject]@{
                        host = 'mail.example.com'
                        port = 25
                        from = 'syncfactors@example.com'
                        to = @('ops@example.com')
                    }
                }
            }
            $report = [pscustomobject]@{
                runId = 'run-124'
                status = 'Succeeded'
                mode = 'Delta'
                artifactType = 'SyncReport'
                guardrailFailures = @()
                manualReview = @()
            }

            Mock Send-SyncFactorsSmtpMessage { throw 'should not send' }

            $sent = Send-SyncFactorsRunAlert -Config $config -Report $report -ReportReference 'run:run-124'

            $sent | Should -BeFalse
            Assert-MockCalled Send-SyncFactorsSmtpMessage -Times 0 -Exactly
        }
    }
}
