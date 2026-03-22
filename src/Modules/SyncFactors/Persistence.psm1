Set-StrictMode -Version Latest

function Test-SyncFactorsHasProperty {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [object]$InputObject,
        [Parameter(Mandatory)]
        [string]$PropertyName
    )

    if ($null -eq $InputObject) {
        return $false
    }

    return $null -ne $InputObject.PSObject.Properties[$PropertyName]
}

function Test-SyncFactorsReportField {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [object]$Report,
        [Parameter(Mandatory)]
        [string]$FieldName
    )

    if ($null -eq $Report) {
        return $false
    }

    if ($Report -is [System.Collections.IDictionary]) {
        return $Report.Contains($FieldName)
    }

    return Test-SyncFactorsHasProperty -InputObject $Report -PropertyName $FieldName
}

function Get-SyncFactorsReportFieldValue {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [object]$Report,
        [Parameter(Mandatory)]
        [string]$FieldName
    )

    if (-not (Test-SyncFactorsReportField -Report $Report -FieldName $FieldName)) {
        return $null
    }

    if ($Report -is [System.Collections.IDictionary]) {
        return $Report[$FieldName]
    }

    return $Report.$FieldName
}

function Get-SyncFactorsReportBucketNames {
    [CmdletBinding()]
    param()

    return @(
        'creates',
        'updates',
        'enables',
        'disables',
        'graveyardMoves',
        'deletions',
        'quarantined',
        'conflicts',
        'guardrailFailures',
        'manualReview',
        'unchanged'
    )
}

function New-SyncFactorsRunEntryId {
    [CmdletBinding()]
    param(
        [string]$RunId,
        [string]$Bucket,
        [AllowNull()]
        [object]$Item,
        [int]$Index
    )

    $workerId = if (Test-SyncFactorsHasProperty -InputObject $Item -PropertyName 'workerId') { "$($Item.workerId)" } else { '' }
    $samAccountName = if (Test-SyncFactorsHasProperty -InputObject $Item -PropertyName 'samAccountName') { "$($Item.samAccountName)" } else { '' }
    $identity = if (-not [string]::IsNullOrWhiteSpace($workerId)) { $workerId } elseif (-not [string]::IsNullOrWhiteSpace($samAccountName)) { $samAccountName } else { 'unknown' }
    $effectiveRunId = if (-not [string]::IsNullOrWhiteSpace($RunId)) { $RunId } else { 'no-run' }
    return "$effectiveRunId`:$Bucket`:$identity`:$Index"
}

function Get-SyncFactorsSqlitePath {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [pscustomobject]$Config,
        [string]$StatePath
    )

    if ($Config -and (Test-SyncFactorsHasProperty -InputObject $Config -PropertyName 'persistence') -and $Config.persistence) {
        if ((Test-SyncFactorsHasProperty -InputObject $Config.persistence -PropertyName 'sqlitePath') -and -not [string]::IsNullOrWhiteSpace("$($Config.persistence.sqlitePath)")) {
            return "$($Config.persistence.sqlitePath)"
        }
    }

    $effectiveStatePath = if (-not [string]::IsNullOrWhiteSpace($StatePath)) {
        $StatePath
    } elseif ($Config -and $Config.state -and (Test-SyncFactorsHasProperty -InputObject $Config.state -PropertyName 'path')) {
        "$($Config.state.path)"
    } else {
        $null
    }

    if ([string]::IsNullOrWhiteSpace($effectiveStatePath)) {
        return $null
    }

    if ($effectiveStatePath.StartsWith('/')) {
        $trimmedStatePath = $effectiveStatePath.TrimEnd('/')
        $lastSeparatorIndex = $trimmedStatePath.LastIndexOf('/')
        if ($lastSeparatorIndex -lt 0) {
            return 'syncfactors.db'
        }

        $stateDirectory = $trimmedStatePath.Substring(0, $lastSeparatorIndex)
        if ([string]::IsNullOrWhiteSpace($stateDirectory)) {
            return '/syncfactors.db'
        }

        return "$stateDirectory/syncfactors.db"
    }

    $stateDirectory = Split-Path -Path $effectiveStatePath -Parent
    if ([string]::IsNullOrWhiteSpace($stateDirectory)) {
        return 'syncfactors.db'
    }

    return Join-Path -Path $stateDirectory -ChildPath 'syncfactors.db'
}

function New-SyncFactorsReportReference {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RunId
    )

    return "run:$RunId"
}

function Get-SyncFactorsRunIdFromReference {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [string]$Reference
    )

    if ([string]::IsNullOrWhiteSpace($Reference)) {
        return $null
    }

    if ($Reference.StartsWith('run:')) {
        return $Reference.Substring(4)
    }

    return $null
}

function ConvertTo-SyncFactorsSqliteLiteral {
    [CmdletBinding()]
    param($Value)

    if ($null -eq $Value) {
        return 'NULL'
    }

    if ($Value -is [bool]) {
        if ($Value) {
            return '1'
        }

        return '0'
    }

    if ($Value -is [byte] -or $Value -is [int16] -or $Value -is [int] -or $Value -is [int64] -or $Value -is [decimal] -or $Value -is [double] -or $Value -is [single]) {
        return "$Value"
    }

    $stringValue = "$Value"
    return "'$($stringValue.Replace("'", "''"))'"
}

