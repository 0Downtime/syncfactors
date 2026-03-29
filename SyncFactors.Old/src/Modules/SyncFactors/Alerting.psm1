Set-StrictMode -Version Latest

function Test-SyncFactorsHasProperty {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [object]$InputObject,
        [Parameter(Mandatory)]
        [string]$PropertyName
    )

    if ($null -eq $InputObject) {
        return $false
    }

    return $null -ne $InputObject.PSObject.Properties[$PropertyName]
}

function Get-SyncFactorsAlertReasons {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config,
        [Parameter(Mandatory)]
        [object]$Report
    )

    if (-not (Test-SyncFactorsHasProperty -InputObject $Config -PropertyName 'alerts') -or $null -eq $Config.alerts) {
        return @()
    }

    $alertsConfig = $Config.alerts
    if (-not ((Test-SyncFactorsHasProperty -InputObject $alertsConfig -PropertyName 'enabled') -and [bool]$alertsConfig.enabled)) {
        return @()
    }

    $triggers = if ((Test-SyncFactorsHasProperty -InputObject $alertsConfig -PropertyName 'triggers') -and $alertsConfig.triggers) {
        $alertsConfig.triggers
    } else {
        [pscustomobject]@{
            failedRuns = $true
            guardrailBreaches = $true
            manualReviewEvents = $true
        }
    }

    $reasons = [System.Collections.Generic.List[string]]::new()
    $status = if (Test-SyncFactorsHasProperty -InputObject $Report -PropertyName 'status') { "$($Report.status)" } else { '' }
    $guardrailCount = if (Test-SyncFactorsHasProperty -InputObject $Report -PropertyName 'guardrailFailures') { @($Report.guardrailFailures).Count } else { 0 }
    $manualReviewCount = if (Test-SyncFactorsHasProperty -InputObject $Report -PropertyName 'manualReview') { @($Report.manualReview).Count } else { 0 }

    if (((Test-SyncFactorsHasProperty -InputObject $triggers -PropertyName 'failedRuns') -and [bool]$triggers.failedRuns) -and $status -eq 'Failed') {
        $reasons.Add('failed-run')
    }

    if (((Test-SyncFactorsHasProperty -InputObject $triggers -PropertyName 'guardrailBreaches') -and [bool]$triggers.guardrailBreaches) -and $guardrailCount -gt 0) {
        $reasons.Add('guardrail-breach')
    }

    if (((Test-SyncFactorsHasProperty -InputObject $triggers -PropertyName 'manualReviewEvents') -and [bool]$triggers.manualReviewEvents) -and $manualReviewCount -gt 0) {
        $reasons.Add('manual-review')
    }

    return @($reasons)
}

function Get-SyncFactorsAlertSubject {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config,
        [Parameter(Mandatory)]
        [object]$Report,
        [Parameter(Mandatory)]
        [string[]]$Reasons
    )

    $prefix = if (
        (Test-SyncFactorsHasProperty -InputObject $Config.alerts -PropertyName 'subjectPrefix') -and
        -not [string]::IsNullOrWhiteSpace("$($Config.alerts.subjectPrefix)")
    ) {
        "$($Config.alerts.subjectPrefix)"
    } else {
        '[SyncFactors]'
    }

    $status = if (Test-SyncFactorsHasProperty -InputObject $Report -PropertyName 'status') { "$($Report.status)" } else { 'Unknown' }
    $mode = if (Test-SyncFactorsHasProperty -InputObject $Report -PropertyName 'mode') { "$($Report.mode)" } else { 'Unknown' }
    $artifactType = if (Test-SyncFactorsHasProperty -InputObject $Report -PropertyName 'artifactType') { "$($Report.artifactType)" } else { 'SyncReport' }
    $reasonSummary = if ($Reasons.Count -gt 0) { $Reasons -join ', ' } else { 'run-event' }

    return "$prefix $status $artifactType $mode ($reasonSummary)"
}

