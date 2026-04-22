[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'SyncFactorsBackup.ps1')
. (Join-Path $PSScriptRoot 'SyncFactorsJson.ps1')

$script:SyncFactorsRepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).ProviderPath

function Get-TrackedLocalConfigPairs {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    return @(
        [pscustomobject]@{
            SamplePath = Join-Path $RepositoryRoot 'config/sample.codex-run.json'
            LocalPath = Join-Path $RepositoryRoot 'config/local.codex-run.json'
        },
        [pscustomobject]@{
            SamplePath = Join-Path $RepositoryRoot 'config/sample.mock-successfactors.real-ad.sync-config.json'
            LocalPath = Join-Path $RepositoryRoot 'config/local.mock-successfactors.real-ad.sync-config.json'
        },
        [pscustomobject]@{
            SamplePath = Join-Path $RepositoryRoot 'config/sample.real-successfactors.real-ad.sync-config.json'
            LocalPath = Join-Path $RepositoryRoot 'config/local.real-successfactors.real-ad.sync-config.json'
        },
        [pscustomobject]@{
            SamplePath = Join-Path $RepositoryRoot 'config/sample.empjob-confirmed.mapping-config.json'
            LocalPath = Join-Path $RepositoryRoot 'config/local.syncfactors.mapping-config.json'
        },
        [pscustomobject]@{
            SamplePath = Join-Path $RepositoryRoot 'config/sample.empjob-confirmed.mapping-config.json'
            LocalPath = Join-Path $RepositoryRoot 'config/local.empjob-confirmed.mapping-config.json'
        }
    )
}

function Test-ConfigArrayValue {
    param(
        [AllowNull()]
        $Value
    )

    return $Value -is [System.Collections.IList] -and $Value -isnot [string]
}

function Test-ConfigObjectValue {
    param(
        [AllowNull()]
        $Value
    )

    return $Value -is [System.Collections.IDictionary]
}

function Copy-ConfigNode {
    param(
        [Parameter(Mandatory)]
        [AllowNull()]
        $Value
    )

    if ($null -eq $Value) {
        return $null
    }

    if (Test-ConfigObjectValue -Value $Value) {
        $copy = [ordered]@{}
        foreach ($key in $Value.Keys) {
            $copy[$key] = Copy-ConfigNode -Value $Value[$key]
        }

        return $copy
    }

    if (Test-ConfigArrayValue -Value $Value) {
        $copy = New-Object System.Collections.ArrayList
        foreach ($item in $Value) {
            [void]$copy.Add((Copy-ConfigNode -Value $item))
        }

        return ,$copy.ToArray()
    }

    return $Value
}

function Read-ConfigJsonObject {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [switch]$Optional
    )

    if (-not (Test-Path $Path)) {
        if ($Optional) {
            return $null
        }

        throw "JSON file not found: $Path"
    }

    try {
        $parsed = ConvertFrom-SyncFactorsJson -InputObject (Get-Content -Path $Path -Raw)
    }
    catch {
        throw "Failed to parse JSON file '$Path'. $_"
    }

    if ($parsed -isnot [System.Collections.IDictionary]) {
        throw "JSON file '$Path' must contain a JSON object."
    }

    return $parsed
}

function ConvertTo-ConfigJson {
    param(
        [Parameter(Mandatory)]
        $Value
    )

    return ($Value | ConvertTo-Json -Depth 100)
}

function Write-ConfigJsonFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        $Value
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    $json = ConvertTo-ConfigJson -Value $Value
    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, $encoding)
}