function ConvertTo-SyncFactorsSqliteJsonLiteral {
    [CmdletBinding()]
    param($Value)

    if ($null -eq $Value) {
        return 'NULL'
    }

    return ConvertTo-SyncFactorsSqliteLiteral -Value ($Value | ConvertTo-Json -Depth 40 -Compress)
}

function Get-SyncFactorsSqliteCommandPath {
    [CmdletBinding()]
    param()

    $command = Get-Command -Name 'sqlite3' -ErrorAction SilentlyContinue
    if (-not $command) {
        throw 'sqlite3 command not found. Install SQLite CLI to enable SQLite persistence.'
    }

    return $command.Source
}

function Invoke-SyncFactorsSqliteCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$DatabasePath,
        [Parameter(Mandatory)]
        [string]$Sql,
        [switch]$AsJson
    )

    $sqlitePath = Get-SyncFactorsSqliteCommandPath
    $directory = Split-Path -Path $DatabasePath -Parent
    if (-not [string]::IsNullOrWhiteSpace($directory) -and -not (Test-Path -Path $directory -PathType Container)) {
        New-Item -Path $directory -ItemType Directory -Force | Out-Null
    }

    $arguments = @()
    if ($AsJson) {
        $arguments += '-json'
    }
    $arguments += $DatabasePath

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $sqlitePath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true

    foreach ($argument in $arguments) {
        [void]$startInfo.ArgumentList.Add("$argument")
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo

    $stdout = ''
    $stderr = ''
    $exitCode = -1

    try {
        [void]$process.Start()
        $process.StandardInput.WriteLine($Sql)
        $process.StandardInput.Close()
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        $exitCode = $process.ExitCode
    } finally {
        $process.Dispose()
    }

    if ($exitCode -ne 0) {
        $errorText = @(@($stderr) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if ($errorText.Length -eq 0 -and -not [string]::IsNullOrWhiteSpace($stdout)) {
            $errorText = @($stdout)
        }
        throw "sqlite3 command failed: $($errorText -join [Environment]::NewLine)"
    }

    if ([string]::IsNullOrEmpty($stdout)) {
        return @()
    }

    return @($stdout -split "`r?`n") | Where-Object { $_ -ne '' }
}

function Initialize-SyncFactorsSqliteDatabase {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$DatabasePath
    )

    $schema = @'
PRAGMA journal_mode = WAL;
PRAGMA busy_timeout = 5000;
CREATE TABLE IF NOT EXISTS sync_state (
  state_path TEXT PRIMARY KEY,
  checkpoint TEXT NULL,
  raw_state_json TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS worker_state (
  state_path TEXT NOT NULL,
  worker_id TEXT NOT NULL,
  ad_object_guid TEXT NULL,
  distinguished_name TEXT NULL,
  suppressed INTEGER NOT NULL DEFAULT 0,
  first_disabled_at TEXT NULL,
  delete_after TEXT NULL,
  last_seen_status TEXT NULL,
  raw_state_json TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  PRIMARY KEY (state_path, worker_id)
);
CREATE TABLE IF NOT EXISTS runtime_status (
  state_path TEXT PRIMARY KEY,
  run_id TEXT NULL,
  status TEXT NULL,
  stage TEXT NULL,
  started_at TEXT NULL,
  last_updated_at TEXT NULL,
  completed_at TEXT NULL,
  current_worker_id TEXT NULL,
  last_action TEXT NULL,
  processed_workers INTEGER NOT NULL DEFAULT 0,
  total_workers INTEGER NOT NULL DEFAULT 0,
  error_message TEXT NULL,
  snapshot_json TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS runs (
  run_id TEXT PRIMARY KEY,
  state_path TEXT NULL,
  path TEXT NULL,
  artifact_type TEXT NOT NULL,
  worker_scope_json TEXT NULL,
  config_path TEXT NULL,
  mapping_config_path TEXT NULL,
  mode TEXT NULL,
  dry_run INTEGER NOT NULL DEFAULT 0,
  status TEXT NULL,
  started_at TEXT NULL,
  completed_at TEXT NULL,
  duration_seconds INTEGER NULL,
  reversible_operations INTEGER NOT NULL DEFAULT 0,
  creates INTEGER NOT NULL DEFAULT 0,
  updates INTEGER NOT NULL DEFAULT 0,
  enables INTEGER NOT NULL DEFAULT 0,
  disables INTEGER NOT NULL DEFAULT 0,
  graveyard_moves INTEGER NOT NULL DEFAULT 0,
  deletions INTEGER NOT NULL DEFAULT 0,
  quarantined INTEGER NOT NULL DEFAULT 0,
  conflicts INTEGER NOT NULL DEFAULT 0,
  guardrail_failures INTEGER NOT NULL DEFAULT 0,
  manual_review INTEGER NOT NULL DEFAULT 0,
  unchanged INTEGER NOT NULL DEFAULT 0,
  review_summary_json TEXT NULL,
  report_json TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS run_entries (
  entry_id TEXT PRIMARY KEY,
  run_id TEXT NOT NULL,
  state_path TEXT NULL,
  bucket TEXT NOT NULL,
  bucket_index INTEGER NOT NULL,
  worker_id TEXT NULL,
  sam_account_name TEXT NULL,
  reason TEXT NULL,
  review_category TEXT NULL,
  review_case_type TEXT NULL,
  started_at TEXT NULL,
  item_json TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_runs_started_at ON runs (started_at DESC, path DESC);
CREATE INDEX IF NOT EXISTS idx_runs_state_path ON runs (state_path);
CREATE INDEX IF NOT EXISTS idx_worker_state_lookup ON worker_state (state_path, worker_id);
CREATE INDEX IF NOT EXISTS idx_run_entries_run_id ON run_entries (run_id, bucket, bucket_index);
CREATE INDEX IF NOT EXISTS idx_run_entries_queue ON run_entries (state_path, bucket, reason, review_case_type, started_at DESC);
CREATE INDEX IF NOT EXISTS idx_run_entries_worker ON run_entries (state_path, worker_id, started_at DESC);
'@

    [void](Invoke-SyncFactorsSqliteCommand -DatabasePath $DatabasePath -Sql $schema)
    return $DatabasePath
}

function Save-SyncFactorsStateToSqlite {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$State,
        [Parameter(Mandatory)]
        [string]$StatePath,
        [string]$DatabasePath
    )

    $effectiveDatabasePath = if (-not [string]::IsNullOrWhiteSpace($DatabasePath)) { $DatabasePath } else { Get-SyncFactorsSqlitePath -StatePath $StatePath }
    if ([string]::IsNullOrWhiteSpace($effectiveDatabasePath)) {
        return
    }

    Initialize-SyncFactorsSqliteDatabase -DatabasePath $effectiveDatabasePath | Out-Null
    $updatedAt = (Get-Date).ToString('o')
    $statements = [System.Collections.Generic.List[string]]::new()
    $statements.Add('BEGIN IMMEDIATE TRANSACTION;')
    $statements.Add(@"
INSERT INTO sync_state (state_path, checkpoint, raw_state_json, updated_at)
VALUES (
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $StatePath),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $State.checkpoint),
  $(ConvertTo-SyncFactorsSqliteJsonLiteral -Value $State),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $updatedAt)
)
ON CONFLICT(state_path) DO UPDATE SET
  checkpoint = excluded.checkpoint,
  raw_state_json = excluded.raw_state_json,
  updated_at = excluded.updated_at;
"@)
    $statements.Add("DELETE FROM worker_state WHERE state_path = $(ConvertTo-SyncFactorsSqliteLiteral -Value $StatePath);")

    $workers = if ($State.workers -is [System.Collections.IDictionary]) {
        @(
            foreach ($key in $State.workers.Keys) {
                [pscustomobject]@{
                    workerId = $key
                    state = $State.workers[$key]
                }
            }
        )
    } else {
        @(
            foreach ($property in @($State.workers.PSObject.Properties)) {
                [pscustomobject]@{
                    workerId = $property.Name
                    state = $property.Value
                }
            }
        )
    }

    foreach ($entry in $workers) {
        $workerState = $entry.state
        $statements.Add(@"
INSERT INTO worker_state (
  state_path,
  worker_id,
  ad_object_guid,
  distinguished_name,
  suppressed,
  first_disabled_at,
  delete_after,
  last_seen_status,
  raw_state_json,
  updated_at
)
VALUES (
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $StatePath),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $entry.workerId),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $workerState.adObjectGuid),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $workerState.distinguishedName),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value ([bool]$workerState.suppressed)),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $workerState.firstDisabledAt),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $workerState.deleteAfter),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $workerState.lastSeenStatus),
  $(ConvertTo-SyncFactorsSqliteJsonLiteral -Value $workerState),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $updatedAt)
);
"@)
    }

    $statements.Add('COMMIT;')
    [void](Invoke-SyncFactorsSqliteCommand -DatabasePath $effectiveDatabasePath -Sql ($statements -join [Environment]::NewLine))
}

