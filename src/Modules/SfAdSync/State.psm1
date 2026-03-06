Set-StrictMode -Version Latest

function Get-SfAdSyncState {
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

    $state = Get-Content -Path $Path -Raw | ConvertFrom-Json -Depth 20
    if (-not $state.workers) {
        $state | Add-Member -MemberType NoteProperty -Name workers -Value @{} -Force
    }

    return $state
}

function Save-SfAdSyncState {
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
}

function Get-SfAdWorkerState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$State,
        [Parameter(Mandatory)]
        [string]$WorkerId
    )

    if ($State.workers.PSObject.Properties.Name -contains $WorkerId) {
        return $State.workers.$WorkerId
    }

    return $null
}

function Set-SfAdWorkerState {
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

Export-ModuleMember -Function Get-SfAdSyncState, Save-SfAdSyncState, Get-SfAdWorkerState, Set-SfAdWorkerState