function Get-ConfigArrayHandlingMode {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    switch ($Path) {
        'mappings' { return 'mapping-object-array' }
        'ad.ouRoutingRules' { return 'ou-routing-rule-array' }
        'ad.licensingGroups' { return 'value-list-scalar-array' }
        'ad.transport.trustedCertificateThumbprints' { return 'value-list-scalar-array' }
        'alerts.smtp.to' { return 'value-list-scalar-array' }
        'sync.leaveStatusValues' { return 'value-list-scalar-array' }
        'approval.requireFor' { return 'structural-scalar-array' }
        'successFactors.query.select' { return 'structural-scalar-array' }
        'successFactors.query.expand' { return 'structural-scalar-array' }
        'successFactors.query.inactiveStatusValues' { return 'structural-scalar-array' }
        'successFactors.previewQuery.select' { return 'structural-scalar-array' }
        'successFactors.previewQuery.expand' { return 'structural-scalar-array' }
        'successFactors.previewQuery.inactiveStatusValues' { return 'structural-scalar-array' }
        default { return 'sample-array' }
    }
}

function ConvertTo-CanonicalIdentityNode {
    param(
        [Parameter(Mandatory)]
        $Value
    )

    if (Test-ConfigObjectValue -Value $Value) {
        $copy = [ordered]@{}
        foreach ($key in ($Value.Keys | Sort-Object)) {
            $copy[$key] = ConvertTo-CanonicalIdentityNode -Value $Value[$key]
        }

        return $copy
    }

    if (Test-ConfigArrayValue -Value $Value) {
        $items = New-Object System.Collections.ArrayList
        foreach ($item in $Value) {
            [void]$items.Add((ConvertTo-CanonicalIdentityNode -Value $item))
        }

        return ,$items.ToArray()
    }

    return $Value
}

function Get-MappingArrayIdentity {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Item,
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not $Item.Contains('target')) {
        throw "Config array '$Path' contains an entry without a 'target' identity."
    }

    $identity = [string]$Item['target']
    if ([string]::IsNullOrWhiteSpace($identity)) {
        throw "Config array '$Path' contains an entry with a blank 'target' identity."
    }

    return $identity
}

function Get-OuRoutingRuleIdentity {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Item,
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not $Item.Contains('match')) {
        throw "Config array '$Path' contains an entry without a 'match' identity object."
    }

    $match = $Item['match']
    if (-not (Test-ConfigObjectValue -Value $match)) {
        throw "Config array '$Path' contains an entry whose 'match' identity must be a JSON object."
    }

    return ((ConvertTo-CanonicalIdentityNode -Value $match) | ConvertTo-Json -Depth 100 -Compress)
}

function New-IdentityLookupTable {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Items,
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [scriptblock]$IdentitySelector
    )

    $lookup = @{}
    foreach ($item in $Items) {
        if (-not (Test-ConfigObjectValue -Value $item)) {
            throw "Config array '$Path' must contain JSON objects."
        }

        $identity = & $IdentitySelector $item $Path
        if ($lookup.ContainsKey($identity)) {
            throw "Config array '$Path' contains duplicate identity '$identity'."
        }

        $lookup[$identity] = $item
    }

    return $lookup
}

function Normalize-ObjectArrayByIdentity {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$SampleItems,
        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$LocalItems,
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [scriptblock]$IdentitySelector
    )

    $sampleLookup = New-IdentityLookupTable -Items $SampleItems -Path $Path -IdentitySelector $IdentitySelector
    $localLookup = if ($null -eq $LocalItems) {
        @{}
    }
    else {
        New-IdentityLookupTable -Items $LocalItems -Path $Path -IdentitySelector $IdentitySelector
    }

    $normalized = New-Object System.Collections.ArrayList
    foreach ($sampleItem in $SampleItems) {
        $identity = & $IdentitySelector $sampleItem $Path
        if ($localLookup.ContainsKey($identity)) {
            [void]$normalized.Add((Normalize-ConfigNode `
                -SampleValue $sampleItem `
                -HasLocalValue $true `
                -LocalValue $localLookup[$identity] `
                -Path "$Path[$identity]"))
        }
        else {
            [void]$normalized.Add((Copy-ConfigNode -Value $sampleItem))
        }
    }

    return ,$normalized.ToArray()
}