function Get-SyncFactorsStateFromSqlite {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$StatePath,
        [string]$DatabasePath
    )

    $effectiveDatabasePath = if (-not [string]::IsNullOrWhiteSpace($DatabasePath)) { $DatabasePath } else { Get-SyncFactorsSqlitePath -StatePath $StatePath }
    if ([string]::IsNullOrWhiteSpace($effectiveDatabasePath) -or -not (Test-Path -Path $effectiveDatabasePath -PathType Leaf)) {
        return $null
    }

    $query = "SELECT raw_state_json FROM sync_state WHERE state_path = $(ConvertTo-SyncFactorsSqliteLiteral -Value $StatePath) LIMIT 1;"
    $rows = @(Invoke-SyncFactorsSqliteCommand -DatabasePath $effectiveDatabasePath -Sql $query -AsJson)
    if (@($rows).Count -eq 0) {
        return $null
    }

    $payload = ($rows -join [Environment]::NewLine) | ConvertFrom-Json -Depth 30
    if (-not $payload -or @($payload).Count -eq 0 -or -not $payload[0].raw_state_json) {
        return $null
    }

    return ($payload[0].raw_state_json | ConvertFrom-Json -Depth 40)
}

function Save-SyncFactorsRuntimeStatusToSqlite {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Snapshot,
        [Parameter(Mandatory)]
        [string]$StatePath,
        [string]$DatabasePath
    )

    $effectiveDatabasePath = if (-not [string]::IsNullOrWhiteSpace($DatabasePath)) { $DatabasePath } else { Get-SyncFactorsSqlitePath -StatePath $StatePath }
    if ([string]::IsNullOrWhiteSpace($effectiveDatabasePath)) {
        return
    }

    Initialize-SyncFactorsSqliteDatabase -DatabasePath $effectiveDatabasePath | Out-Null
    $runId = if (Test-SyncFactorsHasProperty -InputObject $Snapshot -PropertyName 'runId') { $Snapshot.runId } else { $null }
    $status = if (Test-SyncFactorsHasProperty -InputObject $Snapshot -PropertyName 'status') { $Snapshot.status } else { $null }
    $stage = if (Test-SyncFactorsHasProperty -InputObject $Snapshot -PropertyName 'stage') { $Snapshot.stage } else { $null }
    $startedAt = if (Test-SyncFactorsHasProperty -InputObject $Snapshot -PropertyName 'startedAt') { $Snapshot.startedAt } else { $null }
    $lastUpdatedAt = if (Test-SyncFactorsHasProperty -InputObject $Snapshot -PropertyName 'lastUpdatedAt') { $Snapshot.lastUpdatedAt } else { $null }
    $completedAt = if (Test-SyncFactorsHasProperty -InputObject $Snapshot -PropertyName 'completedAt') { $Snapshot.completedAt } else { $null }
    $currentWorkerId = if (Test-SyncFactorsHasProperty -InputObject $Snapshot -PropertyName 'currentWorkerId') { $Snapshot.currentWorkerId } else { $null }
    $lastAction = if (Test-SyncFactorsHasProperty -InputObject $Snapshot -PropertyName 'lastAction') { $Snapshot.lastAction } else { $null }
    $processedWorkers = if (Test-SyncFactorsHasProperty -InputObject $Snapshot -PropertyName 'processedWorkers') { $Snapshot.processedWorkers } else { 0 }
    $totalWorkers = if (Test-SyncFactorsHasProperty -InputObject $Snapshot -PropertyName 'totalWorkers') { $Snapshot.totalWorkers } else { 0 }
    $errorMessage = if (Test-SyncFactorsHasProperty -InputObject $Snapshot -PropertyName 'errorMessage') { $Snapshot.errorMessage } else { $null }
    $statement = @"
