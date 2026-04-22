function ConvertTo-SyncFactorsJsonNode {
    param(
        [AllowNull()]
        $Value
    )

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [System.Collections.IDictionary]) {
        $copy = [ordered]@{}
        foreach ($key in $Value.Keys) {
            $copy[$key] = ConvertTo-SyncFactorsJsonNode -Value $Value[$key]
        }

        return $copy
    }

    if ($Value -is [System.Collections.IList] -and $Value -isnot [string]) {
        $copy = New-Object System.Collections.ArrayList
        foreach ($item in $Value) {
            [void]$copy.Add((ConvertTo-SyncFactorsJsonNode -Value $item))
        }

        return ,$copy.ToArray()
    }

    if ($Value -is [pscustomobject]) {
        $copy = [ordered]@{}
        foreach ($property in $Value.PSObject.Properties) {
            $copy[$property.Name] = ConvertTo-SyncFactorsJsonNode -Value $property.Value
        }

        return $copy
    }

    return $Value
}

function ConvertFrom-SyncFactorsJson {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$InputObject
    )

    $convertFromJsonCommand = Get-Command 'ConvertFrom-Json' -ErrorAction Stop
    if ($convertFromJsonCommand.Parameters.ContainsKey('AsHashtable')) {
        return Microsoft.PowerShell.Utility\ConvertFrom-Json -InputObject $InputObject -AsHashtable
    }

    $parsed = Microsoft.PowerShell.Utility\ConvertFrom-Json -InputObject $InputObject
    return ConvertTo-SyncFactorsJsonNode -Value $parsed
}
