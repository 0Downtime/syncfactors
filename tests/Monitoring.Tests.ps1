Describe 'Monitoring module' {
    BeforeAll {
        Import-Module "$PSScriptRoot/../src/Modules/SyncFactors/Monitoring.psm1" -Force
    }

    It 'includes config and mapping paths in run summaries' {
        $reportDirectory = Join-Path $TestDrive 'reports'
        $reportPath = Join-Path $reportDirectory 'syncfactors-Delta-20260312-220000.json'
        New-Item -Path $reportDirectory -ItemType Directory -Force | Out-Null

        @{
            runId = 'run-123'
            configPath = 'config.json'
            mappingConfigPath = 'mapping.json'
            mode = 'Delta'
            dryRun = $false
            startedAt = '2026-03-12T21:30:00'
            completedAt = '2026-03-12T21:35:00'
            status = 'Succeeded'
            operations = @()
            creates = @()
            updates = @()
            enables = @()
            disables = @()
            graveyardMoves = @()
            deletions = @()
            quarantined = @()
            conflicts = @()
            guardrailFailures = @()
            manualReview = @()
            unchanged = @()
        } | ConvertTo-Json -Depth 10 | Set-Content -Path $reportPath

        $result = @(Get-SyncFactorsRecentRunSummaries -Directory $reportDirectory -Limit 5)

        $result[0].configPath | Should -Be 'config.json'
        $result[0].mappingConfigPath | Should -Be 'mapping.json'
    }

    It 'prefers SQLite-backed selected run reports when available' {
        $sqlitePath = Join-Path $TestDrive 'syncfactors.db'
        $sql = @"
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
INSERT INTO runs (run_id, state_path, path, artifact_type, mode, dry_run, status, started_at, completed_at, report_json)
VALUES ('run-sqlite-monitor', '/tmp/state.json', '/tmp/missing.json', 'SyncReport', 'Delta', 0, 'Succeeded', '2026-03-22T12:00:00Z', '2026-03-22T12:05:00Z', '{"runId":"run-sqlite-monitor","updates":[{"workerId":"5001"}],"operations":[]}');
"@
        sqlite3 $sqlitePath $sql | Out-Null

        $status = [pscustomobject]@{
            paths = [pscustomobject]@{
                sqlitePath = $sqlitePath
            }
            recentRuns = @(
                [pscustomobject]@{
                    runId = 'run-sqlite-monitor'
                    path = '/tmp/missing.json'
                }
            )
            latestRun = [pscustomobject]@{
                runId = 'run-sqlite-monitor'
                path = '/tmp/missing.json'
            }
        }
        $uiState = New-SyncFactorsMonitorUiState

        $report = Get-SyncFactorsMonitorSelectedRunReport -Status $status -UiState $uiState

        $report.runId | Should -Be 'run-sqlite-monitor'
        $report.updates[0].workerId | Should -Be '5001'
    }

    It 'returns recent runs from SQLite when available' {
        $configPath = Join-Path $TestDrive 'config.json'
        $statePath = Join-Path $TestDrive 'state/sync-state.json'
        $sqlitePath = Join-Path $TestDrive 'state/syncfactors.db'
        $reportDirectory = Join-Path $TestDrive 'reports'
        $reviewDirectory = Join-Path $reportDirectory 'review'

        New-Item -Path (Split-Path $statePath -Parent) -ItemType Directory -Force | Out-Null
        New-Item -Path $reviewDirectory -ItemType Directory -Force | Out-Null
        '{"checkpoint":"2026-03-22T12:00:00","workers":{}}' | Set-Content -Path $statePath
        @"
{
  "successFactors": {
    "baseUrl": "https://example.successfactors.com/odata/v2",
    "oauth": {
      "tokenUrl": "https://example.successfactors.com/oauth/token",
      "clientId": "client-id",
      "clientSecret": "client-secret"
    },
    "query": {
      "entitySet": "PerPerson",
      "identityField": "personIdExternal",
      "deltaField": "lastModifiedDateTime",
      "select": [ "personIdExternal" ],
      "expand": [ "employmentNav" ]
    }
  },
  "ad": {
    "identityAttribute": "employeeID",
    "defaultActiveOu": "OU=Employees,DC=example,DC=com",
    "graveyardOu": "OU=Graveyard,DC=example,DC=com",
    "defaultPassword": "config-password"
  },
  "sync": {
    "enableBeforeStartDays": 7,
    "deletionRetentionDays": 90
  },
  "state": {
    "path": "$($statePath.Replace('\', '\\'))"
  },
  "reporting": {
    "outputDirectory": "$($reportDirectory.Replace('\', '\\'))",
    "reviewOutputDirectory": "$($reviewDirectory.Replace('\', '\\'))"
  },
  "persistence": {
    "sqlitePath": "$($sqlitePath.Replace('\', '\\'))"
  }
}
"@ | Set-Content -Path $configPath

        $sql = @"
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
INSERT INTO sync_state (state_path, checkpoint, raw_state_json, updated_at)
VALUES ('$($statePath.Replace("'", "''"))', '2026-03-22T12:00:00', '{"checkpoint":"2026-03-22T12:00:00","workers":{}}', '2026-03-22T12:05:00Z');
INSERT INTO runtime_status (state_path, run_id, status, stage, started_at, last_updated_at, completed_at, processed_workers, total_workers, snapshot_json)
VALUES ('$($statePath.Replace("'", "''"))', 'run-monitor-1', 'Succeeded', 'Completed', '2026-03-22T12:00:00Z', '2026-03-22T12:05:00Z', '2026-03-22T12:05:00Z', 1, 1, '{"runId":"run-monitor-1","status":"Succeeded","stage":"Completed","processedWorkers":1,"totalWorkers":1}');
INSERT INTO runs (run_id, state_path, path, artifact_type, mode, dry_run, status, started_at, completed_at, duration_seconds, report_json)
VALUES ('run-monitor-1', '$($statePath.Replace("'", "''"))', '/tmp/run-monitor.json', 'SyncReport', 'Delta', 0, 'Succeeded', '2026-03-22T12:00:00Z', '2026-03-22T12:05:00Z', 300, '{"runId":"run-monitor-1","operations":[],"creates":[],"updates":[],"enables":[],"disables":[],"graveyardMoves":[],"deletions":[],"quarantined":[],"conflicts":[],"guardrailFailures":[],"manualReview":[],"unchanged":[]}');
"@
        sqlite3 $sqlitePath $sql | Out-Null

        Mock Test-SyncFactorsMonitorSuccessFactorsConnection { [pscustomobject]@{ status = 'OK'; detail = 'oauth' } } -ModuleName Monitoring
        Mock Test-SyncFactorsMonitorActiveDirectoryConnection { [pscustomobject]@{ status = 'OK'; detail = 'dc01' } } -ModuleName Monitoring

        $status = Get-SyncFactorsMonitorStatus -ConfigPath $configPath -HistoryLimit 5

        $status.recentRuns.Count | Should -Be 1
        $status.latestRun.runId | Should -Be 'run-monitor-1'
        $status.paths.sqlitePath | Should -Be $sqlitePath
    }

    It 'resolves mapping config path from recent runs when no override is provided' {
        $mappingPath = Join-Path $TestDrive 'mapping.json'
        '{}' | Set-Content -Path $mappingPath

        $status = [pscustomobject]@{
            recentRuns = @(
                [pscustomobject]@{
                    mappingConfigPath = $mappingPath
                }
            )
        }

        $resolved = Resolve-SyncFactorsMonitorMappingConfigPath -Status $status

        $resolved | Should -Be (Resolve-Path -Path $mappingPath).Path
    }

    It 'includes all operation buckets in the dashboard browser' {
        $bucketNames = @(Get-SyncFactorsMonitorBucketDefinitions | ForEach-Object { $_.Name })

        $bucketNames | Should -Contain 'creates'
        $bucketNames | Should -Contain 'updates'
        $bucketNames | Should -Contain 'enables'
        $bucketNames | Should -Contain 'disables'
        $bucketNames | Should -Contain 'graveyardMoves'
        $bucketNames | Should -Contain 'deletions'
        $bucketNames | Should -Contain 'unchanged'
    }

    It 'uses review-first bucket ordering for review artifacts' {
        $bucketNames = @(Get-SyncFactorsMonitorBucketDefinitions -Mode Review | ForEach-Object { $_.Name })

        $bucketNames[0] | Should -Be 'updates'
        $bucketNames[1] | Should -Be 'unchanged'
        $bucketNames[2] | Should -Be 'creates'
        $bucketNames | Should -Not -Contain 'deletions'
    }

    It 'filters selected bucket items by text across object fields' {
        $bucketSelection = [pscustomobject]@{
            Bucket = [pscustomobject]@{
                Name = 'creates'
                Label = 'Creates'
            }
            Items = @(
                [pscustomobject]@{ workerId = '1001'; samAccountName = 'jdoe'; department = 'Sales' }
                [pscustomobject]@{ workerId = '1002'; samAccountName = 'asmith'; department = 'Finance' }
            )
        }
        $uiState = New-SyncFactorsMonitorUiState
        $uiState.filterText = 'finance'

        $items = @(Get-SyncFactorsMonitorFilteredBucketItems -BucketSelection $bucketSelection -UiState $uiState)

        $items.Count | Should -Be 1
        $items[0].workerId | Should -Be '1002'
    }

    It 'includes all run shortcuts in the default dashboard shortcut help' {
        $uiState = New-SyncFactorsMonitorUiState

        $uiState.autoRefreshEnabled | Should -BeFalse
        $uiState.statusMessage | Should -Match 't toggle auto-refresh'
        $uiState.statusMessage | Should -Match 'd delta dry-run'
        $uiState.statusMessage | Should -Match 's delta sync'
        $uiState.statusMessage | Should -Match 'f full dry-run'
        $uiState.statusMessage | Should -Match 'a full sync'
        $uiState.statusMessage | Should -Match 'w worker preview'
        $uiState.statusMessage | Should -Match 'z fresh reset'
        $uiState.statusMessage | Should -Match 'v review'
    }

    It 'finds the selected operation journal entry for the selected bucket object' {
        $reportPath = Join-Path $TestDrive 'syncfactors-Delta-20260312-220000.json'
        @{
            runId = 'run-123'
            status = 'Succeeded'
            operations = @(
                @{
                    sequence = 1
                    operationType = 'UpdateAttributes'
                    workerId = '1001'
                    bucket = 'updates'
                    target = @{ samAccountName = 'jdoe' }
                    before = @{ title = 'Old Title' }
                    after = @{ title = 'New Title' }
                }
            )
            updates = @(@{ workerId = '1001'; samAccountName = 'jdoe'; changedAttributes = @('title') })
            creates = @()
            enables = @()
            disables = @()
            graveyardMoves = @()
            deletions = @()
            quarantined = @()
            conflicts = @()
            guardrailFailures = @()
            manualReview = @()
            unchanged = @()
        } | ConvertTo-Json -Depth 10 | Set-Content -Path $reportPath

        $status = [pscustomobject]@{
            recentRuns = @(
                [pscustomobject]@{
                    runId = 'run-123'
                    path = $reportPath
                }
            )
            latestRun = [pscustomobject]@{
                runId = 'run-123'
                path = $reportPath
            }
        }
        $uiState = New-SyncFactorsMonitorUiState
        $uiState.selectedBucketIndex = 5

        $operation = Get-SyncFactorsMonitorSelectedBucketOperation -Status $status -UiState $uiState

        $operation.operationType | Should -Be 'UpdateAttributes'
        $operation.before.title | Should -Be 'Old Title'
        $operation.after.title | Should -Be 'New Title'
    }

    It 'formats selected update objects as compact diffs' {
        $selectedItem = [pscustomobject]@{
            workerId = '1001'
            samAccountName = 'jdoe'
            changedAttributes = @('title', 'department')
        }
        $selectedOperation = [pscustomobject]@{
            operationType = 'UpdateAttributes'
            target = [pscustomobject]@{
                samAccountName = 'jdoe'
            }
            before = [pscustomobject]@{
                title = 'Old Title'
                department = 'Finance'
            }
            after = [pscustomobject]@{
                title = 'New Title'
                department = 'Engineering'
            }
        }

        $lines = @(Format-SyncFactorsMonitorSelectedObjectLines -SelectedItem $selectedItem -SelectedOperation $selectedOperation)

        ($lines -join "`n") | Should -Match 'Item: workerId=1001'
        ($lines -join "`n") | Should -Match 'Action: Update attributes for jdoe'
        ($lines -join "`n") | Should -Match 'Δ title: Old Title -> New Title'
        ($lines -join "`n") | Should -Match 'Δ department: Finance -> Engineering'
    }

    It 'formats create and delete style operations without generic blobs' {
        $createLines = @(Format-SyncFactorsMonitorSelectedObjectLines `
                -SelectedItem ([pscustomobject]@{ workerId = '1001'; samAccountName = 'jdoe' }) `
                -SelectedOperation ([pscustomobject]@{
                    operationType = 'CreateUser'
                    target = [pscustomobject]@{ samAccountName = 'jdoe' }
                    before = $null
                    after = [pscustomobject]@{ samAccountName = 'jdoe'; enabled = 'True' }
                }))
        $deleteLines = @(Format-SyncFactorsMonitorSelectedObjectLines `
                -SelectedItem ([pscustomobject]@{ workerId = '1002'; samAccountName = 'adoe' }) `
                -SelectedOperation ([pscustomobject]@{
                    operationType = 'DeleteUser'
                    target = [pscustomobject]@{ samAccountName = 'adoe' }
                    before = [pscustomobject]@{ samAccountName = 'adoe'; enabled = 'False' }
                    after = $null
                }))

        ($createLines -join "`n") | Should -Match 'Action: Create account jdoe'
        ($deleteLines -join "`n") | Should -Match 'Action: Delete account adoe'
        ($createLines -join "`n") | Should -Match 'Δ samAccountName: \(unset\) -> jdoe'
        ($deleteLines -join "`n") | Should -Match 'Δ samAccountName: adoe -> \(unset\)'
    }

    It 'formats mapped attribute detail rows and handles missing operations' {
        $selectedItem = [pscustomobject]@{
            workerId = '1001'
            samAccountName = 'jdoe'
            changedAttributeDetails = @(
                [pscustomobject]@{
                    sourceField = 'jobInfo.department'
                    targetAttribute = 'department'
                    transform = 'none'
                    currentAdValue = 'Finance'
                    proposedValue = 'Engineering'
                }
            )
        }

        $lines = @(Format-SyncFactorsMonitorSelectedObjectLines -SelectedItem $selectedItem -SelectedOperation $null)

        ($lines -join "`n") | Should -Match 'Operation: no matching reversible operation recorded'
        ($lines -join "`n") | Should -Match 'Map: jobInfo.department -> department \[none\]'
        ($lines -join "`n") | Should -Match 'Finance -> Engineering'
    }

    It 'builds explorer diff rows from move operations without DN-only fields' {
        $entry = [pscustomobject]@{
            Item = [pscustomobject]@{
                workerId = '1001'
                samAccountName = 'jdoe'
            }
            Operation = [pscustomobject]@{
                operationType = 'MoveUser'
                before = [pscustomobject]@{
                    distinguishedName = 'CN=Jamie Doe,OU=Old,DC=example,DC=com'
                    parentOu = 'OU=Old,DC=example,DC=com'
                    title = 'Old Title'
                }
                after = [pscustomobject]@{
                    distinguishedName = 'CN=Jamie Doe,OU=New,DC=example,DC=com'
                    targetOu = 'OU=New,DC=example,DC=com'
                    title = 'New Title'
                    department = 'Engineering'
                }
            }
        }

        $rows = @(Get-SyncFactorsMonitorReportExplorerDiffRows -Entry $entry)

        $rows.Attribute | Should -Contain 'title'
        $rows.Attribute | Should -Contain 'department'
        $rows.Attribute | Should -Not -Contain 'distinguishedName'
        $rows.Attribute | Should -Not -Contain 'parentOu'
        $rows.Attribute | Should -Not -Contain 'targetOu'
        (@($rows | Where-Object { $_.Attribute -eq 'department' })[0].Marker) | Should -Be '[CREATE]'
    }

    It 'renders the report explorer for changed, created, and deleted entries' {
        $reportPath = Join-Path $TestDrive 'syncfactors-Review-20260312-220000.json'
        @{
            runId = 'review-123'
            status = 'Succeeded'
            operations = @(
                @{
                    sequence = 3
                    operationType = 'UpdateAttributes'
                    workerId = '1001'
                    bucket = 'updates'
                    target = @{ samAccountName = 'jdoe'; userPrincipalName = 'jdoe@example.com' }
                    before = @{ title = 'Old Title'; department = 'Finance' }
                    after = @{ title = 'New Title'; department = 'Engineering' }
                }
                @{
                    sequence = 2
                    operationType = 'CreateUser'
                    workerId = '1002'
                    bucket = 'creates'
                    target = @{ samAccountName = 'asmith'; userPrincipalName = 'asmith@example.com' }
                    before = @{}
                    after = @{ samAccountName = 'asmith'; enabled = 'True' }
                }
                @{
                    sequence = 1
                    operationType = 'DeleteUser'
                    workerId = '1003'
                    bucket = 'deletions'
                    target = @{ samAccountName = 'bdoe'; userPrincipalName = 'bdoe@example.com' }
                    before = @{ samAccountName = 'bdoe'; enabled = 'False' }
                    after = @{}
                }
            )
            updates = @(
                @{
                    workerId = '1001'
                    samAccountName = 'jdoe'
                    changedAttributeDetails = @(
                        @{
                            sourceField = 'jobInfo.title'
                            targetAttribute = 'title'
                            currentAdValue = 'Old Title'
                            proposedValue = 'New Title'
                        }
                    )
                }
            )
            creates = @(
                @{
                    workerId = '1002'
                    samAccountName = 'asmith'
                    targetOu = 'OU=Employees,DC=example,DC=com'
                }
            )
            deletions = @(
                @{
                    workerId = '1003'
                    samAccountName = 'bdoe'
                    reason = 'Inactive'
                }
            )
            enables = @()
            disables = @()
            graveyardMoves = @()
            quarantined = @()
            conflicts = @()
            guardrailFailures = @()
            manualReview = @()
            unchanged = @()
        } | ConvertTo-Json -Depth 20 | Set-Content -Path $reportPath

        $status = [pscustomobject]@{
            recentRuns = @(
                [pscustomobject]@{
                    runId = 'review-123'
                    path = $reportPath
                    status = 'Succeeded'
                    mode = 'Review'
                }
            )
            latestRun = [pscustomobject]@{
                runId = 'review-123'
                path = $reportPath
                status = 'Succeeded'
                mode = 'Review'
            }
        }
        $uiState = New-SyncFactorsMonitorUiState
        $uiState.viewMode = 'ReportExplorer'
        $uiState.statusMessage = 'Explorer ready'

        $selection = Get-SyncFactorsMonitorReportExplorerSelection -Status $status -UiState $uiState
        $selection.Categories.Count | Should -Be 3
        $selection.Entries.Count | Should -Be 3
        $selection.SelectedCategory.Name | Should -Be 'Changed'
        $selection.SelectedEntry.SamAccountName | Should -Be 'jdoe'

        $createdState = New-SyncFactorsMonitorUiState
        $createdState.viewMode = 'ReportExplorer'
        $createdState.reportCategoryIndex = 1
        $createdState.statusMessage = 'Explorer ready'

        $deletedState = New-SyncFactorsMonitorUiState
        $deletedState.viewMode = 'ReportExplorer'
        $deletedState.reportCategoryIndex = 2
        $deletedState.statusMessage = 'Explorer ready'

        $changedLines = @(Format-SyncFactorsMonitorDashboardView -Status $status -UiState $uiState)
        $createdLines = @(Format-SyncFactorsMonitorDashboardView -Status $status -UiState $createdState)
        $deletedLines = @(Format-SyncFactorsMonitorDashboardView -Status $status -UiState $deletedState)

        ($changedLines -join "`n") | Should -Match 'Report Explorer'
        ($changedLines -join "`n") | Should -Match 'Summary: \[UPDATE\] Changed=1    \[CREATE\] Created=1    \[DELETE\] Deleted=1'
        ($changedLines -join "`n") | Should -Match 'jobInfo.title'
        ($createdLines -join "`n") | Should -Match '\[CREATE\] workerId=1002'
        ($createdLines -join "`n") | Should -Match 'Target OU: OU=Employees,DC=example,DC=com'
        ($deletedLines -join "`n") | Should -Match '\[DELETE\] workerId=1003'
        ($deletedLines -join "`n") | Should -Match 'Inactive'
    }

    It 'resolves selected worker state from tracked workers' {
        $status = [pscustomobject]@{
            currentRun = [pscustomobject]@{
                currentWorkerId = $null
            }
            recentRuns = @()
            latestRun = [pscustomobject]@{}
            trackedWorkers = @(
                [pscustomobject]@{
                    workerId = '1002'
                    suppressed = $true
                    deleteAfter = '2026-03-20T00:00:00'
                }
            )
        }
        $uiState = New-SyncFactorsMonitorUiState
        $uiState.selectedBucketIndex = 0
        $bucketSelection = [pscustomobject]@{
            Bucket = [pscustomobject]@{
                Name = 'quarantined'
                Label = 'Quarantined'
            }
            Items = @(
                [pscustomobject]@{ workerId = '1002'; reason = 'ManagerNotResolved' }
            )
        }

        Mock Get-SyncFactorsMonitorSelectedBucket { $bucketSelection } -ModuleName Monitoring

        $workerState = Get-SyncFactorsMonitorSelectedWorkerState -Status $status -UiState $uiState

        $workerState.workerId | Should -Be '1002'
        $workerState.suppressed | Should -BeTrue
    }

    It 'formats dashboard view with selected run and selected bucket details' {
        $reportPath = Join-Path $TestDrive 'syncfactors-Delta-20260312-220000.json'
        @{
            runId = 'run-123'
            configPath = 'config.json'
            mappingConfigPath = 'mapping.json'
            mode = 'Delta'
            dryRun = $true
            startedAt = '2026-03-12T21:30:00'
            completedAt = '2026-03-12T21:35:00'
            status = 'Succeeded'
            operations = @()
            creates = @(@{ workerId = '1001'; samAccountName = 'jdoe' })
            updates = @(
                @{ workerId = '1003'; samAccountName = 'bchan'; changedAttributes = @('department') }
            )
            enables = @()
            disables = @(@{ workerId = '1004'; samAccountName = 'legacy.user'; targetState = 'Disabled' })
            graveyardMoves = @()
            deletions = @()
            quarantined = @(@{ workerId = '1002'; reason = 'ManagerNotResolved' })
            conflicts = @()
            guardrailFailures = @()
            manualReview = @()
            unchanged = @()
        } | ConvertTo-Json -Depth 10 | Set-Content -Path $reportPath

        $status = [pscustomobject]@{
            paths = [pscustomobject]@{
                configPath = 'config.json'
                statePath = 'state.json'
            }
            currentRun = [pscustomobject]@{
                status = 'InProgress'
                stage = 'ProcessingWorkers'
                mode = 'Delta'
                dryRun = $true
                startedAt = '2026-03-12T21:40:00'
                lastUpdatedAt = '2026-03-12T21:41:00'
                processedWorkers = 3
                totalWorkers = 5
                currentWorkerId = '1003'
                lastAction = 'Updated attributes for worker 1003.'
                errorMessage = $null
                creates = 1
                updates = 1
                enables = 0
                disables = 0
                graveyardMoves = 0
                deletions = 0
                quarantined = 1
                conflicts = 0
                guardrailFailures = 0
                manualReview = 0
                unchanged = 1
            }
            latestRun = [pscustomobject]@{
                status = 'Succeeded'
                mode = 'Delta'
                dryRun = $true
                startedAt = '2026-03-12T21:30:00'
                durationSeconds = 300
                reversibleOperations = 0
                creates = 1
                updates = 0
                enables = 0
                disables = 0
                graveyardMoves = 0
                deletions = 0
                quarantined = 1
                conflicts = 0
                guardrailFailures = 0
                manualReview = 0
                unchanged = 0
            }
            summary = [pscustomobject]@{
                lastCheckpoint = '2026-03-12T21:00:00'
                totalTrackedWorkers = 10
                suppressedWorkers = 1
                pendingDeletionWorkers = 0
            }
            health = [pscustomobject]@{
                successFactors = [pscustomobject]@{
                    status = 'OK'
                    detail = 'basic auth ok'
                }
                activeDirectory = [pscustomobject]@{
                    status = 'ERROR'
                    detail = 'bind failed'
                }
            }
            trackedWorkers = @(
                [pscustomobject]@{
                    workerId = '1002'
                    suppressed = $true
                    deleteAfter = '2026-03-20T00:00:00'
                    distinguishedName = 'CN=Jamie Doe,OU=Graveyard,DC=example,DC=com'
                    adObjectGuid = 'guid-1'
                    firstDisabledAt = '2026-03-10T00:00:00'
                    lastSeenStatus = 'inactive'
                }
            )
            context = [pscustomobject]@{
                successFactorsAuth = 'oauth (body client auth)'
                identityField = 'personIdExternal'
                identityAttribute = 'employeeID'
                defaultActiveOu = 'OU=Employees,DC=example,DC=com'
                graveyardOu = 'OU=Graveyard,DC=example,DC=com'
                enableBeforeStartDays = 7
                deletionRetentionDays = 90
                maxCreatesPerRun = 5
                maxDisablesPerRun = 5
                maxDeletionsPerRun = 5
            }
            recentRuns = @(
                [pscustomobject]@{
                    runId = 'run-123'
                    path = $reportPath
                    configPath = 'config.json'
                    mappingConfigPath = 'mapping.json'
                    status = 'Succeeded'
                    mode = 'Delta'
                    dryRun = $true
                    startedAt = '2026-03-12T21:30:00'
                    durationSeconds = 300
                    creates = 1
                    updates = 0
                    disables = 0
                    deletions = 0
                    conflicts = 0
                    guardrailFailures = 0
                }
            )
        }
        $uiState = New-SyncFactorsMonitorUiState
        $uiState.filterText = 'manager'

        $lines = @(Format-SyncFactorsMonitorDashboardView -Status $status -UiState $uiState)

        ($lines -join "`n") | Should -Match 'SuccessFactors AD Sync Dashboard'
        ($lines -join "`n") | Should -Match 'AutoRefresh: Paused'
        ($lines -join "`n") | Should -Match 'Health: SF=OK \(basic auth ok\)    AD=ERROR \(bind failed\)'
        ($lines -join "`n") | Should -Match 'SF Auth=oauth \(body client auth\)'
        ($lines -join "`n") | Should -Match 'Detail: Quarantined'
        ($lines -join "`n") | Should -Match 'Filter: manager'
        ($lines -join "`n") | Should -Match 'Diagnostics:'
        ($lines -join "`n") | Should -Match 'Selected Object'
        ($lines -join "`n") | Should -Match 'Operation: no matching reversible operation'
        ($lines -join "`n") | Should -Match 'workerId=1002'
        ($lines -join "`n") | Should -Match 'Tracked: workerId=1002'
        ($lines -join "`n") | Should -Not -Match 'Context:'
        ($lines -join "`n") | Should -Not -Match 'Paths:'
    }

    It 'formats review artifacts with review summary and mapped field details' {
        $reportPath = Join-Path $TestDrive 'syncfactors-Review-20260312-220000.json'
        @{
            runId = 'review-123'
            artifactType = 'FirstSyncReview'
            configPath = 'config.json'
            mappingConfigPath = 'mapping.json'
            mode = 'Review'
            dryRun = $true
            startedAt = '2026-03-12T21:30:00'
            completedAt = '2026-03-12T21:35:00'
            status = 'Succeeded'
            reviewSummary = @{
                existingUsersMatched = 2
                existingUsersWithAttributeChanges = 1
                existingUsersWithoutAttributeChanges = 1
                proposedCreates = 1
                proposedOffboarding = 0
                mappingCount = 3
                deletionPassSkipped = $true
            }
            operations = @()
            updates = @(
                @{
                    workerId = '1001'
                    samAccountName = 'jdoe'
                    reviewCategory = 'ExistingUserChanges'
                    changedAttributeDetails = @(
                        @{
                            sourceField = 'department'
                            targetAttribute = 'department'
                            transform = 'Trim'
                            currentAdValue = 'Finance'
                            proposedValue = 'Sales'
                        }
                    )
                }
            )
            unchanged = @()
            creates = @()
            enables = @()
            disables = @()
            graveyardMoves = @()
            deletions = @()
            quarantined = @()
            conflicts = @()
            guardrailFailures = @()
            manualReview = @()
        } | ConvertTo-Json -Depth 20 | Set-Content -Path $reportPath

        $status = [pscustomobject]@{
            paths = [pscustomobject]@{
                configPath = 'config.json'
                statePath = 'state.json'
                reportDirectory = 'reports'
                reviewReportDirectory = 'reviews'
            }
            currentRun = [pscustomobject]@{
                status = 'Idle'
                stage = 'Completed'
                mode = $null
                dryRun = $false
                startedAt = $null
                lastUpdatedAt = '2026-03-12T21:41:00'
                processedWorkers = 0
                totalWorkers = 0
                currentWorkerId = $null
                lastAction = 'No active sync run.'
                errorMessage = $null
                creates = 0
                updates = 0
                enables = 0
                disables = 0
                graveyardMoves = 0
                deletions = 0
                quarantined = 0
                conflicts = 0
                guardrailFailures = 0
                manualReview = 0
                unchanged = 0
            }
            latestRun = [pscustomobject]@{
                status = 'Succeeded'
                mode = 'Review'
                artifactType = 'FirstSyncReview'
                dryRun = $true
                startedAt = '2026-03-12T21:30:00'
                durationSeconds = 300
                reversibleOperations = 0
                creates = 0
                updates = 1
                enables = 0
                disables = 0
                graveyardMoves = 0
                deletions = 0
                quarantined = 0
                conflicts = 0
                guardrailFailures = 0
                manualReview = 0
                unchanged = 0
                reviewSummary = [pscustomobject]@{
                    existingUsersMatched = 2
                    existingUsersWithAttributeChanges = 1
                    existingUsersWithoutAttributeChanges = 1
                    proposedCreates = 1
                    proposedOffboarding = 0
                    mappingCount = 3
                    deletionPassSkipped = $true
                }
            }
            summary = [pscustomobject]@{
                lastCheckpoint = '2026-03-12T21:00:00'
                totalTrackedWorkers = 10
                suppressedWorkers = 1
                pendingDeletionWorkers = 0
            }
            trackedWorkers = @()
            context = [pscustomobject]@{
                identityField = 'personIdExternal'
                identityAttribute = 'employeeID'
                defaultActiveOu = 'OU=Employees,DC=example,DC=com'
                graveyardOu = 'OU=Graveyard,DC=example,DC=com'
                enableBeforeStartDays = 7
                deletionRetentionDays = 90
                maxCreatesPerRun = 5
                maxDisablesPerRun = 5
                maxDeletionsPerRun = 5
            }
            recentRuns = @(
                [pscustomobject]@{
                    runId = 'review-123'
                    path = $reportPath
                    configPath = 'config.json'
                    mappingConfigPath = 'mapping.json'
                    status = 'Succeeded'
                    mode = 'Review'
                    artifactType = 'FirstSyncReview'
                    dryRun = $true
                    startedAt = '2026-03-12T21:30:00'
                    durationSeconds = 300
                    creates = 0
                    updates = 1
                    disables = 0
                    deletions = 0
                    conflicts = 0
                    guardrailFailures = 0
                    reviewSummary = [pscustomobject]@{
                        existingUsersMatched = 2
                        existingUsersWithAttributeChanges = 1
                        existingUsersWithoutAttributeChanges = 1
                        proposedCreates = 1
                        proposedOffboarding = 0
                        mappingCount = 3
                        deletionPassSkipped = $true
                        operatorActionCases = [pscustomobject]@{
                            quarantinedWorkers = 1
                            unresolvedManagers = 1
                            rehireCases = 1
                        }
                    }
                }
            )
        }
        $uiState = New-SyncFactorsMonitorUiState

        $lines = @(Format-SyncFactorsMonitorDashboardView -Status $status -UiState $uiState)

        ($lines -join "`n") | Should -Match 'First Sync Review Summary'
        ($lines -join "`n") | Should -Match 'Review: existing=2 changed=1 aligned=1 creates=1 offboarding=0'
        ($lines -join "`n") | Should -Match 'Manual review cases: quarantined=1 unresolvedManagers=1 rehires=1'
        ($lines -join "`n") | Should -Match 'Map: department -> department \[Trim\]'
        ($lines -join "`n") | Should -Match 'Finance -> Sales'
    }

    It 'formats worker preview artifacts as review-style summaries' {
        $reportPath = Join-Path $TestDrive 'syncfactors-Review-20260312-220000.json'
        @{
            runId = 'preview-123'
            artifactType = 'WorkerPreview'
            configPath = 'config.json'
            mappingConfigPath = 'mapping.json'
            mode = 'Review'
            dryRun = $true
            startedAt = '2026-03-12T21:30:00'
            completedAt = '2026-03-12T21:35:00'
            status = 'Succeeded'
            reviewSummary = @{
                existingUsersMatched = 1
                existingUsersWithAttributeChanges = 1
                existingUsersWithoutAttributeChanges = 0
                proposedCreates = 0
                proposedOffboarding = 0
                mappingCount = 3
                deletionPassSkipped = $true
            }
            operations = @()
            updates = @(
                @{
                    workerId = '1001'
                    samAccountName = 'jdoe'
                    reviewCategory = 'ExistingUserChanges'
                    changedAttributeDetails = @(
                        @{
                            sourceField = 'department'
                            targetAttribute = 'department'
                            transform = 'Trim'
                            currentAdValue = 'Finance'
                            proposedValue = 'Sales'
                        }
                    )
                }
            )
            unchanged = @()
            creates = @()
            enables = @()
            disables = @()
            graveyardMoves = @()
            deletions = @()
            quarantined = @()
            conflicts = @()
            guardrailFailures = @()
            manualReview = @()
        } | ConvertTo-Json -Depth 20 | Set-Content -Path $reportPath

        $status = [pscustomobject]@{
            paths = [pscustomobject]@{
                configPath = 'config.json'
                statePath = 'state.json'
                reportDirectory = 'reports'
                reviewReportDirectory = 'reviews'
            }
            currentRun = [pscustomobject]@{
                status = 'Idle'
                stage = 'Completed'
                mode = $null
                dryRun = $false
                startedAt = $null
                lastUpdatedAt = '2026-03-12T21:41:00'
                processedWorkers = 0
                totalWorkers = 0
                currentWorkerId = $null
                lastAction = 'No active sync run.'
                errorMessage = $null
                creates = 0
                updates = 0
                enables = 0
                disables = 0
                graveyardMoves = 0
                deletions = 0
                quarantined = 0
                conflicts = 0
                guardrailFailures = 0
                manualReview = 0
                unchanged = 0
            }
            latestRun = [pscustomobject]@{
                status = 'Succeeded'
                mode = 'Review'
                artifactType = 'WorkerPreview'
                dryRun = $true
                startedAt = '2026-03-12T21:30:00'
                durationSeconds = 300
                reversibleOperations = 0
                creates = 0
                updates = 1
                enables = 0
                disables = 0
                graveyardMoves = 0
                deletions = 0
                quarantined = 0
                conflicts = 0
                guardrailFailures = 0
                manualReview = 0
                unchanged = 0
                reviewSummary = [pscustomobject]@{
                    existingUsersMatched = 1
                    existingUsersWithAttributeChanges = 1
                    existingUsersWithoutAttributeChanges = 0
                    proposedCreates = 0
                    proposedOffboarding = 0
                    mappingCount = 3
                    deletionPassSkipped = $true
                }
            }
            summary = [pscustomobject]@{
                lastCheckpoint = '2026-03-12T21:00:00'
                totalTrackedWorkers = 10
                suppressedWorkers = 1
                pendingDeletionWorkers = 0
            }
            trackedWorkers = @()
            context = [pscustomobject]@{
                identityField = 'personIdExternal'
                identityAttribute = 'employeeID'
                defaultActiveOu = 'OU=Employees,DC=example,DC=com'
                graveyardOu = 'OU=Graveyard,DC=example,DC=com'
                enableBeforeStartDays = 7
                deletionRetentionDays = 90
                maxCreatesPerRun = 5
                maxDisablesPerRun = 5
                maxDeletionsPerRun = 5
            }
            recentRuns = @(
                [pscustomobject]@{
                    runId = 'preview-123'
                    path = $reportPath
                    configPath = 'config.json'
                    mappingConfigPath = 'mapping.json'
                    status = 'Succeeded'
                    mode = 'Review'
                    artifactType = 'WorkerPreview'
                    workerScope = [pscustomobject]@{
                        workerId = '1001'
                    }
                    dryRun = $true
                    startedAt = '2026-03-12T21:30:00'
                    durationSeconds = 300
                    creates = 0
                    updates = 1
                    disables = 0
                    deletions = 0
                    conflicts = 0
                    guardrailFailures = 0
                    reviewSummary = [pscustomobject]@{
                        existingUsersMatched = 1
                        existingUsersWithAttributeChanges = 1
                        existingUsersWithoutAttributeChanges = 0
                        proposedCreates = 0
                        proposedOffboarding = 0
                        mappingCount = 3
                        deletionPassSkipped = $true
                        operatorActionCases = [pscustomobject]@{
                            quarantinedWorkers = 0
                            unresolvedManagers = 0
                            rehireCases = 1
                        }
                    }
                }
            )
        }
        $uiState = New-SyncFactorsMonitorUiState

        $lines = @(Format-SyncFactorsMonitorDashboardView -Status $status -UiState $uiState)

        ($lines -join "`n") | Should -Match 'Worker Preview Summary'
        ($lines -join "`n") | Should -Match 't auto-refresh'
        ($lines -join "`n") | Should -Match 'Review: existing=1 changed=1 aligned=0 creates=0 offboarding=0'
        ($lines -join "`n") | Should -Match 'Manual review cases: quarantined=0 unresolvedManagers=0 rehires=1'
        ($lines -join "`n") | Should -Match 'Finance -> Sales'
        ($lines -join "`n") | Should -Match 'd delta dry-run'
        ($lines -join "`n") | Should -Match 's delta sync'
        ($lines -join "`n") | Should -Match 'f full dry-run'
        ($lines -join "`n") | Should -Match 'a full sync'
        ($lines -join "`n") | Should -Match 'w worker preview'
        ($lines -join "`n") | Should -Match 'Single-worker review ready for'
        ($lines -join "`n") | Should -Match 'g to choose whether to apply this worker'
        ($lines -join "`n") | Should -Match 'z fresh reset'
    }

    It 'renders the inline worker preview diff screen with only changed attributes' {
        $previewResult = [pscustomobject]@{
            reportPath = 'preview-report.json'
            runId = 'preview-123'
            status = 'Succeeded'
            artifactType = 'WorkerPreview'
            workerScope = [pscustomobject]@{
                workerId = '1001'
            }
            preview = [pscustomobject]@{
                matchedExistingUser = $true
                samAccountName = 'jdoe'
                reviewCategory = 'ExistingUserChanges'
                reason = 'AttributeDelta'
                targetOu = 'OU=Employees,DC=example,DC=com'
            }
            changedAttributes = @(
                [pscustomobject]@{
                    sourceField = 'department'
                    targetAttribute = 'department'
                    currentAdValue = 'Finance'
                    proposedValue = 'Sales'
                }
                [pscustomobject]@{
                    sourceField = 'company'
                    targetAttribute = 'company'
                    currentAdValue = 'ExampleCo'
                    proposedValue = 'ExampleCo'
                }
            )
            operations = @()
        }
        $status = [pscustomobject]@{
            recentRuns = @()
            latestRun = [pscustomobject]@{}
        }
        $uiState = New-SyncFactorsMonitorUiState
        $uiState.viewMode = 'WorkerPreviewDiff'
        $uiState.workerPreviewResult = $previewResult
        $uiState.workerPreviewDiffRows = @(Get-SyncFactorsMonitorWorkerPreviewDiffRows -PreviewResult $previewResult)
        $uiState.statusMessage = 'Ready to apply.'

        $lines = @(Format-SyncFactorsMonitorDashboardView -Status $status -UiState $uiState)
        $joined = $lines -join "`n"

        $joined | Should -Match 'Single-Worker Diff Review'
        $joined | Should -Match 'Attribute\s+Old Value\s+New Value'
        $joined | Should -Match 'department\s+Finance\s+Sales'
        $joined | Should -Not -Match 'company'
        $joined | Should -Match 'Press a to apply this worker sync'
        $joined | Should -Match 'Press Esc to return to the dashboard'
    }

    It 'renders a no-changes message for empty inline worker preview diffs' {
        $previewResult = [pscustomobject]@{
            reportPath = 'preview-report.json'
            runId = 'preview-456'
            status = 'Succeeded'
            artifactType = 'WorkerPreview'
            workerScope = [pscustomobject]@{
                workerId = '1002'
            }
            preview = [pscustomobject]@{
                matchedExistingUser = $true
                samAccountName = 'asmith'
            }
            changedAttributes = @()
            operations = @()
        }
        $status = [pscustomobject]@{
            recentRuns = @()
            latestRun = [pscustomobject]@{}
        }
        $uiState = New-SyncFactorsMonitorUiState
        $uiState.viewMode = 'WorkerPreviewDiff'
        $uiState.workerPreviewResult = $previewResult
        $uiState.statusMessage = 'No changes found.'

        $lines = @(Format-SyncFactorsMonitorDashboardView -Status $status -UiState $uiState)

        ($lines -join "`n") | Should -Match 'No attribute changes were detected for this worker preview'
    }

    It 'formats concise post-run summary lines for a single-worker sync' {
        $syncResult = [pscustomobject]@{
            reportPath = 'worker-sync-report.json'
            runId = 'worker-sync-123'
            status = 'Succeeded'
            artifactType = 'WorkerSync'
            workerScope = [pscustomobject]@{
                workerId = '1001'
            }
        }
        $report = [pscustomobject]@{
            runId = 'worker-sync-123'
            status = 'Succeeded'
            artifactType = 'WorkerSync'
            creates = @()
            updates = @(
                [pscustomobject]@{ workerId = '1001'; samAccountName = 'jdoe' }
            )
            enables = @(
                [pscustomobject]@{ workerId = '1001'; samAccountName = 'jdoe' }
            )
            disables = @()
            graveyardMoves = @()
            deletions = @()
            operations = @(
                [pscustomobject]@{
                    operationType = 'UpdateAttributes'
                    target = [pscustomobject]@{ samAccountName = 'jdoe' }
                }
                [pscustomobject]@{
                    operationType = 'EnableUser'
                    target = [pscustomobject]@{ samAccountName = 'jdoe' }
                }
            )
        }

        $lines = @(Get-SyncFactorsMonitorWorkerSyncSummaryLines -SyncResult $syncResult -Report $report)
        $joined = $lines -join "`n"

        $joined | Should -Match 'Single-worker sync completed'
        $joined | Should -Match 'WorkerId=1001'
        $joined | Should -Match 'RunId=worker-sync-123'
        $joined | Should -Match 'Report=worker-sync-report.json'
        $joined | Should -Match 'Buckets: updates=1, enables=1'
        $joined | Should -Match 'UpdateAttributes \(jdoe\)'
        $joined | Should -Match 'EnableUser \(jdoe\)'
    }

    It 'renders a parsed report explorer with created changed and deleted objects' {
        $reportPath = Join-Path $TestDrive 'syncfactors-Delta-20260312-220000.json'
        @{
            runId = 'run-456'
            artifactType = 'SyncReport'
            mode = 'Delta'
            dryRun = $true
            startedAt = '2026-03-12T21:30:00'
            completedAt = '2026-03-12T21:35:00'
            status = 'Succeeded'
            operations = @(
                @{
                    sequence = 3
                    operationType = 'UpdateAttributes'
                    workerId = '1001'
                    bucket = 'updates'
                    target = @{ samAccountName = 'jdoe' }
                    before = @{ department = 'Finance' }
                    after = @{ department = 'Sales' }
                }
                @{
                    sequence = 2
                    operationType = 'CreateUser'
                    workerId = '1002'
                    bucket = 'creates'
                    target = @{ samAccountName = 'asmith' }
                    before = $null
                    after = @{ samAccountName = 'asmith'; enabled = 'True' }
                }
                @{
                    sequence = 1
                    operationType = 'DeleteUser'
                    workerId = '1003'
                    bucket = 'deletions'
                    target = @{ samAccountName = 'old.user' }
                    before = @{ samAccountName = 'old.user'; enabled = 'False' }
                    after = $null
                }
            )
            updates = @(
                @{
                    workerId = '1001'
                    samAccountName = 'jdoe'
                    changedAttributeDetails = @(
                        @{
                            sourceField = 'department'
                            targetAttribute = 'department'
                            transform = 'Trim'
                            currentAdValue = 'Finance'
                            proposedValue = 'Sales'
                        }
                    )
                }
            )
            creates = @(
                @{
                    workerId = '1002'
                    samAccountName = 'asmith'
                    targetOu = 'OU=Employees,DC=example,DC=com'
                }
            )
            deletions = @(
                @{
                    workerId = '1003'
                    samAccountName = 'old.user'
                    reason = 'InactiveRetentionElapsed'
                }
            )
            enables = @()
            disables = @()
            graveyardMoves = @()
            quarantined = @()
            conflicts = @()
            guardrailFailures = @()
            manualReview = @()
            unchanged = @()
        } | ConvertTo-Json -Depth 20 | Set-Content -Path $reportPath

        $status = [pscustomobject]@{
            paths = [pscustomobject]@{
                configPath = 'config.json'
                statePath = 'state.json'
            }
            currentRun = [pscustomobject]@{
                status = 'Idle'
                stage = 'Completed'
                mode = $null
                dryRun = $false
                startedAt = $null
                lastUpdatedAt = '2026-03-12T21:41:00'
                processedWorkers = 0
                totalWorkers = 0
                currentWorkerId = $null
                lastAction = 'No active sync run.'
                errorMessage = $null
                creates = 0
                updates = 0
                enables = 0
                disables = 0
                graveyardMoves = 0
                deletions = 0
                quarantined = 0
                conflicts = 0
                guardrailFailures = 0
                manualReview = 0
                unchanged = 0
            }
            latestRun = [pscustomobject]@{
                runId = 'run-456'
                path = $reportPath
                status = 'Succeeded'
                mode = 'Delta'
                artifactType = 'SyncReport'
            }
            recentRuns = @(
                [pscustomobject]@{
                    runId = 'run-456'
                    path = $reportPath
                    status = 'Succeeded'
                    mode = 'Delta'
                    artifactType = 'SyncReport'
                }
            )
        }
        $uiState = New-SyncFactorsMonitorUiState
        $uiState.viewMode = 'ReportExplorer'

        $lines = @(Format-SyncFactorsMonitorDashboardView -Status $status -UiState $uiState)

        ($lines -join "`n") | Should -Match 'Report Explorer'
        ($lines -join "`n") | Should -Match '\[UPDATE\] Changed=1'
        ($lines -join "`n") | Should -Match '\[CREATE\] Created=1'
        ($lines -join "`n") | Should -Match '\[DELETE\] Deleted=1'
        ($lines -join "`n") | Should -Match 'jdoe'
        ($lines -join "`n") | Should -Match 'Action: Update attributes for jdoe'
        ($lines -join "`n") | Should -Match '\[UPDATE\] department \[department\]: Finance -> Sales'

        $uiState.reportCategoryIndex = 1
        $createLines = @(Format-SyncFactorsMonitorDashboardView -Status $status -UiState $uiState)
        ($createLines -join "`n") | Should -Match 'asmith'
        ($createLines -join "`n") | Should -Match 'Action: Create account asmith'
        ($createLines -join "`n") | Should -Match '\[CREATE\] enabled: \(unset\) -> True'

        $uiState.reportCategoryIndex = 2
        $deleteLines = @(Format-SyncFactorsMonitorDashboardView -Status $status -UiState $uiState)
        ($deleteLines -join "`n") | Should -Match 'old.user'
        ($deleteLines -join "`n") | Should -Match 'Action: Delete account old.user'
        ($deleteLines -join "`n") | Should -Match '\[DELETE\] enabled: False -> \(unset\)'
    }

    It 'formats disable and move operations as human readable actions' {
        $disableLines = @(Format-SyncFactorsMonitorSelectedObjectLines `
                -SelectedItem ([pscustomobject]@{ workerId = '44522'; samAccountName = '44522' }) `
                -SelectedOperation ([pscustomobject]@{
                    operationType = 'DisableUser'
                    target = [pscustomobject]@{ samAccountName = '44522' }
                    before = [pscustomobject]@{ enabled = 'True' }
                    after = [pscustomobject]@{ enabled = 'False' }
                }))
        $moveLines = @(Format-SyncFactorsMonitorSelectedObjectLines `
                -SelectedItem ([pscustomobject]@{ workerId = '44522'; samAccountName = '44522'; targetOu = 'OU=GRAVEYARD,DC=example,DC=com' }) `
                -SelectedOperation ([pscustomobject]@{
                    operationType = 'MoveUser'
                    target = [pscustomobject]@{ samAccountName = '44522' }
                    before = [pscustomobject]@{
                        distinguishedName = 'CN=44522,OU=TestUsers,DC=example,DC=com'
                        parentOu = 'OU=TestUsers,DC=example,DC=com'
                    }
                    after = [pscustomobject]@{
                        targetOu = 'OU=GRAVEYARD,DC=example,DC=com'
                    }
                }))

        ($disableLines -join "`n") | Should -Match 'Action: Disable account 44522'
        ($disableLines -join "`n") | Should -Match 'Effect: Account sign-in will be turned off'
        ($moveLines -join "`n") | Should -Match 'Action: Move account 44522'
        ($moveLines -join "`n") | Should -Match 'From OU: OU=TestUsers,DC=example,DC=com'
        ($moveLines -join "`n") | Should -Match 'To OU: OU=GRAVEYARD,DC=example,DC=com'
        ($moveLines -join "`n") | Should -Not -Match 'Δ distinguishedName:'
        ($moveLines -join "`n") | Should -Not -Match 'Δ parentOu:'
        ($moveLines -join "`n") | Should -Not -Match 'Δ targetOu:'
    }

    It 'renders operator actions for manual review cases' {
        $lines = @(Format-SyncFactorsMonitorSelectedObjectLines `
                -SelectedItem ([pscustomobject]@{
                    workerId = '6051'
                    reason = 'ManagerNotResolved'
                    reviewCaseType = 'UnresolvedManager'
                    operatorActionSummary = 'Resolve the worker manager before applying AD changes.'
                    operatorActions = @(
                        [pscustomobject]@{
                            code = 'ResolveManagerIdentity'
                            label = 'Resolve manager identity'
                            description = 'Find or create the manager account for employee ID 9999 before retrying the worker sync.'
                        }
                    )
                }) `
                -SelectedOperation $null)

        ($lines -join "`n") | Should -Match 'Review workflow: UnresolvedManager'
        ($lines -join "`n") | Should -Match 'Operator summary: Resolve the worker manager before applying AD changes.'
        ($lines -join "`n") | Should -Match 'Operator action: Resolve manager identity - Find or create the manager account'
    }

    It 'renders a modal action prompt for selected worker preview runs' {
        $status = [pscustomobject]@{
            paths = [pscustomobject]@{
                configPath = 'config.json'
                statePath = 'state.json'
                reportDirectory = 'reports'
                reviewReportDirectory = 'reviews'
            }
            currentRun = [pscustomobject]@{
                status = 'Idle'
                stage = 'Completed'
                mode = $null
                dryRun = $false
                startedAt = $null
                lastUpdatedAt = '2026-03-12T21:41:00'
                processedWorkers = 0
                totalWorkers = 0
                currentWorkerId = $null
                lastAction = 'No active sync run.'
                errorMessage = $null
            }
            latestRun = [pscustomobject]@{
                status = 'Succeeded'
                mode = 'Review'
                artifactType = 'WorkerPreview'
                dryRun = $true
                durationSeconds = 300
                workerScope = [pscustomobject]@{
                    workerId = '1001'
                }
                creates = 0
                updates = 1
                disables = 0
                deletions = 0
                quarantined = 0
                conflicts = 0
                guardrailFailures = 0
            }
            summary = [pscustomobject]@{
                lastCheckpoint = '2026-03-12T21:00:00'
                totalTrackedWorkers = 10
                suppressedWorkers = 1
                pendingDeletionWorkers = 0
            }
            trackedWorkers = @()
            context = [pscustomobject]@{
                identityField = 'personIdExternal'
                identityAttribute = 'employeeID'
            }
            recentRuns = @(
                [pscustomobject]@{
                    runId = 'preview-123'
                    path = 'report.json'
                    configPath = 'config.json'
                    mappingConfigPath = 'mapping.json'
                    status = 'Succeeded'
                    mode = 'Review'
                    artifactType = 'WorkerPreview'
                    dryRun = $true
                    startedAt = '2026-03-12T21:30:00'
                    durationSeconds = 300
                    creates = 0
                    updates = 1
                    disables = 0
                    deletions = 0
                    conflicts = 0
                    guardrailFailures = 0
                    reviewSummary = [pscustomobject]@{
                        existingUsersMatched = 1
                        existingUsersWithAttributeChanges = 1
                        existingUsersWithoutAttributeChanges = 0
                        proposedCreates = 0
                        proposedOffboarding = 0
                    }
                    workerScope = [pscustomobject]@{
                        workerId = '1001'
                    }
                }
            )
        }
        $uiState = New-SyncFactorsMonitorUiState
        $uiState.pendingAction = 'ApplyWorkerSync'
        $uiState.pendingWorkerId = '1001'

        $lines = @(Format-SyncFactorsMonitorDashboardView -Status $status -UiState $uiState)

        ($lines -join "`n") | Should -Match 'Worker Review Actions'
        ($lines -join "`n") | Should -Match 'Press a to write the reviewed changes to AD'
        ($lines -join "`n") | Should -Match 'Press o to choose a related worker report'
    }

    It 'lists related worker preview and sync reports in the worker report picker' {
        $status = [pscustomobject]@{
            paths = [pscustomobject]@{
                configPath = 'config.json'
                statePath = 'state.json'
                reportDirectory = 'reports'
                reviewReportDirectory = 'reviews'
            }
            currentRun = [pscustomobject]@{
                status = 'Idle'
                stage = 'Completed'
                mode = $null
                dryRun = $false
                startedAt = $null
                lastUpdatedAt = '2026-03-12T21:41:00'
                processedWorkers = 0
                totalWorkers = 0
                currentWorkerId = $null
                lastAction = 'No active sync run.'
                errorMessage = $null
            }
            latestRun = [pscustomobject]@{
                status = 'Succeeded'
                mode = 'Review'
                artifactType = 'WorkerPreview'
                workerScope = [pscustomobject]@{
                    workerId = '1001'
                }
            }
            summary = [pscustomobject]@{
                lastCheckpoint = '2026-03-12T21:00:00'
                totalTrackedWorkers = 10
                suppressedWorkers = 1
                pendingDeletionWorkers = 0
            }
            trackedWorkers = @()
            context = [pscustomobject]@{
                identityField = 'personIdExternal'
                identityAttribute = 'employeeID'
            }
            recentRuns = @(
                [pscustomobject]@{
                    runId = 'preview-123'
                    path = 'preview-report.json'
                    status = 'Succeeded'
                    mode = 'Review'
                    artifactType = 'WorkerPreview'
                    dryRun = $true
                    durationSeconds = 300
                    workerScope = [pscustomobject]@{
                        workerId = '1001'
                    }
                    startedAt = '2026-03-12T21:30:00'
                    creates = 0
                    updates = 1
                    disables = 0
                    deletions = 0
                    quarantined = 0
                    conflicts = 0
                    guardrailFailures = 0
                }
                [pscustomobject]@{
                    runId = 'sync-123'
                    path = 'sync-report.json'
                    status = 'Succeeded'
                    mode = 'Full'
                    artifactType = 'WorkerSync'
                    dryRun = $false
                    durationSeconds = 120
                    workerScope = [pscustomobject]@{
                        workerId = '1001'
                    }
                    startedAt = '2026-03-12T21:45:00'
                    creates = 0
                    updates = 1
                    disables = 0
                    deletions = 0
                    quarantined = 0
                    conflicts = 0
                    guardrailFailures = 0
                }
                [pscustomobject]@{
                    runId = 'preview-999'
                    path = 'other-worker-preview.json'
                    status = 'Succeeded'
                    mode = 'Review'
                    artifactType = 'WorkerPreview'
                    dryRun = $true
                    durationSeconds = 300
                    workerScope = [pscustomobject]@{
                        workerId = '9999'
                    }
                    startedAt = '2026-03-12T20:30:00'
                    creates = 0
                    updates = 0
                    disables = 0
                    deletions = 0
                    quarantined = 0
                    conflicts = 0
                    guardrailFailures = 0
                }
            )
        }
        $uiState = New-SyncFactorsMonitorUiState
        $uiState.pendingAction = 'WorkerReportPicker'
        $uiState.pendingWorkerId = '1001'

        $relatedRuns = @(Get-SyncFactorsMonitorWorkerRelatedRuns -Status $status -WorkerId '1001')
        $relatedRuns.Count | Should -Be 2
        $relatedRuns[0].Run.artifactType | Should -Be 'WorkerPreview'
        $relatedRuns[1].Run.artifactType | Should -Be 'WorkerSync'

        $lines = @(Format-SyncFactorsMonitorDashboardView -Status $status -UiState $uiState)
        ($lines -join "`n") | Should -Match 'Worker Report Picker'
        ($lines -join "`n") | Should -Match 'WorkerPreview Succeeded'
        ($lines -join "`n") | Should -Match 'WorkerSync Succeeded'
        ($lines -join "`n") | Should -Match 'Press Enter or o to open the selected report'
    }

    It 'uses the selected run rather than latest run in the summary panel' {
        $selectedReportPath = Join-Path $TestDrive 'syncfactors-Review-20260312-220000.json'
        @{
            runId = 'review-older'
            artifactType = 'FirstSyncReview'
            configPath = 'config.json'
            mappingConfigPath = 'mapping.json'
            mode = 'Review'
            dryRun = $true
            startedAt = '2026-03-12T21:30:00'
            completedAt = '2026-03-12T21:35:00'
            status = 'Succeeded'
            reviewSummary = @{
                existingUsersMatched = 3
                existingUsersWithAttributeChanges = 2
                existingUsersWithoutAttributeChanges = 1
                proposedCreates = 0
                proposedOffboarding = 0
                mappingCount = 3
                deletionPassSkipped = $true
            }
            operations = @()
            updates = @(@{ workerId = '1001'; samAccountName = 'jdoe' })
            creates = @()
            enables = @()
            disables = @()
            graveyardMoves = @()
            deletions = @()
            quarantined = @()
            conflicts = @()
            guardrailFailures = @()
            manualReview = @()
            unchanged = @()
        } | ConvertTo-Json -Depth 20 | Set-Content -Path $selectedReportPath

        $status = [pscustomobject]@{
            paths = [pscustomobject]@{
                configPath = 'config.json'
                statePath = 'state.json'
                reportDirectory = 'reports'
                reviewReportDirectory = 'reviews'
            }
            currentRun = [pscustomobject]@{
                status = 'Idle'
                stage = 'Completed'
                mode = $null
                dryRun = $false
                startedAt = $null
                lastUpdatedAt = '2026-03-12T21:41:00'
                processedWorkers = 0
                totalWorkers = 0
                currentWorkerId = $null
                lastAction = 'No active sync run.'
                errorMessage = $null
                creates = 0
                updates = 0
                enables = 0
                disables = 0
                graveyardMoves = 0
                deletions = 0
                quarantined = 0
                conflicts = 0
                guardrailFailures = 0
                manualReview = 0
                unchanged = 0
            }
            latestRun = [pscustomobject]@{
                status = 'Succeeded'
                mode = 'Delta'
                artifactType = 'SyncReport'
                dryRun = $true
                startedAt = '2026-03-13T09:00:00'
                durationSeconds = 60
                reversibleOperations = 0
                creates = 9
                updates = 8
                enables = 0
                disables = 0
                graveyardMoves = 0
                deletions = 0
                quarantined = 7
                conflicts = 6
                guardrailFailures = 5
                manualReview = 0
                unchanged = 4
            }
            summary = [pscustomobject]@{
                lastCheckpoint = '2026-03-12T21:00:00'
                totalTrackedWorkers = 10
                suppressedWorkers = 1
                pendingDeletionWorkers = 0
            }
            trackedWorkers = @()
            context = [pscustomobject]@{
                identityField = 'personIdExternal'
                identityAttribute = 'employeeID'
                defaultActiveOu = 'OU=Employees,DC=example,DC=com'
                graveyardOu = 'OU=Graveyard,DC=example,DC=com'
                enableBeforeStartDays = 7
                deletionRetentionDays = 90
                maxCreatesPerRun = 5
                maxDisablesPerRun = 5
                maxDeletionsPerRun = 5
            }
            recentRuns = @(
                [pscustomobject]@{
                    runId = 'latest-delta'
                    path = 'latest.json'
                    status = 'Succeeded'
                    mode = 'Delta'
                    artifactType = 'SyncReport'
                    dryRun = $true
                    startedAt = '2026-03-13T09:00:00'
                    durationSeconds = 60
                    creates = 9
                    updates = 8
                    enables = 0
                    disables = 0
                    graveyardMoves = 0
                    deletions = 0
                    quarantined = 7
                    conflicts = 6
                    guardrailFailures = 5
                    manualReview = 0
                    unchanged = 4
                }
                [pscustomobject]@{
                    runId = 'review-older'
                    path = $selectedReportPath
                    status = 'Succeeded'
                    mode = 'Review'
                    artifactType = 'FirstSyncReview'
                    dryRun = $true
                    startedAt = '2026-03-12T21:30:00'
                    durationSeconds = 300
                    reversibleOperations = 0
                    creates = 0
                    updates = 1
                    enables = 0
                    disables = 0
                    graveyardMoves = 0
                    deletions = 0
                    quarantined = 0
                    conflicts = 0
                    guardrailFailures = 0
                    manualReview = 0
                    unchanged = 0
                    reviewSummary = [pscustomobject]@{
                        existingUsersMatched = 3
                        existingUsersWithAttributeChanges = 2
                        existingUsersWithoutAttributeChanges = 1
                        proposedCreates = 0
                        proposedOffboarding = 0
                        mappingCount = 3
                        deletionPassSkipped = $true
                    }
                }
            )
        }
        $uiState = New-SyncFactorsMonitorUiState
        $uiState.selectedRunIndex = 1

        $lines = @(Format-SyncFactorsMonitorDashboardView -Status $status -UiState $uiState)

        ($lines -join "`n") | Should -Match 'First Sync Review Summary'
        ($lines -join "`n") | Should -Match 'Status: Succeeded    Mode: Review'
        ($lines -join "`n") | Should -Match 'Totals: C=0 U=1 D=0 X=0'
        ($lines -join "`n") | Should -Not -Match 'Totals: C=9 U=8 D=0 X=0'
    }

    It 'limits recent runs and detail rows in the compact dashboard' {
        $reportPath = Join-Path $TestDrive 'syncfactors-Delta-20260312-220000.json'
        @{
            runId = 'run-limit'
            configPath = 'config.json'
            mappingConfigPath = 'mapping.json'
            mode = 'Delta'
            dryRun = $true
            startedAt = '2026-03-12T21:30:00'
            completedAt = '2026-03-12T21:35:00'
            status = 'Succeeded'
            operations = @()
            creates = @(
                @{ workerId = '1001'; samAccountName = 'user1' }
                @{ workerId = '1002'; samAccountName = 'user2' }
                @{ workerId = '1003'; samAccountName = 'user3' }
                @{ workerId = '1004'; samAccountName = 'user4' }
                @{ workerId = '1005'; samAccountName = 'user5' }
                @{ workerId = '1006'; samAccountName = 'user6' }
            )
            updates = @()
            enables = @()
            disables = @()
            graveyardMoves = @()
            deletions = @()
            quarantined = @()
            conflicts = @()
            guardrailFailures = @()
            manualReview = @()
            unchanged = @()
        } | ConvertTo-Json -Depth 10 | Set-Content -Path $reportPath

        $recentRuns = @()
        for ($i = 1; $i -le 6; $i += 1) {
            $recentRuns += [pscustomobject]@{
                runId = "run-$i"
                path = $reportPath
                status = 'Succeeded'
                mode = 'Delta'
                dryRun = $true
                startedAt = "2026-03-12T21:3$($i - 1):00"
                durationSeconds = 60 * $i
                creates = $i
                updates = 0
                quarantined = 0
                disables = 0
                deletions = 0
                conflicts = 0
                guardrailFailures = 0
            }
        }

        $status = [pscustomobject]@{
            paths = [pscustomobject]@{
                configPath = 'config.json'
                statePath = 'state.json'
            }
            currentRun = [pscustomobject]@{
                status = 'Idle'
                stage = 'Completed'
                mode = $null
                dryRun = $false
                startedAt = $null
                lastUpdatedAt = '2026-03-12T21:41:00'
                processedWorkers = 0
                totalWorkers = 0
                currentWorkerId = $null
                lastAction = 'No active sync run.'
                errorMessage = $null
                creates = 0
                updates = 0
                enables = 0
                disables = 0
                graveyardMoves = 0
                deletions = 0
                quarantined = 0
                conflicts = 0
                guardrailFailures = 0
                manualReview = 0
                unchanged = 0
            }
            latestRun = $recentRuns[0]
            summary = [pscustomobject]@{
                lastCheckpoint = '2026-03-12T21:00:00'
                totalTrackedWorkers = 10
                suppressedWorkers = 1
                pendingDeletionWorkers = 0
            }
            trackedWorkers = @()
            context = [pscustomobject]@{
                identityField = 'personIdExternal'
                identityAttribute = 'employeeID'
                defaultActiveOu = 'OU=Employees,DC=example,DC=com'
                graveyardOu = 'OU=Graveyard,DC=example,DC=com'
                enableBeforeStartDays = 7
                deletionRetentionDays = 90
                maxCreatesPerRun = 5
                maxDisablesPerRun = 5
                maxDeletionsPerRun = 5
            }
            recentRuns = $recentRuns
        }
        $uiState = New-SyncFactorsMonitorUiState
        $uiState.selectedBucketIndex = 4

        $lines = @(Format-SyncFactorsMonitorDashboardView -Status $status -UiState $uiState)
        $joined = $lines -join "`n"
        $recentRunLines = @($lines | Where-Object { $_ -match '^\s+[>]?\s*Succeeded\s+Delta' })
        $detailLines = @($lines | Where-Object { $_ -match '^[>-] .*workerId=' })

        $recentRunLines.Count | Should -Be 5
        $detailLines.Count | Should -Be 4
        $joined | Should -Match '\.\.\. 1 older runs'
        $joined | Should -Match '\.\.\. 2 more'
    }
}