INSERT INTO runtime_status (
  state_path,
  run_id,
  status,
  stage,
  started_at,
  last_updated_at,
  completed_at,
  current_worker_id,
  last_action,
  processed_workers,
  total_workers,
  error_message,
  snapshot_json
)
VALUES (
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $StatePath),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $runId),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $status),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $stage),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $startedAt),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $lastUpdatedAt),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $completedAt),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $currentWorkerId),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $lastAction),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $processedWorkers),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $totalWorkers),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $errorMessage),
  $(ConvertTo-SyncFactorsSqliteJsonLiteral -Value $Snapshot)
)
ON CONFLICT(state_path) DO UPDATE SET
  run_id = excluded.run_id,
  status = excluded.status,
  stage = excluded.stage,
  started_at = excluded.started_at,
  last_updated_at = excluded.last_updated_at,
  completed_at = excluded.completed_at,
  current_worker_id = excluded.current_worker_id,
  last_action = excluded.last_action,
  processed_workers = excluded.processed_workers,
  total_workers = excluded.total_workers,
  error_message = excluded.error_message,
  snapshot_json = excluded.snapshot_json;
"@

    [void](Invoke-SyncFactorsSqliteCommand -DatabasePath $effectiveDatabasePath -Sql $statement)
}

function Save-SyncFactorsReportToSqlite {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Report,
        [string]$ReportPath,
        [string]$DatabasePath
    )

    $statePath = "$((Get-SyncFactorsReportFieldValue -Report $Report -FieldName 'statePath'))"
    if ([string]::IsNullOrWhiteSpace($statePath)) {
        $statePath = $null
    }
    $effectiveDatabasePath = if (-not [string]::IsNullOrWhiteSpace($DatabasePath)) { $DatabasePath } else { Get-SyncFactorsSqlitePath -StatePath $statePath }
    if ([string]::IsNullOrWhiteSpace($effectiveDatabasePath)) {
        return
    }

    Initialize-SyncFactorsSqliteDatabase -DatabasePath $effectiveDatabasePath | Out-Null
    $startedAt = Get-SyncFactorsReportFieldValue -Report $Report -FieldName 'startedAt'
    $completedAt = Get-SyncFactorsReportFieldValue -Report $Report -FieldName 'completedAt'
    $durationSeconds = $null
    if (-not [string]::IsNullOrWhiteSpace("$startedAt") -and -not [string]::IsNullOrWhiteSpace("$completedAt")) {
        try {
            $durationSeconds = [int][Math]::Max(0, [Math]::Round(((Get-Date $completedAt) - (Get-Date $startedAt)).TotalSeconds))
        } catch {
            $durationSeconds = $null
        }
    }

    $runId = Get-SyncFactorsReportFieldValue -Report $Report -FieldName 'runId'
    $reportReference = if (-not [string]::IsNullOrWhiteSpace($ReportPath)) { $ReportPath } else { New-SyncFactorsReportReference -RunId "$runId" }
    $statement = @"
