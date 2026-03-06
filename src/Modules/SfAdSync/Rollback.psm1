Set-StrictMode -Version Latest

function Write-SfAdRollbackLog {
    [CmdletBinding()]
    param(
        [string]$Level = 'INFO',
        [string]$Message
    )

    $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    Write-Host "[$timestamp][$Level] $Message"
}

function Resolve-SfAdRollbackUser {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Operation,
        [Parameter(Mandatory)]
        [pscustomobject]$Config
    )

    if ($Operation.target.objectGuid) {
        $user = Get-SfAdUserByObjectGuid -ObjectGuid $Operation.target.objectGuid
        if ($user) {
            return $user
        }
    }

    if ($Operation.workerId -and $Operation.workerId -ne '__checkpoint__') {
        return Get-SfAdTargetUser -Config $Config -WorkerId $Operation.workerId
    }

    return $null
}

function Convert-SfAdRollbackValueToHashtable {
    [CmdletBinding()]
    param($Value)

    if ($null -eq $Value) {
        return @{}
    }

    $result = @{}
    foreach ($property in $Value.PSObject.Properties) {
        $result[$property.Name] = $property.Value
    }

    return $result
}

function Invoke-SfAdRollback {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [string]$ReportPath,
        [string]$ConfigPath,
        [switch]$DryRun
    )

    $resolvedReportPath = (Resolve-Path -Path $ReportPath).Path
    $report = Get-Content -Path $resolvedReportPath -Raw | ConvertFrom-Json -Depth 40
    $effectiveConfigPath = if ($ConfigPath) { (Resolve-Path -Path $ConfigPath).Path } else { $report.configPath }
    if (-not $effectiveConfigPath) {
        throw 'ConfigPath is required when the report does not include it.'
    }

    $config = Get-SfAdSyncConfig -Path $effectiveConfigPath
    $statePath = if ($report.statePath) { $report.statePath } else { $config.state.path }
    $state = if ($statePath) { Get-SfAdSyncState -Path $statePath } else { [pscustomobject]@{ checkpoint = $null; workers = @{} } }

    Write-SfAdRollbackLog -Message "Rolling back run $($report.runId) from $resolvedReportPath"

    $operations = @($report.operations | Sort-Object sequence -Descending)
    foreach ($operation in $operations) {
        if ($operation.status -and $operation.status -ne 'Applied') {
            continue
        }

        Write-SfAdRollbackLog -Message "Reverting [$($operation.sequence)] $($operation.operationType) for worker $($operation.workerId)"

        switch ($operation.operationType) {
            'AddGroupMembership' {
                $user = Resolve-SfAdRollbackUser -Operation $operation -Config $config
                if ($user -and $operation.after -and $operation.after.groupsAdded) {
                    Remove-SfAdUserFromGroups -User $user -Groups @($operation.after.groupsAdded) -DryRun:$DryRun
                }
            }
            'EnableUser' {
                $user = Resolve-SfAdRollbackUser -Operation $operation -Config $config
                if ($user) {
                    if ($operation.before.enabled) {
                        Enable-SfAdUser -User $user -DryRun:$DryRun
                    } else {
                        Disable-SfAdUser -User $user -DryRun:$DryRun
                    }
                }
            }
            'DisableUser' {
                $user = Resolve-SfAdRollbackUser -Operation $operation -Config $config
                if ($user) {
                    if ($operation.before.enabled) {
                        Enable-SfAdUser -User $user -DryRun:$DryRun
                    } else {
                        Disable-SfAdUser -User $user -DryRun:$DryRun
                    }
                }
            }
            'MoveUser' {
                $user = Resolve-SfAdRollbackUser -Operation $operation -Config $config
                if ($user -and $operation.before.parentOu) {
                    Move-SfAdUser -User $user -TargetOu $operation.before.parentOu -DryRun:$DryRun
                }
            }
            'UpdateAttributes' {
                $user = Resolve-SfAdRollbackUser -Operation $operation -Config $config
                if ($user) {
                    $changes = Convert-SfAdRollbackValueToHashtable -Value $operation.before
                    if ($changes.Count -gt 0) {
                        Set-SfAdUserAttributes -User $user -Changes $changes -DryRun:$DryRun | Out-Null
                    }
                }
            }
            'CreateUser' {
                $user = Resolve-SfAdRollbackUser -Operation $operation -Config $config
                if ($user) {
                    Remove-SfAdUser -User $user -DryRun:$DryRun
                }
            }
            'DeleteUser' {
                if ($operation.before) {
                    Restore-SfAdUserFromSnapshot -Config $config -Snapshot $operation.before -DryRun:$DryRun | Out-Null
                }
            }
            'SetWorkerState' {
                if ($DryRun) {
                    continue
                }

                if ($null -eq $operation.before) {
                    Remove-SfAdWorkerState -State $state -WorkerId $operation.workerId
                } else {
                    Set-SfAdWorkerState -State $state -WorkerId $operation.workerId -WorkerState $operation.before
                }
            }
            'SetCheckpoint' {
                if (-not $DryRun) {
                    $state.checkpoint = $operation.before
                }
            }
        }
    }

    if (-not $DryRun -and $statePath) {
        Save-SfAdSyncState -State $state -Path $statePath
    }

    Write-SfAdRollbackLog -Message 'Rollback completed.'
}

Export-ModuleMember -Function Write-SfAdRollbackLog, Resolve-SfAdRollbackUser, Convert-SfAdRollbackValueToHashtable, Invoke-SfAdRollback
