Set-StrictMode -Version Latest

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

function Convert-SfAdMappedValue {
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

function Get-SfAdAttributeChanges {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Worker,
        [AllowNull()]
        [pscustomobject]$ExistingUser,
        [Parameter(Mandatory)]
        [pscustomobject]$MappingConfig
    )

    $changes = @{}
    $missingRequired = @()

    foreach ($mapping in $MappingConfig.mappings) {
        if (-not $mapping.enabled) {
            continue
        }

        $sourceValue = Get-NestedValue -InputObject $Worker -Path $mapping.source
        $targetValue = $null
        if ($ExistingUser -and $ExistingUser.PSObject.Properties.Name -contains $mapping.target) {
            $targetValue = $ExistingUser.$($mapping.target)
        }

        $mappedValue = Convert-SfAdMappedValue -Value $sourceValue -Transform $mapping.transform
        if ($mapping.required -and [string]::IsNullOrWhiteSpace("$mappedValue")) {
            $missingRequired += $mapping.source
            continue
        }

        if ("$mappedValue" -ne "$targetValue") {
            $changes[$mapping.target] = $mappedValue
        }
    }

    return [pscustomobject]@{
        Changes = $changes
        MissingRequired = $missingRequired
    }
}

Export-ModuleMember -Function Get-PathSegments, Get-NestedValue, Convert-SfAdMappedValue, Get-SfAdAttributeChanges