BEGIN IMMEDIATE TRANSACTION;
INSERT INTO runs (
  run_id,
  state_path,
  path,
  artifact_type,
  worker_scope_json,
  config_path,
  mapping_config_path,
  mode,
  dry_run,
  status,
  started_at,
  completed_at,
  duration_seconds,
  reversible_operations,
  creates,
  updates,
  enables,
  disables,
  graveyard_moves,
  deletions,
  quarantined,
  conflicts,
  guardrail_failures,
  manual_review,
  unchanged,
  review_summary_json,
  report_json
)
VALUES (
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $runId),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $statePath),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $reportReference),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $(if (Test-SyncFactorsReportField -Report $Report -FieldName 'artifactType') { Get-SyncFactorsReportFieldValue -Report $Report -FieldName 'artifactType' } else { 'SyncReport' })),
  $(ConvertTo-SyncFactorsSqliteJsonLiteral -Value (Get-SyncFactorsReportFieldValue -Report $Report -FieldName 'workerScope')),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value (Get-SyncFactorsReportFieldValue -Report $Report -FieldName 'configPath')),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value (Get-SyncFactorsReportFieldValue -Report $Report -FieldName 'mappingConfigPath')),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value (Get-SyncFactorsReportFieldValue -Report $Report -FieldName 'mode')),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $(if (Test-SyncFactorsReportField -Report $Report -FieldName 'dryRun') { [bool](Get-SyncFactorsReportFieldValue -Report $Report -FieldName 'dryRun') } else { $false })),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value (Get-SyncFactorsReportFieldValue -Report $Report -FieldName 'status')),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $startedAt),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $completedAt),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $durationSeconds),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value @(Get-SyncFactorsReportFieldValue -Report $Report -FieldName 'operations').Count),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value @(Get-SyncFactorsReportFieldValue -Report $Report -FieldName 'creates').Count),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value @(Get-SyncFactorsReportFieldValue -Report $Report -FieldName 'updates').Count),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value @(Get-SyncFactorsReportFieldValue -Report $Report -FieldName 'enables').Count),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value @(Get-SyncFactorsReportFieldValue -Report $Report -FieldName 'disables').Count),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value @(Get-SyncFactorsReportFieldValue -Report $Report -FieldName 'graveyardMoves').Count),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value @(Get-SyncFactorsReportFieldValue -Report $Report -FieldName 'deletions').Count),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value @(Get-SyncFactorsReportFieldValue -Report $Report -FieldName 'quarantined').Count),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value @(Get-SyncFactorsReportFieldValue -Report $Report -FieldName 'conflicts').Count),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value @(Get-SyncFactorsReportFieldValue -Report $Report -FieldName 'guardrailFailures').Count),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value @(Get-SyncFactorsReportFieldValue -Report $Report -FieldName 'manualReview').Count),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value @(Get-SyncFactorsReportFieldValue -Report $Report -FieldName 'unchanged').Count),
  $(ConvertTo-SyncFactorsSqliteJsonLiteral -Value (Get-SyncFactorsReportFieldValue -Report $Report -FieldName 'reviewSummary')),
  $(ConvertTo-SyncFactorsSqliteJsonLiteral -Value $Report)
)
ON CONFLICT(run_id) DO UPDATE SET
  state_path = excluded.state_path,
  path = excluded.path,
  artifact_type = excluded.artifact_type,
  worker_scope_json = excluded.worker_scope_json,
  config_path = excluded.config_path,
  mapping_config_path = excluded.mapping_config_path,
  mode = excluded.mode,
  dry_run = excluded.dry_run,
  status = excluded.status,
  started_at = excluded.started_at,
  completed_at = excluded.completed_at,
  duration_seconds = excluded.duration_seconds,
  reversible_operations = excluded.reversible_operations,
  creates = excluded.creates,
  updates = excluded.updates,
  enables = excluded.enables,
  disables = excluded.disables,
  graveyard_moves = excluded.graveyard_moves,
  deletions = excluded.deletions,
  quarantined = excluded.quarantined,
  conflicts = excluded.conflicts,
  guardrail_failures = excluded.guardrail_failures,
  manual_review = excluded.manual_review,
  unchanged = excluded.unchanged,
  review_summary_json = excluded.review_summary_json,
  report_json = excluded.report_json;