function Get-SyncFactorsAlertBody {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Report,
        [Parameter(Mandatory)]
        [string[]]$Reasons,
        [string]$ReportReference
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('SyncFactors alert')
    $lines.Add('')
    $lines.Add("Reasons: $($Reasons -join ', ')")
    $lines.Add("Run ID: $(if (Test-SyncFactorsHasProperty -InputObject $Report -PropertyName 'runId') { $Report.runId } else { '' })")
    $lines.Add("Status: $(if (Test-SyncFactorsHasProperty -InputObject $Report -PropertyName 'status') { $Report.status } else { '' })")
    $lines.Add("Mode: $(if (Test-SyncFactorsHasProperty -InputObject $Report -PropertyName 'mode') { $Report.mode } else { '' })")
    $lines.Add("Artifact: $(if (Test-SyncFactorsHasProperty -InputObject $Report -PropertyName 'artifactType') { $Report.artifactType } else { '' })")
    $lines.Add("Started: $(if (Test-SyncFactorsHasProperty -InputObject $Report -PropertyName 'startedAt') { $Report.startedAt } else { '' })")
    $lines.Add("Completed: $(if (Test-SyncFactorsHasProperty -InputObject $Report -PropertyName 'completedAt') { $Report.completedAt } else { '' })")
    if (-not [string]::IsNullOrWhiteSpace($ReportReference)) {
        $lines.Add("Report: $ReportReference")
    }

    $lines.Add('')
    $lines.Add('Counts:')
    $lines.Add("Creates: $(if (Test-SyncFactorsHasProperty -InputObject $Report -PropertyName 'creates') { @($Report.creates).Count } else { 0 })")
    $lines.Add("Updates: $(if (Test-SyncFactorsHasProperty -InputObject $Report -PropertyName 'updates') { @($Report.updates).Count } else { 0 })")
    $lines.Add("Enables: $(if (Test-SyncFactorsHasProperty -InputObject $Report -PropertyName 'enables') { @($Report.enables).Count } else { 0 })")
    $lines.Add("Disables: $(if (Test-SyncFactorsHasProperty -InputObject $Report -PropertyName 'disables') { @($Report.disables).Count } else { 0 })")
    $lines.Add("Graveyard moves: $(if (Test-SyncFactorsHasProperty -InputObject $Report -PropertyName 'graveyardMoves') { @($Report.graveyardMoves).Count } else { 0 })")
    $lines.Add("Deletions: $(if (Test-SyncFactorsHasProperty -InputObject $Report -PropertyName 'deletions') { @($Report.deletions).Count } else { 0 })")
    $lines.Add("Quarantined: $(if (Test-SyncFactorsHasProperty -InputObject $Report -PropertyName 'quarantined') { @($Report.quarantined).Count } else { 0 })")
    $lines.Add("Conflicts: $(if (Test-SyncFactorsHasProperty -InputObject $Report -PropertyName 'conflicts') { @($Report.conflicts).Count } else { 0 })")
    $lines.Add("Guardrail failures: $(if (Test-SyncFactorsHasProperty -InputObject $Report -PropertyName 'guardrailFailures') { @($Report.guardrailFailures).Count } else { 0 })")
    $lines.Add("Manual review: $(if (Test-SyncFactorsHasProperty -InputObject $Report -PropertyName 'manualReview') { @($Report.manualReview).Count } else { 0 })")

    $errorMessage = if (Test-SyncFactorsHasProperty -InputObject $Report -PropertyName 'errorMessage') { "$($Report.errorMessage)" } else { '' }
    if (-not [string]::IsNullOrWhiteSpace($errorMessage)) {
        $lines.Add('')
        $lines.Add("Error: $errorMessage")
    }

    $guardrailEntries = @(if (Test-SyncFactorsHasProperty -InputObject $Report -PropertyName 'guardrailFailures') { @($Report.guardrailFailures) } else { @() })
    if ($guardrailEntries.Count -gt 0) {
        $lines.Add('')
        $lines.Add('Guardrail details:')
        foreach ($entry in ($guardrailEntries | Select-Object -First 5)) {
            $lines.Add("- workerId=$(if (Test-SyncFactorsHasProperty -InputObject $entry -PropertyName 'workerId') { $entry.workerId } else { '' }) threshold=$(if (Test-SyncFactorsHasProperty -InputObject $entry -PropertyName 'threshold') { $entry.threshold } else { '' }) attemptedCount=$(if (Test-SyncFactorsHasProperty -InputObject $entry -PropertyName 'attemptedCount') { $entry.attemptedCount } else { '' })")
        }
    }

    $manualReviewEntries = @(if (Test-SyncFactorsHasProperty -InputObject $Report -PropertyName 'manualReview') { @($Report.manualReview) } else { @() })
    if ($manualReviewEntries.Count -gt 0) {
        $lines.Add('')
        $lines.Add('Manual review details:')
        foreach ($entry in ($manualReviewEntries | Select-Object -First 5)) {
            $lines.Add("- workerId=$(if (Test-SyncFactorsHasProperty -InputObject $entry -PropertyName 'workerId') { $entry.workerId } else { '' }) reason=$(if (Test-SyncFactorsHasProperty -InputObject $entry -PropertyName 'reason') { $entry.reason } else { '' }) reviewCaseType=$(if (Test-SyncFactorsHasProperty -InputObject $entry -PropertyName 'reviewCaseType') { $entry.reviewCaseType } else { '' })")
        }
    }

    return ($lines -join [Environment]::NewLine)
}

