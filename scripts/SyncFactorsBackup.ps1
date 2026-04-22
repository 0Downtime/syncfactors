function Get-SyncFactorsBackupDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    return Join-Path $RepositoryRoot 'config/backup'
}

function Get-SyncFactorsBackupFileName {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory)]
        [string]$SourcePath,
        [Parameter(Mandatory)]
        [string]$Timestamp
    )

    $relativePath = [System.IO.Path]::GetRelativePath(
        [System.IO.Path]::GetFullPath($RepositoryRoot),
        [System.IO.Path]::GetFullPath($SourcePath))
    $safeRelativePath = ($relativePath -replace '[\\/:\s]+', '.') -replace '[^A-Za-z0-9._-]', '_'
    $safeRelativePath = $safeRelativePath.Trim('.')
    if ([string]::IsNullOrWhiteSpace($safeRelativePath)) {
        $safeRelativePath = Split-Path -Leaf $SourcePath
    }

    return "$safeRelativePath.$Timestamp.bak"
}

function New-SyncFactorsBackup {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory)]
        [string]$SourcePath
    )

    $backupDirectory = Get-SyncFactorsBackupDirectory -RepositoryRoot $RepositoryRoot
    [System.IO.Directory]::CreateDirectory($backupDirectory) | Out-Null

    $timestamp = Get-Date -Format 'yyyyMMddHHmmss'
    $backupFileName = Get-SyncFactorsBackupFileName `
        -RepositoryRoot $RepositoryRoot `
        -SourcePath $SourcePath `
        -Timestamp $timestamp
    $backupPath = Join-Path $backupDirectory $backupFileName
    Copy-Item -LiteralPath $SourcePath -Destination $backupPath
    return $backupPath
}