DELETE FROM run_entries WHERE run_id = $(ConvertTo-SyncFactorsSqliteLiteral -Value $runId);
"@

    foreach ($bucket in @(Get-SyncFactorsReportBucketNames)) {
        $items = @(Get-SyncFactorsReportFieldValue -Report $Report -FieldName $bucket)
        for ($index = 0; $index -lt $items.Count; $index += 1) {
            $item = $items[$index]
            $statement += @"
INSERT INTO run_entries (
  entry_id,
  run_id,
  state_path,
  bucket,
  bucket_index,
  worker_id,
  sam_account_name,
  reason,
  review_category,
  review_case_type,
  started_at,
  item_json
)
VALUES (
  $(ConvertTo-SyncFactorsSqliteLiteral -Value (New-SyncFactorsRunEntryId -RunId "$runId" -Bucket $bucket -Item $item -Index $index)),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $runId),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $statePath),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $bucket),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $index),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $(if (Test-SyncFactorsHasProperty -InputObject $item -PropertyName 'workerId') { $item.workerId } else { $null })),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $(if (Test-SyncFactorsHasProperty -InputObject $item -PropertyName 'samAccountName') { $item.samAccountName } else { $null })),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $(if (Test-SyncFactorsHasProperty -InputObject $item -PropertyName 'reason') { $item.reason } else { $null })),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $(if (Test-SyncFactorsHasProperty -InputObject $item -PropertyName 'reviewCategory') { $item.reviewCategory } else { $null })),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $(if (Test-SyncFactorsHasProperty -InputObject $item -PropertyName 'reviewCaseType') { $item.reviewCaseType } else { $null })),
  $(ConvertTo-SyncFactorsSqliteLiteral -Value $startedAt),
  $(ConvertTo-SyncFactorsSqliteJsonLiteral -Value $item)
);
"@
        }
    }

    $statement += "COMMIT;"

    [void](Invoke-SyncFactorsSqliteCommand -DatabasePath $effectiveDatabasePath -Sql $statement)
    return $reportReference
}

function Get-SyncFactorsRuntimeStatusSnapshotFromSqlite {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$StatePath,
        [string]$DatabasePath
    )

    $effectiveDatabasePath = if (-not [string]::IsNullOrWhiteSpace($DatabasePath)) { $DatabasePath } else { Get-SyncFactorsSqlitePath -StatePath $StatePath }
    if ([string]::IsNullOrWhiteSpace($effectiveDatabasePath) -or -not (Test-Path -Path $effectiveDatabasePath -PathType Leaf)) {
        return $null
    }

    $query = "SELECT snapshot_json FROM runtime_status WHERE state_path = $(ConvertTo-SyncFactorsSqliteLiteral -Value $StatePath) LIMIT 1;"
    $rows = @(Invoke-SyncFactorsSqliteCommand -DatabasePath $effectiveDatabasePath -Sql $query -AsJson)
    if (@($rows).Count -eq 0) {
        return $null
    }

    $payload = ($rows -join [Environment]::NewLine) | ConvertFrom-Json -Depth 30
    if (-not $payload -or @($payload).Count -eq 0) {
        return $null
    }

    return ($payload[0].snapshot_json | ConvertFrom-Json -Depth 30)
}

function Get-SyncFactorsTrackedWorkersFromSqlite {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$StatePath,
        [string]$DatabasePath
    )

    $effectiveDatabasePath = if (-not [string]::IsNullOrWhiteSpace($DatabasePath)) { $DatabasePath } else { Get-SyncFactorsSqlitePath -StatePath $StatePath }
    if ([string]::IsNullOrWhiteSpace($effectiveDatabasePath) -or -not (Test-Path -Path $effectiveDatabasePath -PathType Leaf)) {
        return @()
    }

    $query = @"
SELECT worker_id, ad_object_guid, distinguished_name, suppressed, first_disabled_at, delete_after, last_seen_status
FROM worker_state
WHERE state_path = $(ConvertTo-SyncFactorsSqliteLiteral -Value $StatePath)
ORDER BY worker_id ASC;
"@
    $rows = @(Invoke-SyncFactorsSqliteCommand -DatabasePath $effectiveDatabasePath -Sql $query -AsJson)
    if (@($rows).Count -eq 0) {
        return @()
    }

    $payload = ($rows -join [Environment]::NewLine) | ConvertFrom-Json -Depth 30
    return @(
        foreach ($row in @($payload)) {
            [pscustomobject]@{
                workerId = $row.worker_id
                adObjectGuid = $row.ad_object_guid
                distinguishedName = $row.distinguished_name
                suppressed = [bool]$row.suppressed
                firstDisabledAt = $row.first_disabled_at
                deleteAfter = $row.delete_after
                lastSeenStatus = $row.last_seen_status
            }
        }
    )
}

