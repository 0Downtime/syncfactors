Set-StrictMode -Version Latest

function New-SfAdSyncReport {
    [CmdletBinding()]
    param()

    return [ordered]@{
        startedAt = (Get-Date).ToString('o')
        creates = @()
        updates = @()
        enables = @()
        disables = @()
        graveyardMoves = @()
        deletions = @()
        quarantined = @()
        manualReview = @()
        unchanged = @()
    }
}

function Add-SfAdReportEntry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [hashtable]$Report,
        [Parameter(Mandatory)]
        [string]$Bucket,
        [Parameter(Mandatory)]
        [hashtable]$Entry
    )

    if (-not $Report.Contains($Bucket)) {
        throw "Unknown report bucket '$Bucket'."
    }

    $Report[$Bucket] = @($Report[$Bucket]) + [pscustomobject]$Entry
}

function Save-SfAdSyncReport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [hashtable]$Report,
        [Parameter(Mandatory)]
        [string]$Directory,
        [Parameter(Mandatory)]
        [string]$Mode
    )

    if (-not (Test-Path -Path $Directory -PathType Container)) {
        New-Item -Path $Directory -ItemType Directory -Force | Out-Null
    }

    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $path = Join-Path -Path $Directory -ChildPath "sf-ad-sync-$Mode-$timestamp.json"
    $Report['completedAt'] = (Get-Date).ToString('o')
    $Report | ConvertTo-Json -Depth 20 | Set-Content -Path $path
    return $path
}

Export-ModuleMember -Function New-SfAdSyncReport, Add-SfAdReportEntry, Save-SfAdSyncReport