function Normalize-ObjectArrayByIndex {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$SampleItems,
        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$LocalItems,
        [Parameter(Mandatory)]
        [string]$Path
    )

    $normalized = New-Object System.Collections.ArrayList
    for ($index = 0; $index -lt $SampleItems.Length; $index++) {
        $sampleItem = $SampleItems[$index]
        if ($null -ne $LocalItems -and $index -lt $LocalItems.Length -and (Test-ConfigObjectValue -Value $LocalItems[$index])) {
            [void]$normalized.Add((Normalize-ConfigNode `
                -SampleValue $sampleItem `
                -HasLocalValue $true `
                -LocalValue $LocalItems[$index] `
                -Path "$Path[$index]"))
        }
        else {
            [void]$normalized.Add((Copy-ConfigNode -Value $sampleItem))
        }
    }

    return ,$normalized.ToArray()
}

function Normalize-ConfigArray {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$SampleItems,
        [switch]$HasLocalValue,
        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$LocalItems,
        [Parameter(Mandatory)]
        [string]$Path
    )

    if ($null -eq $SampleItems) {
        $SampleItems = @()
    }

    if ($HasLocalValue -and $null -eq $LocalItems) {
        $LocalItems = @()
    }

    $mode = Get-ConfigArrayHandlingMode -Path $Path
    switch ($mode) {
        'mapping-object-array' {
            return Normalize-ObjectArrayByIdentity `
                -SampleItems $SampleItems `
                -LocalItems $LocalItems `
                -Path $Path `
                -IdentitySelector ${function:Get-MappingArrayIdentity}
        }
        'ou-routing-rule-array' {
            return Normalize-ObjectArrayByIdentity `
                -SampleItems $SampleItems `
                -LocalItems $LocalItems `
                -Path $Path `
                -IdentitySelector ${function:Get-OuRoutingRuleIdentity}
        }
        'value-list-scalar-array' {
            if ($HasLocalValue) {
                return Copy-ConfigNode -Value $LocalItems
            }

            return Copy-ConfigNode -Value $SampleItems
        }
        'structural-scalar-array' {
            return Copy-ConfigNode -Value $SampleItems
        }
        default {
            if ($SampleItems.Length -gt 0 -and (Test-ConfigObjectValue -Value $SampleItems[0])) {
                return Normalize-ObjectArrayByIndex -SampleItems $SampleItems -LocalItems $LocalItems -Path $Path
            }

            return Copy-ConfigNode -Value $SampleItems
        }
    }
}

function Normalize-ConfigNode {
    param(
        [Parameter(Mandatory)]
        $SampleValue,
        [Parameter(Mandatory)]
        [bool]$HasLocalValue,
        [AllowNull()]
        $LocalValue,
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Path
    )

    if (Test-ConfigObjectValue -Value $SampleValue) {
        $localObject = if ($HasLocalValue -and (Test-ConfigObjectValue -Value $LocalValue)) { $LocalValue } else { $null }
        $normalized = [ordered]@{}
        foreach ($key in $SampleValue.Keys) {
            $childPath = if ([string]::IsNullOrWhiteSpace($Path)) { $key } else { "$Path.$key" }
            $hasChild = $null -ne $localObject -and $localObject.Contains($key)
            $localChildValue = $null
            if ($hasChild) {
                $localChildValue = $localObject[$key]
            }

            $normalized[$key] = Normalize-ConfigNode `
                -SampleValue $SampleValue[$key] `
                -HasLocalValue $hasChild `
                -LocalValue $localChildValue `
                -Path $childPath
        }

        return $normalized
    }

    if (Test-ConfigArrayValue -Value $SampleValue) {
        $sampleItems = @($SampleValue)
        $localItems = if ($HasLocalValue -and (Test-ConfigArrayValue -Value $LocalValue)) { @($LocalValue) } else { $null }

        return Normalize-ConfigArray `
            -SampleItems $sampleItems `
            -HasLocalValue:$HasLocalValue `
            -LocalItems $localItems `
            -Path $Path
    }

    if ($HasLocalValue) {
        return Copy-ConfigNode -Value $LocalValue
    }

    return Copy-ConfigNode -Value $SampleValue
}