function Get-SyncFactorsStateCheckpointFromSqlite {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$StatePath,
        [string]$DatabasePath
    )

    $effectiveDatabasePath = if (-not [string]::IsNullOrWhiteSpace($DatabasePath)) { $DatabasePath } else { Get-SyncFactorsSqlitePath -StatePath $StatePath }
    if ([string]::IsNullOrWhiteSpace($effectiveDatabasePath) -or -not (Test-Path -Path $effectiveDatabasePath -PathType Leaf)) {
        return $null
    }

    $query = "SELECT checkpoint FROM sync_state WHERE state_path = $(ConvertTo-SyncFactorsSqliteLiteral -Value $StatePath) LIMIT 1;"
    $rows = @(Invoke-SyncFactorsSqliteCommand -DatabasePath $effectiveDatabasePath -Sql $query -AsJson)
    if (@($rows).Count -eq 0) {
        return $null
    }

    $payload = ($rows -join [Environment]::NewLine) | ConvertFrom-Json -Depth 30
    if (-not $payload -or @($payload).Count -eq 0) {
        return $null
    }

    return $payload[0].checkpoint
}

function Get-SyncFactorsRecentRunsFromSqlite {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$StatePath,
        [Parameter(Mandatory)]
        [int]$Limit,
        [string]$DatabasePath
    )

    $effectiveDatabasePath = if (-not [string]::IsNullOrWhiteSpace($DatabasePath)) { $DatabasePath } else { Get-SyncFactorsSqlitePath -StatePath $StatePath }
    if ([string]::IsNullOrWhiteSpace($effectiveDatabasePath) -or -not (Test-Path -Path $effectiveDatabasePath -PathType Leaf)) {
        return @()
    }

    $query = @"
SELECT
  run_id,
  path,
  artifact_type,
  worker_scope_json,
  config_path,
  mapping_config_path,
  mode,
  dry_run,
  status,
  started_at,
  completed_at,
  duration_seconds,
  reversible_operations,
  creates,
  updates,
  enables,
  disables,
  graveyard_moves,
  deletions,
  quarantined,
  conflicts,
  guardrail_failures,
  manual_review,
  unchanged,
  review_summary_json
FROM runs
WHERE state_path = $(ConvertTo-SyncFactorsSqliteLiteral -Value $StatePath)
ORDER BY COALESCE(started_at, '') DESC, COALESCE(path, '') DESC
LIMIT $(ConvertTo-SyncFactorsSqliteLiteral -Value $Limit);
"@
    $rows = @(Invoke-SyncFactorsSqliteCommand -DatabasePath $effectiveDatabasePath -Sql $query -AsJson)
    if (@($rows).Count -eq 0) {
        return @()
    }

    $payload = ($rows -join [Environment]::NewLine) | ConvertFrom-Json -Depth 30
    return @(
        foreach ($row in @($payload)) {
            [pscustomobject]@{
                runId = $row.run_id
                path = $row.path
                artifactType = if ($row.artifact_type) { $row.artifact_type } else { 'SyncReport' }
                workerScope = if ($row.worker_scope_json) { $row.worker_scope_json | ConvertFrom-Json -Depth 30 } else { $null }
                configPath = $row.config_path
                mappingConfigPath = $row.mapping_config_path
                mode = $row.mode
                dryRun = [bool]$row.dry_run
                status = $row.status
                startedAt = $row.started_at
                completedAt = $row.completed_at
                durationSeconds = if ($null -eq $row.duration_seconds -or "$($row.duration_seconds)" -eq '') { $null } else { [int]$row.duration_seconds }
                reversibleOperations = [int]$row.reversible_operations
                creates = [int]$row.creates
                updates = [int]$row.updates
                enables = [int]$row.enables
                disables = [int]$row.disables
                graveyardMoves = [int]$row.graveyard_moves
                deletions = [int]$row.deletions
                quarantined = [int]$row.quarantined
                conflicts = [int]$row.conflicts
                guardrailFailures = [int]$row.guardrail_failures
                manualReview = [int]$row.manual_review
                unchanged = [int]$row.unchanged
                reviewSummary = if ($row.review_summary_json) { $row.review_summary_json | ConvertFrom-Json -Depth 30 } else { $null }
            }
        }
    )
}

function Get-SyncFactorsRunReportFromSqlite {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RunId,
        [string]$DatabasePath,
        [string]$StatePath
    )

    $effectiveDatabasePath = if (-not [string]::IsNullOrWhiteSpace($DatabasePath)) { $DatabasePath } else { Get-SyncFactorsSqlitePath -StatePath $StatePath }
    if ([string]::IsNullOrWhiteSpace($effectiveDatabasePath) -or -not (Test-Path -Path $effectiveDatabasePath -PathType Leaf)) {
        return $null
    }

    $query = "SELECT report_json FROM runs WHERE run_id = $(ConvertTo-SyncFactorsSqliteLiteral -Value $RunId) LIMIT 1;"
    $rows = @(Invoke-SyncFactorsSqliteCommand -DatabasePath $effectiveDatabasePath -Sql $query -AsJson)
    if (@($rows).Count -eq 0) {
        return $null
    }

    $payload = ($rows -join [Environment]::NewLine) | ConvertFrom-Json -Depth 30
    if (-not $payload -or @($payload).Count -eq 0 -or -not $payload[0].report_json) {
        return $null
    }

    return ($payload[0].report_json | ConvertFrom-Json -Depth 40)
}

