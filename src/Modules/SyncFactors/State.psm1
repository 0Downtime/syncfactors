Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'Persistence.psm1') -Force -DisableNameChecking

function ConvertFrom-SyncFactorsJsonDocument {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Json
    )

    $convertFromJson = Get-Command -Name ConvertFrom-Json -CommandType Cmdlet
    if ($convertFromJson.Parameters.ContainsKey('DateKind')) {
        return $Json | ConvertFrom-Json -Depth 20 -DateKind String
    }

    return $Json | ConvertFrom-Json -Depth 20
}

function Get-SyncFactorsState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -Path $Path -PathType Leaf)) {
        return [pscustomobject]@{
            checkpoint = $null
            workers = @{}
        }
    }

    $state = ConvertFrom-SyncFactorsJsonDocument -Json (Get-Content -Path $Path -Raw)
    if (-not $state.workers) {
        $state | Add-Member -MemberType NoteProperty -Name workers -Value @{} -Force
    }

    return $state
}

function Get-SyncFactorsWorkerEntries {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        $Workers
    )

    if ($null -eq $Workers) {
        return @()
    }

    if ($Workers -is [System.Collections.IDictionary]) {
        return @(
            foreach ($key in $Workers.Keys) {
                [pscustomobject]@{
                    Name = $key
                    Value = $Workers[$key]
                }
            }
        )
    }

    return @($Workers.PSObject.Properties | ForEach-Object {
        [pscustomobject]@{
            Name = $_.Name
            Value = $_.Value
        }
    })
}

function Save-SyncFactorsState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$State,
        [Parameter(Mandatory)]
        [string]$Path
    )

    $directory = Split-Path -Path $Path -Parent
    if (-not (Test-Path -Path $directory -PathType Container)) {
        New-Item -Path $directory -ItemType Directory -Force | Out-Null
    }

    $State | ConvertTo-Json -Depth 20 | Set-Content -Path $Path
    Save-SyncFactorsStateToSqlite -State $State -StatePath $Path
}

function Get-SyncFactorsWorkerState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$State,
        [Parameter(Mandatory)]
        [string]$WorkerId
    )

    if ($State.workers -is [System.Collections.IDictionary]) {
        if ($State.workers.Contains($WorkerId)) {
            return $State.workers[$WorkerId]
        }
        return $null
    }

    $property = $State.workers.PSObject.Properties[$WorkerId]
    if ($property) {
        return $property.Value
    }

    return $null
}

function Set-SyncFactorsWorkerState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$State,
        [Parameter(Mandatory)]
        [string]$WorkerId,
        [Parameter(Mandatory)]
        [pscustomobject]$WorkerState
    )

    $State.workers | Add-Member -MemberType NoteProperty -Name $WorkerId -Value $WorkerState -Force
}

function Remove-SyncFactorsWorkerState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$State,
        [Parameter(Mandatory)]
        [string]$WorkerId
    )

    if ($State.workers -is [System.Collections.IDictionary]) {
        [void]$State.workers.Remove($WorkerId)
        return
    }

    $property = $State.workers.PSObject.Properties[$WorkerId]
    if ($property) {
        [void]$State.workers.PSObject.Properties.Remove($WorkerId)
    }
}

Export-ModuleMember -Function ConvertFrom-SyncFactorsJsonDocument, Get-SyncFactorsState, Get-SyncFactorsWorkerEntries, Save-SyncFactorsState, Get-SyncFactorsWorkerState, Set-SyncFactorsWorkerState, Remove-SyncFactorsWorkerState
