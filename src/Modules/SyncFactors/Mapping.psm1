Set-StrictMode -Version Latest

function Resolve-SyncFactorsCollectionValue {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [object]$Value
    )

    if ($null -eq $Value -or $Value -is [string]) {
        return $Value
    }

    $resultsProperty = if ($Value -is [System.Collections.IDictionary]) {
        if ($Value.Contains('results')) { $Value['results'] } else { $null }
    } else {
        $Value.PSObject.Properties['results']
    }

    if ($null -eq $resultsProperty) {
        return $Value
    }

    if ($resultsProperty -is [System.Management.Automation.PSPropertyInfo]) {
        return $resultsProperty.Value
    }

    return $resultsProperty
}

function Get-PathSegments {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $segments = @()
    foreach ($rawSegment in $Path.Split('.')) {
        if ($rawSegment -notmatch '^(?<name>[^\[\]]+)(?:\[(?<index>\d+)\])?$') {
            throw "Unsupported mapping source path segment '$rawSegment' in '$Path'."
        }

        $segments += [pscustomobject]@{
            Name = $Matches.name
            Index = if ($Matches.ContainsKey('index') -and $null -ne $Matches['index'] -and "$($Matches['index'])" -ne '') { [int]$Matches['index'] } else { $null }
        }
    }

    return $segments
}

function Get-NestedValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$InputObject,
        [Parameter(Mandatory)]
        [string]$Path
    )

    $current = $InputObject
    foreach ($segment in Get-PathSegments -Path $Path) {
        if ($null -eq $current) {
            return $null
        }

        if ($current -is [System.Collections.IDictionary]) {
            $current = $current[$segment.Name]
        } else {
            $property = $current.PSObject.Properties[$segment.Name]
            if (-not $property) {
                return $null
            }

            $current = $property.Value
        }

        $current = Resolve-SyncFactorsCollectionValue -Value $current

        if ($null -eq $segment.Index) {
            continue
        }

        if ($current -is [string]) {
            return $null
        }

        $values = @($current)
        if ($segment.Index -ge $values.Count) {
            return $null
        }

        $current = $values[$segment.Index]
    }

    return $current
}

function Convert-SyncFactorsMappedValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        $Value,
        [string]$Transform
    )

    switch ($Transform) {
        'Trim' { return "$Value".Trim() }
        'Upper' { return "$Value".ToUpperInvariant() }
        'Lower' { return "$Value".ToLowerInvariant() }
        'DateOnly' {
            if ($null -eq $Value -or [string]::IsNullOrWhiteSpace("$Value")) { return $null }
            return (Get-Date $Value).ToString('yyyy-MM-dd')
        }
        default { return $Value }
    }
}

function Get-SyncFactorsAttributeChanges {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Worker,
        [AllowNull()]
        [object]$ExistingUser,
        [Parameter(Mandatory)]
        [pscustomobject]$MappingConfig
    )

    $evaluation = Get-SyncFactorsMappingEvaluation -Worker $Worker -ExistingUser $ExistingUser -MappingConfig $MappingConfig
    return [pscustomobject]@{
        Changes = $evaluation.Changes
        MissingRequired = $evaluation.MissingRequired
    }
}

function Get-SyncFactorsMappingEvaluation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Worker,
        [AllowNull()]
        [object]$ExistingUser,
        [Parameter(Mandatory)]
        [pscustomobject]$MappingConfig
    )

    $changes = @{}
    $missingRequired = @()
    $rows = @()

    foreach ($mapping in $MappingConfig.mappings) {
        if (-not $mapping.enabled) {
            continue
        }

        $sourceValue = Get-NestedValue -InputObject $Worker -Path $mapping.source
        $targetValue = $null
        if ($ExistingUser -and $ExistingUser.PSObject.Properties.Name -contains $mapping.target) {
            $targetValue = $ExistingUser.$($mapping.target)
        }

        $mappedValue = Convert-SyncFactorsMappedValue -Value $sourceValue -Transform $mapping.transform
        $changed = "$mappedValue" -ne "$targetValue"
        $rows += [pscustomobject]@{
            sourceField = $mapping.source
            targetAttribute = $mapping.target
            transform = $mapping.transform
            required = [bool]$mapping.required
            sourceValue = $sourceValue
            currentAdValue = $targetValue
            proposedValue = $mappedValue
            changed = $changed
        }

        if ($mapping.required -and [string]::IsNullOrWhiteSpace("$mappedValue")) {
            $missingRequired += $mapping.source
            continue
        }

        if ($changed) {
            $changes[$mapping.target] = $mappedValue
        }
    }

    return [pscustomobject]@{
        Changes = $changes
        MissingRequired = $missingRequired
        Rows = @($rows)
    }
}

Export-ModuleMember -Function Get-PathSegments, Get-NestedValue, Convert-SyncFactorsMappedValue, Get-SyncFactorsAttributeChanges, Get-SyncFactorsMappingEvaluation