function Get-SyncFactorsReportFromReference {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Reference,
        [string]$DatabasePath,
        [string]$StatePath
    )

    $runId = Get-SyncFactorsRunIdFromReference -Reference $Reference
    if (-not [string]::IsNullOrWhiteSpace($runId)) {
        return Get-SyncFactorsRunReportFromSqlite -RunId $runId -DatabasePath $DatabasePath -StatePath $StatePath
    }

    if (Test-Path -Path $Reference -PathType Leaf) {
        return Get-Content -Path $Reference -Raw | ConvertFrom-Json -Depth 40
    }

    return $null
}

function Import-SyncFactorsJsonArtifactsToSqlite {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config,
        [switch]$SkipState,
        [switch]$SkipRuntimeStatus,
        [switch]$SkipReports
    )

    $sqlitePath = Get-SyncFactorsSqlitePath -Config $Config
    if ([string]::IsNullOrWhiteSpace($sqlitePath)) {
        throw 'SQLite path could not be resolved from config.'
    }

    Initialize-SyncFactorsSqliteDatabase -DatabasePath $sqlitePath | Out-Null

    $stateImported = $false
    $runtimeImported = $false
    $reportCount = 0

    if (-not $SkipState -and $Config.state -and -not [string]::IsNullOrWhiteSpace("$($Config.state.path)") -and (Test-Path -Path $Config.state.path -PathType Leaf)) {
        $state = Get-Content -Path $Config.state.path -Raw | ConvertFrom-Json -Depth 30
        Save-SyncFactorsStateToSqlite -State $state -StatePath $Config.state.path -DatabasePath $sqlitePath | Out-Null
        $stateImported = $true
    }

    if (-not $SkipRuntimeStatus) {
        $runtimeStatusPath = if ($Config.state -and -not [string]::IsNullOrWhiteSpace("$($Config.state.path)")) {
            $stateDirectory = Split-Path -Path $Config.state.path -Parent
            if ([string]::IsNullOrWhiteSpace($stateDirectory)) {
                'runtime-status.json'
            } else {
                Join-Path -Path $stateDirectory -ChildPath 'runtime-status.json'
            }
        } else {
            $null
        }

        if (-not [string]::IsNullOrWhiteSpace($runtimeStatusPath) -and (Test-Path -Path $runtimeStatusPath -PathType Leaf)) {
            $snapshot = Get-Content -Path $runtimeStatusPath -Raw | ConvertFrom-Json -Depth 30
            Save-SyncFactorsRuntimeStatusToSqlite -Snapshot $snapshot -StatePath $Config.state.path -DatabasePath $sqlitePath | Out-Null
            $runtimeImported = $true
        }
    }

    if (-not $SkipReports) {
        $directories = @()
        foreach ($candidate in @($Config.reporting.outputDirectory, $(if (Test-SyncFactorsHasProperty -InputObject $Config.reporting -PropertyName 'reviewOutputDirectory') { $Config.reporting.reviewOutputDirectory } else { $null }))) {
            if (-not [string]::IsNullOrWhiteSpace("$candidate") -and $directories -notcontains "$candidate") {
                $directories += "$candidate"
            }
        }

        foreach ($directory in $directories) {
            if (-not (Test-Path -Path $directory -PathType Container)) {
                continue
            }

            foreach ($file in @(Get-ChildItem -Path $directory -Filter 'syncfactors-*.json' -File)) {
                try {
                    $report = Get-Content -Path $file.FullName -Raw | ConvertFrom-Json -Depth 40
                    Save-SyncFactorsReportToSqlite -Report $report -ReportPath $file.FullName -DatabasePath $sqlitePath | Out-Null
                    $reportCount += 1
                } catch {
                    Write-Warning "Skipping malformed report '$($file.FullName)': $($_.Exception.Message)"
                }
            }
        }
    }

    return [pscustomobject]@{
        sqlitePath = $sqlitePath
        stateImported = $stateImported
        runtimeStatusImported = $runtimeImported
        reportsImported = $reportCount
    }
}

Export-ModuleMember -Function Get-SyncFactorsSqlitePath, New-SyncFactorsReportReference, Get-SyncFactorsRunIdFromReference, Initialize-SyncFactorsSqliteDatabase, Save-SyncFactorsStateToSqlite, Get-SyncFactorsStateFromSqlite, Save-SyncFactorsRuntimeStatusToSqlite, Save-SyncFactorsReportToSqlite, Get-SyncFactorsRuntimeStatusSnapshotFromSqlite, Get-SyncFactorsTrackedWorkersFromSqlite, Get-SyncFactorsStateCheckpointFromSqlite, Get-SyncFactorsRecentRunsFromSqlite, Get-SyncFactorsRunReportFromSqlite, Get-SyncFactorsReportFromReference, Import-SyncFactorsJsonArtifactsToSqlite
