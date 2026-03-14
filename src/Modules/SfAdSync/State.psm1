Set-StrictMode -Version Latest

function ConvertFrom-SfAdJsonDocument {
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

    $state = ConvertFrom-SfAdJsonDocument -Json (Get-Content -Path $Path -Raw)
    if (-not $state.workers) {
        $state | Add-Member -MemberType NoteProperty -Name workers -Value @{} -Force
    }

    return $state
}

function Get-SfAdWorkerEntries {
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

function Remove-SfAdWorkerState {
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

Export-ModuleMember -Function ConvertFrom-SfAdJsonDocument, Get-SfAdSyncState, Get-SfAdWorkerEntries, Save-SfAdSyncState, Get-SfAdWorkerState, Set-SfAdWorkerState, Remove-SfAdWorkerState