function Send-SyncFactorsSmtpMessage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$SmtpConfig,
        [Parameter(Mandatory)]
        [string]$Subject,
        [Parameter(Mandatory)]
        [string]$Body
    )

    $client = [System.Net.Mail.SmtpClient]::new("$($SmtpConfig.host)", [int]$SmtpConfig.port)
    $client.EnableSsl = if ((Test-SyncFactorsHasProperty -InputObject $SmtpConfig -PropertyName 'useSsl') -and $null -ne $SmtpConfig.useSsl) { [bool]$SmtpConfig.useSsl } else { $false }
    $client.DeliveryMethod = [System.Net.Mail.SmtpDeliveryMethod]::Network
    $client.UseDefaultCredentials = $false

    $hasUsername = (Test-SyncFactorsHasProperty -InputObject $SmtpConfig -PropertyName 'username') -and -not [string]::IsNullOrWhiteSpace("$($SmtpConfig.username)")
    $hasPassword = (Test-SyncFactorsHasProperty -InputObject $SmtpConfig -PropertyName 'password') -and -not [string]::IsNullOrWhiteSpace("$($SmtpConfig.password)")
    if ($hasUsername -and $hasPassword) {
        $client.Credentials = [System.Net.NetworkCredential]::new("$($SmtpConfig.username)", "$($SmtpConfig.password)")
    }

    $message = [System.Net.Mail.MailMessage]::new()
    try {
        $message.From = [System.Net.Mail.MailAddress]::new("$($SmtpConfig.from)")
        foreach ($recipient in @($SmtpConfig.to)) {
            if ([string]::IsNullOrWhiteSpace("$recipient")) {
                continue
            }
            [void]$message.To.Add([System.Net.Mail.MailAddress]::new("$recipient"))
        }
        $message.Subject = $Subject
        $message.Body = $Body
        $message.IsBodyHtml = $false

        $client.Send($message)
    } finally {
        $message.Dispose()
        $client.Dispose()
    }
}

function Send-SyncFactorsRunAlert {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config,
        [Parameter(Mandatory)]
        [object]$Report,
        [string]$ReportReference
    )

    $reasons = @(Get-SyncFactorsAlertReasons -Config $Config -Report $Report)
    if ($reasons.Count -eq 0) {
        return $false
    }

    $smtpConfig = $Config.alerts.smtp
    $subject = Get-SyncFactorsAlertSubject -Config $Config -Report $Report -Reasons $reasons
    $body = Get-SyncFactorsAlertBody -Report $Report -Reasons $reasons -ReportReference $ReportReference
    Send-SyncFactorsSmtpMessage -SmtpConfig $smtpConfig -Subject $subject -Body $body
    return $true
}

Export-ModuleMember -Function Get-SyncFactorsAlertReasons, Get-SyncFactorsAlertSubject, Get-SyncFactorsAlertBody, Send-SyncFactorsSmtpMessage, Send-SyncFactorsRunAlert
