Set-StrictMode -Version Latest

$moduleRoot = $PSScriptRoot
Import-Module (Join-Path $moduleRoot 'Config.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'State.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'ActiveDirectorySync.psm1') -Force -DisableNameChecking

function Write-SyncFactorsRollbackLog {
    [CmdletBinding()]
    param(
        [string]$Level = 'INFO',
        [string]$Message
    )

    $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    Write-Host "[$timestamp][$Level] $Message"
}

function Resolve-SyncFactorsRollbackUser {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Operation,
        [Parameter(Mandatory)]
        [pscustomobject]$Config
    )

    if ($Operation.target.objectGuid) {
        $user = Get-SyncFactorsUserByObjectGuid -Config $Config -ObjectGuid $Operation.target.objectGuid
        if ($user) {
            return $user
        }
    }

    if ($Operation.workerId -and $Operation.workerId -ne '__checkpoint__') {
        return Get-SyncFactorsTargetUser -Config $Config -WorkerId $Operation.workerId
    }

    return $null
}

function Convert-SyncFactorsRollbackValueToHashtable {
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

function Convert-SyncFactorsRollbackCheckpointValue {
    [CmdletBinding()]
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [datetime]) {
        return $Value.ToString('yyyy-MM-ddTHH:mm:ss')
    }

    return "$Value"
}

function Invoke-SyncFactorsRollback {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [string]$ReportPath,
        [string]$ConfigPath,
        [switch]$DryRun
    )

    $resolvedReportPath = (Resolve-Path -Path $ReportPath).Path
    $report = Get-Content -Path $resolvedReportPath -Raw | ConvertFrom-Json -Depth 40
    $reportConfigPath = if ($report.PSObject.Properties.Name -contains 'configPath') { $report.configPath } else { $null }
    $effectiveConfigPath = if ($ConfigPath) { (Resolve-Path -Path $ConfigPath).Path } else { $reportConfigPath }
    if (-not $effectiveConfigPath) {
        throw 'ConfigPath is required when the report does not include it.'
    }

    $config = Get-SyncFactorsConfig -Path $effectiveConfigPath
    $statePath = if ($report.statePath) { $report.statePath } else { $config.state.path }
    $state = if ($statePath) { Get-SyncFactorsState -Path $statePath } else { [pscustomobject]@{ checkpoint = $null; workers = @{} } }

    Write-SyncFactorsRollbackLog -Message "Rolling back run $($report.runId) from $resolvedReportPath"

    $operations = @($report.operations | Sort-Object sequence -Descending)
    foreach ($operation in $operations) {
        if ($operation.status -and $operation.status -ne 'Applied') {
            continue
        }

        Write-SyncFactorsRollbackLog -Message "Reverting [$($operation.sequence)] $($operation.operationType) for worker $($operation.workerId)"

        switch ($operation.operationType) {
            'AddGroupMembership' {
                $user = Resolve-SyncFactorsRollbackUser -Operation $operation -Config $config
                if ($user -and $operation.after -and $operation.after.groupsAdded) {
                    Remove-SyncFactorsUserFromGroups -Config $config -User $user -Groups @($operation.after.groupsAdded) -DryRun:$DryRun
                }
            }
            'EnableUser' {
                $user = Resolve-SyncFactorsRollbackUser -Operation $operation -Config $config
                if ($user) {
                    if ($operation.before.enabled) {
                        Enable-SyncFactorsUser -Config $config -User $user -DryRun:$DryRun
                    } else {
                        Disable-SyncFactorsUser -Config $config -User $user -DryRun:$DryRun
                    }
                }
            }
            'DisableUser' {
                $user = Resolve-SyncFactorsRollbackUser -Operation $operation -Config $config
                if ($user) {
                    if ($operation.before.enabled) {
                        Enable-SyncFactorsUser -Config $config -User $user -DryRun:$DryRun
                    } else {
                        Disable-SyncFactorsUser -Config $config -User $user -DryRun:$DryRun
                    }
                }
            }
            'MoveUser' {
                $user = Resolve-SyncFactorsRollbackUser -Operation $operation -Config $config
                if ($user -and $operation.before.parentOu) {
                    Move-SyncFactorsUser -Config $config -User $user -TargetOu $operation.before.parentOu -DryRun:$DryRun
                }
            }
            'UpdateAttributes' {
                $user = Resolve-SyncFactorsRollbackUser -Operation $operation -Config $config
                if ($user) {
                    $changes = Convert-SyncFactorsRollbackValueToHashtable -Value $operation.before
                    if ($changes.Count -gt 0) {
                        Set-SyncFactorsUserAttributes -Config $config -User $user -Changes $changes -DryRun:$DryRun | Out-Null
                    }
                }
            }
            'CreateUser' {
                $user = Resolve-SyncFactorsRollbackUser -Operation $operation -Config $config
                if ($user) {
                    Remove-SyncFactorsUser -Config $config -User $user -DryRun:$DryRun
                }
            }
            'DeleteUser' {
                if ($operation.before) {
                    Restore-SyncFactorsUserFromSnapshot -Config $config -Snapshot $operation.before -DryRun:$DryRun | Out-Null
                }
            }
            'SetWorkerState' {
                if ($DryRun) {
                    continue
                }

                if ($null -eq $operation.before) {
                    Remove-SyncFactorsWorkerState -State $state -WorkerId $operation.workerId
                } else {
                    Set-SyncFactorsWorkerState -State $state -WorkerId $operation.workerId -WorkerState $operation.before
                }
            }
            'SetCheckpoint' {
                if (-not $DryRun) {
                    $state.checkpoint = Convert-SyncFactorsRollbackCheckpointValue -Value $operation.before
                }
            }
        }
    }

    if (-not $DryRun -and $statePath) {
        Save-SyncFactorsState -State $state -Path $statePath
    }

    Write-SyncFactorsRollbackLog -Message 'Rollback completed.'
}

Export-ModuleMember -Function Write-SyncFactorsRollbackLog, Resolve-SyncFactorsRollbackUser, Convert-SyncFactorsRollbackValueToHashtable, Convert-SyncFactorsRollbackCheckpointValue, Invoke-SyncFactorsRollback