function Get-NormalizedConfigDocument {
    param(
        [Parameter(Mandatory)]
        [string]$SampleConfigPath,
        [Parameter(Mandatory)]
        [string]$LocalConfigPath
    )

    $sample = Read-ConfigJsonObject -Path $SampleConfigPath
    $local = Read-ConfigJsonObject -Path $LocalConfigPath -Optional

    return Normalize-ConfigNode `
        -SampleValue $sample `
        -HasLocalValue ($null -ne $local) `
        -LocalValue $local `
        -Path ''
}

function Test-ConfigDrift {
    param(
        [Parameter(Mandatory)]
        [string]$SampleConfigPath,
        [Parameter(Mandatory)]
        [string]$LocalConfigPath
    )

    $normalizedDocument = Get-NormalizedConfigDocument -SampleConfigPath $SampleConfigPath -LocalConfigPath $LocalConfigPath
    $normalizedJson = ConvertTo-ConfigJson -Value $normalizedDocument
    $currentJson = if (Test-Path $LocalConfigPath) { (Get-Content -Path $LocalConfigPath -Raw).TrimEnd("`r", "`n") } else { $null }
    $drifted = -not (Test-Path $LocalConfigPath) -or -not [string]::Equals($currentJson, $normalizedJson, [StringComparison]::Ordinal)

    return [pscustomobject]@{
        SampleConfigPath = $SampleConfigPath
        LocalConfigPath = $LocalConfigPath
        Drifted = $drifted
        CurrentJson = $currentJson
        NormalizedJson = $normalizedJson
        NormalizedDocument = $normalizedDocument
    }
}

function Get-TrackedLocalConfigDrift {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    $drifted = New-Object System.Collections.ArrayList
    foreach ($pair in Get-TrackedLocalConfigPairs -RepositoryRoot $RepositoryRoot) {
        $status = Test-ConfigDrift -SampleConfigPath $pair.SamplePath -LocalConfigPath $pair.LocalPath
        if ($status.Drifted) {
            [void]$drifted.Add($status)
        }
    }

    return $drifted.ToArray()
}

function Sync-ConfigFormat {
    param(
        [Parameter(Mandatory)]
        [string]$SampleConfigPath,
        [Parameter(Mandatory)]
        [string]$LocalConfigPath,
        [switch]$NoBackup
    )

    $status = Test-ConfigDrift -SampleConfigPath $SampleConfigPath -LocalConfigPath $LocalConfigPath
    $backupPath = $null

    if ($status.Drifted) {
        if (-not $NoBackup -and (Test-Path $LocalConfigPath)) {
            $backupPath = New-SyncFactorsBackup `
                -RepositoryRoot $script:SyncFactorsRepositoryRoot `
                -SourcePath $LocalConfigPath
        }

        Write-ConfigJsonFile -Path $LocalConfigPath -Value $status.NormalizedDocument
    }

    return [pscustomobject]@{
        SampleConfigPath = $SampleConfigPath
        LocalConfigPath = $LocalConfigPath
        Drifted = $status.Drifted
        BackupPath = $backupPath
    }
}

function Sync-TrackedLocalConfigFormats {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [switch]$NoBackup
    )

    $results = New-Object System.Collections.ArrayList
    foreach ($pair in Get-TrackedLocalConfigPairs -RepositoryRoot $RepositoryRoot) {
        [void]$results.Add((Sync-ConfigFormat `
            -SampleConfigPath $pair.SamplePath `
            -LocalConfigPath $pair.LocalPath `
            -NoBackup:$NoBackup))
    }

    return $results.ToArray()
}
