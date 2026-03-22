Describe 'Script entrypoints' {
    BeforeAll {
        function New-StatusConfigContent {
            param(
                [Parameter(Mandatory)]
                [string]$StatePath,
                [Parameter(Mandatory)]
                [string]$ReportDirectory
            )
            return @{
                successFactors = @{
                    baseUrl = 'https://example.successfactors.com/odata/v2'
                    oauth = @{
                        tokenUrl = 'https://example.successfactors.com/oauth/token'
                        clientId = 'client-id'
                        clientSecret = 'client-secret'
                    }
                    query = @{
                        entitySet = 'PerPerson'
                        identityField = 'personIdExternal'
                        deltaField = 'lastModifiedDateTime'
                        select = @('personIdExternal')
                        expand = @('employmentNav')
                    }
                }
                ad = @{
                    identityAttribute = 'employeeID'
                    defaultActiveOu = 'OU=Employees,DC=example,DC=com'
                    graveyardOu = 'OU=Graveyard,DC=example,DC=com'
                    defaultPassword = 'password'
                }
                sync = @{
                    enableBeforeStartDays = 7
                    deletionRetentionDays = 90
                }
                state = @{
                    path = $StatePath
                }
                reporting = @{
                    outputDirectory = $ReportDirectory
                }
            } | ConvertTo-Json -Depth 10
        }

        function New-InteractiveConfigContent {
            param(
                [Parameter(Mandatory)]
                [string]$ClientId,
                [Parameter(Mandatory)]
                [string]$ClientSecret,
                [Parameter(Mandatory)]
                [string]$DefaultPassword,
                [string]$AdServer = '',
                [string]$AdUsername = '',
                [string]$AdBindPassword = ''
            )

            return @{
                secrets = @{
                    successFactorsClientIdEnv = 'TEST_SF_CLIENT_ID'
                    successFactorsClientSecretEnv = 'TEST_SF_CLIENT_SECRET'
                    defaultAdPasswordEnv = 'TEST_AD_PASSWORD'
                    adServerEnv = 'TEST_AD_SERVER'
                    adUsernameEnv = 'TEST_AD_USERNAME'
                    adBindPasswordEnv = 'TEST_AD_BIND_PASSWORD'
                }
                successFactors = @{
                    baseUrl = 'https://example.successfactors.com/odata/v2'
                    oauth = @{
                        tokenUrl = 'https://example.successfactors.com/oauth/token'
                        clientId = $ClientId
                        clientSecret = $ClientSecret
                    }
                    query = @{
                        entitySet = 'PerPerson'
                        identityField = 'personIdExternal'
                        deltaField = 'lastModifiedDateTime'
                        select = @('personIdExternal')
                        expand = @('employmentNav')
                    }
                }
                ad = @{
                    server = $AdServer
                    username = $AdUsername
                    bindPassword = $AdBindPassword
                    identityAttribute = 'employeeID'
                    defaultActiveOu = 'OU=Employees,DC=example,DC=com'
                    graveyardOu = 'OU=Graveyard,DC=example,DC=com'
                    defaultPassword = $DefaultPassword
                }
                sync = @{
                    enableBeforeStartDays = 7
                    deletionRetentionDays = 90
                }
                state = @{
                    path = '.\state\sync-state.json'
                }
                reporting = @{
                    outputDirectory = '.\reports\output'
                }
            } | ConvertTo-Json -Depth 10
        }

        function ConvertTo-TestSqliteLiteral {
            param($Value)

            if ($null -eq $Value) {
                return 'NULL'
            }

            return "'$($Value.ToString().Replace("'", "''"))'"
        }

        function ConvertTo-TestSqliteJsonLiteral {
            param($Value)

            if ($null -eq $Value) {
                return 'NULL'
            }

            return ConvertTo-TestSqliteLiteral -Value ($Value | ConvertTo-Json -Depth 20 -Compress)
        }

        function Initialize-StatusSqliteFixture {
            param(
                [Parameter(Mandatory)]
                [string]$StatePath,
                [Parameter(Mandatory)]
                [string]$DatabasePath,
                [AllowNull()]
                [string]$Checkpoint,
                [AllowNull()]
                [object[]]$Workers = @(),
                [AllowNull()]
                [object]$CurrentRun = $null,
                [AllowNull()]
                [object[]]$Runs = @()
            )

            $databaseDirectory = Split-Path -Path $DatabasePath -Parent
            if ($databaseDirectory -and -not (Test-Path -Path $databaseDirectory -PathType Container)) {
                New-Item -Path $databaseDirectory -ItemType Directory -Force | Out-Null
            }
            foreach ($candidate in @($DatabasePath, "$DatabasePath-shm", "$DatabasePath-wal")) {
                if (Test-Path -Path $candidate -PathType Leaf) {
                    Remove-Item -Path $candidate -Force
                }
            }

            $statePayload = @{
                checkpoint = $Checkpoint
                workers = @{}
            }
            foreach ($worker in @($Workers)) {
                $workerId = "$($worker.workerId)"
                $statePayload.workers[$workerId] = @{
                    adObjectGuid = if ($worker.PSObject.Properties.Name -contains 'adObjectGuid') { $worker.adObjectGuid } else { $null }
                    distinguishedName = if ($worker.PSObject.Properties.Name -contains 'distinguishedName') { $worker.distinguishedName } else { $null }
                    suppressed = if ($worker.PSObject.Properties.Name -contains 'suppressed') { [bool]$worker.suppressed } else { $false }
                    firstDisabledAt = if ($worker.PSObject.Properties.Name -contains 'firstDisabledAt') { $worker.firstDisabledAt } else { $null }
                    deleteAfter = if ($worker.PSObject.Properties.Name -contains 'deleteAfter') { $worker.deleteAfter } else { $null }
                    lastSeenStatus = if ($worker.PSObject.Properties.Name -contains 'lastSeenStatus') { $worker.lastSeenStatus } else { $null }
                }
            }

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
DELETE FROM sync_state WHERE state_path = $(ConvertTo-TestSqliteLiteral -Value $StatePath);
INSERT INTO sync_state (state_path, checkpoint, raw_state_json, updated_at)
VALUES (
  $(ConvertTo-TestSqliteLiteral -Value $StatePath),
  $(ConvertTo-TestSqliteLiteral -Value $Checkpoint),
  $(ConvertTo-TestSqliteJsonLiteral -Value $statePayload),
  '2026-03-22T12:05:00Z'
);
"@

            foreach ($worker in @($Workers)) {
                $workerPayload = @{
                    adObjectGuid = if ($worker.PSObject.Properties.Name -contains 'adObjectGuid') { $worker.adObjectGuid } else { $null }
                    distinguishedName = if ($worker.PSObject.Properties.Name -contains 'distinguishedName') { $worker.distinguishedName } else { $null }
                    suppressed = if ($worker.PSObject.Properties.Name -contains 'suppressed') { [bool]$worker.suppressed } else { $false }
                    firstDisabledAt = if ($worker.PSObject.Properties.Name -contains 'firstDisabledAt') { $worker.firstDisabledAt } else { $null }
                    deleteAfter = if ($worker.PSObject.Properties.Name -contains 'deleteAfter') { $worker.deleteAfter } else { $null }
                    lastSeenStatus = if ($worker.PSObject.Properties.Name -contains 'lastSeenStatus') { $worker.lastSeenStatus } else { $null }
                }
                $sql += @"
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
) VALUES (
  $(ConvertTo-TestSqliteLiteral -Value $StatePath),
  $(ConvertTo-TestSqliteLiteral -Value $worker.workerId),
  $(ConvertTo-TestSqliteLiteral -Value $(if ($worker.PSObject.Properties.Name -contains 'adObjectGuid') { $worker.adObjectGuid } else { $null })),
  $(ConvertTo-TestSqliteLiteral -Value $(if ($worker.PSObject.Properties.Name -contains 'distinguishedName') { $worker.distinguishedName } else { $null })),
  $(if ($worker.PSObject.Properties.Name -contains 'suppressed' -and [bool]$worker.suppressed) { 1 } else { 0 }),
  $(ConvertTo-TestSqliteLiteral -Value $(if ($worker.PSObject.Properties.Name -contains 'firstDisabledAt') { $worker.firstDisabledAt } else { $null })),
  $(ConvertTo-TestSqliteLiteral -Value $(if ($worker.PSObject.Properties.Name -contains 'deleteAfter') { $worker.deleteAfter } else { $null })),
  $(ConvertTo-TestSqliteLiteral -Value $(if ($worker.PSObject.Properties.Name -contains 'lastSeenStatus') { $worker.lastSeenStatus } else { $null })),
  $(ConvertTo-TestSqliteJsonLiteral -Value $workerPayload),
  '2026-03-22T12:05:00Z'
);
"@
            }

            if ($CurrentRun) {
                $sql += @"
INSERT OR REPLACE INTO runtime_status (
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
) VALUES (
  $(ConvertTo-TestSqliteLiteral -Value $StatePath),
  $(ConvertTo-TestSqliteLiteral -Value $CurrentRun.runId),
  $(ConvertTo-TestSqliteLiteral -Value $CurrentRun.status),
  $(ConvertTo-TestSqliteLiteral -Value $CurrentRun.stage),
  $(ConvertTo-TestSqliteLiteral -Value $CurrentRun.startedAt),
  $(ConvertTo-TestSqliteLiteral -Value $CurrentRun.lastUpdatedAt),
  $(ConvertTo-TestSqliteLiteral -Value $CurrentRun.completedAt),
  $(ConvertTo-TestSqliteLiteral -Value $CurrentRun.currentWorkerId),
  $(ConvertTo-TestSqliteLiteral -Value $CurrentRun.lastAction),
  $(if ($CurrentRun.PSObject.Properties.Name -contains 'processedWorkers') { [int]$CurrentRun.processedWorkers } else { 0 }),
  $(if ($CurrentRun.PSObject.Properties.Name -contains 'totalWorkers') { [int]$CurrentRun.totalWorkers } else { 0 }),
  $(ConvertTo-TestSqliteLiteral -Value $CurrentRun.errorMessage),
  $(ConvertTo-TestSqliteJsonLiteral -Value $CurrentRun)
);
"@
            }

            foreach ($run in @($Runs)) {
                $reportPayload = if ($run.PSObject.Properties.Name -contains 'reportJson' -and $run.reportJson) { $run.reportJson } else { $run }
                $sql += @"
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
) VALUES (
  $(ConvertTo-TestSqliteLiteral -Value $run.runId),
  $(ConvertTo-TestSqliteLiteral -Value $StatePath),
  $(ConvertTo-TestSqliteLiteral -Value $run.path),
  $(ConvertTo-TestSqliteLiteral -Value $(if ($run.PSObject.Properties.Name -contains 'artifactType') { $run.artifactType } else { 'SyncReport' })),
  NULL,
  $(ConvertTo-TestSqliteLiteral -Value $(if ($run.PSObject.Properties.Name -contains 'configPath') { $run.configPath } else { $null })),
  $(ConvertTo-TestSqliteLiteral -Value $(if ($run.PSObject.Properties.Name -contains 'mappingConfigPath') { $run.mappingConfigPath } else { $null })),
  $(ConvertTo-TestSqliteLiteral -Value $run.mode),
  $(if ($run.PSObject.Properties.Name -contains 'dryRun' -and [bool]$run.dryRun) { 1 } else { 0 }),
  $(ConvertTo-TestSqliteLiteral -Value $run.status),
  $(ConvertTo-TestSqliteLiteral -Value $run.startedAt),
  $(ConvertTo-TestSqliteLiteral -Value $run.completedAt),
  $(if ($run.PSObject.Properties.Name -contains 'durationSeconds' -and $null -ne $run.durationSeconds) { [int]$run.durationSeconds } else { 'NULL' }),
  $(if ($run.PSObject.Properties.Name -contains 'reversibleOperations') { [int]$run.reversibleOperations } else { 0 }),
  $(if ($run.PSObject.Properties.Name -contains 'creates') { [int]$run.creates } else { 0 }),
  $(if ($run.PSObject.Properties.Name -contains 'updates') { [int]$run.updates } else { 0 }),
  $(if ($run.PSObject.Properties.Name -contains 'enables') { [int]$run.enables } else { 0 }),
  $(if ($run.PSObject.Properties.Name -contains 'disables') { [int]$run.disables } else { 0 }),
  $(if ($run.PSObject.Properties.Name -contains 'graveyardMoves') { [int]$run.graveyardMoves } else { 0 }),
  $(if ($run.PSObject.Properties.Name -contains 'deletions') { [int]$run.deletions } else { 0 }),
  $(if ($run.PSObject.Properties.Name -contains 'quarantined') { [int]$run.quarantined } else { 0 }),
  $(if ($run.PSObject.Properties.Name -contains 'conflicts') { [int]$run.conflicts } else { 0 }),
  $(if ($run.PSObject.Properties.Name -contains 'guardrailFailures') { [int]$run.guardrailFailures } else { 0 }),
  $(if ($run.PSObject.Properties.Name -contains 'manualReview') { [int]$run.manualReview } else { 0 }),
  $(if ($run.PSObject.Properties.Name -contains 'unchanged') { [int]$run.unchanged } else { 0 }),
  NULL,
  $(ConvertTo-TestSqliteJsonLiteral -Value $reportPayload)
);
"@
            }

            sqlite3 $DatabasePath $sql | Out-Null
        }
    }

    It 'delegates the main sync entry script to Invoke-SyncFactorsRun' {
        Import-Module "$PSScriptRoot/../src/Modules/SyncFactors/Sync.psm1" -Force -DisableNameChecking
        Mock Invoke-SyncFactorsRun { 'report.json' }

        $result = & "$PSScriptRoot/../src/Invoke-SyncFactors.ps1" -ConfigPath 'config.json' -MappingConfigPath 'mapping.json' -Mode Review -DryRun -WorkerId '1001'

        Assert-MockCalled Invoke-SyncFactorsRun -Times 1 -Exactly -ParameterFilter {
            $ConfigPath -eq 'config.json' -and
            $MappingConfigPath -eq 'mapping.json' -and
            $Mode -eq 'Review' -and
            $DryRun -and
            $WorkerId -eq '1001'
        }
        $result | Should -Be 'report.json'
    }

    It 'delegates the rollback entry script to Invoke-SyncFactorsRollback' {
        Import-Module "$PSScriptRoot/../src/Modules/SyncFactors/Rollback.psm1" -Force -DisableNameChecking
        Mock Invoke-SyncFactorsRollback {}

        & "$PSScriptRoot/../scripts/Undo-SyncFactorsRun.ps1" -ReportPath 'report.json' -ConfigPath 'config.json' -DryRun

        Assert-MockCalled Invoke-SyncFactorsRollback -Times 1 -Exactly -ParameterFilter {
            $ReportPath -eq 'report.json' -and
            $ConfigPath -eq 'config.json' -and
            $DryRun
        }
    }

    It 'runs the first-sync review entry script and returns the review summary in json mode' {
        $configPath = Join-Path $TestDrive 'review-config.json'
        $mappingPath = Join-Path $TestDrive 'review-mapping.json'
        $invokeStubPath = Join-Path $TestDrive 'invoke-review-stub.ps1'
        $reportPath = Join-Path $TestDrive 'syncfactors-Review.json'

        (New-StatusConfigContent -StatePath (Join-Path $TestDrive 'state.json') -ReportDirectory (Join-Path $TestDrive 'reports')) | Set-Content -Path $configPath
        '{}' | Set-Content -Path $mappingPath
        @"
{
  "runId": "review-123",
  "mode": "Review",
  "status": "Succeeded",
  "reviewSummary": {
    "existingUsersMatched": 2,
    "existingUsersWithAttributeChanges": 1,
    "proposedCreates": 1,
    "proposedOffboarding": 0,
    "quarantined": 0,
    "conflicts": 0
  }
}
"@ | Set-Content -Path $reportPath
        @"
param(
    [string]`$ConfigPath,
    [string]`$MappingConfigPath,
    [string]`$Mode
)

'$reportPath'
"@ | Set-Content -Path $invokeStubPath

        Mock Join-Path {
            if ($ChildPath -eq 'src/Invoke-SyncFactors.ps1') {
                return $invokeStubPath
            }

            return [System.IO.Path]::Combine($Path, $ChildPath)
        }

        $result = & "$PSScriptRoot/../scripts/Invoke-SyncFactorsFirstSyncReview.ps1" -ConfigPath $configPath -MappingConfigPath $mappingPath -AsJson | ConvertFrom-Json -Depth 10

        $result.mode | Should -Be 'Review'
        $result.status | Should -Be 'Succeeded'
        $result.reviewSummary.existingUsersMatched | Should -Be 2
        $result.reviewSummary.proposedCreates | Should -Be 1
    }

    It 'runs the worker preview entry script and returns the scoped preview in json mode' {
        $configPath = Join-Path $TestDrive 'preview-config.json'
        $mappingPath = Join-Path $TestDrive 'preview-mapping.json'
        $invokeStubPath = Join-Path $TestDrive 'invoke-preview-stub.ps1'
        $reportPath = Join-Path $TestDrive 'syncfactors-Review.json'

        (New-StatusConfigContent -StatePath (Join-Path $TestDrive 'state.json') -ReportDirectory (Join-Path $TestDrive 'reports')) | Set-Content -Path $configPath
        '{}' | Set-Content -Path $mappingPath
        @"
{
  "runId": "preview-123",
  "mode": "Review",
  "status": "Succeeded",
  "artifactType": "WorkerPreview",
  "workerScope": {
    "identityField": "personIdExternal",
    "workerId": "1001"
  },
  "reviewSummary": {
    "existingUsersMatched": 1,
    "existingUsersWithAttributeChanges": 1,
    "proposedCreates": 0,
    "proposedOffboarding": 0,
    "quarantined": 0,
    "conflicts": 0
  },
  "operations": [
    {
      "workerId": "1001",
      "operationType": "UpdateAttributes",
      "before": {
        "GivenName": "OldJamie"
      },
      "after": {
        "GivenName": "Jamie"
      }
    }
  ],
  "updates": [
    {
      "workerId": "1001",
      "samAccountName": "jdoe",
      "reviewCategory": "ExistingUserChanges",
      "reviewCaseType": "RehireCase",
      "operatorActionSummary": "Confirm how this rehire should reuse or restore the existing AD identity.",
      "operatorActions": [
        {
          "code": "ConfirmAccountReuse",
          "label": "Confirm account reuse",
          "description": "Review the prior AD account and confirm whether it should be reused."
        }
      ],
      "targetOu": "OU=Employees,DC=example,DC=com",
      "currentDistinguishedName": "CN=Jamie Doe,OU=Employees,DC=example,DC=com",
      "currentEnabled": true,
      "changedAttributeDetails": [
        {
          "sourceField": "firstName",
          "targetAttribute": "GivenName",
          "transform": "Trim",
          "currentAdValue": "OldJamie",
          "proposedValue": "Jamie"
        }
      ]
    }
  ],
  "creates": [],
  "enables": [],
  "disables": [],
  "graveyardMoves": [],
  "deletions": [],
  "quarantined": [],
  "conflicts": [],
  "guardrailFailures": [],
  "manualReview": [],
  "unchanged": []
}
"@ | Set-Content -Path $reportPath
        @"
param(
    [string]`$ConfigPath,
    [string]`$MappingConfigPath,
    [string]`$Mode,
    [string]`$WorkerId,
    [string]`$PreviewMode
)

'$reportPath'
"@ | Set-Content -Path $invokeStubPath

        Mock Join-Path {
            if ($ChildPath -eq 'src/Invoke-SyncFactors.ps1') {
                return $invokeStubPath
            }

            return [System.IO.Path]::Combine($Path, $ChildPath)
        }

        $result = & "$PSScriptRoot/../scripts/Invoke-SyncFactorsWorkerPreview.ps1" -ConfigPath $configPath -MappingConfigPath $mappingPath -WorkerId '1001' -PreviewMode Full -AsJson | ConvertFrom-Json -Depth 20

        $result.artifactType | Should -Be 'WorkerPreview'
        $result.successFactorsAuth | Should -Be 'oauth (body client auth)'
        $result.previewMode | Should -Be 'full'
        $result.workerScope.workerId | Should -Be '1001'
        $result.preview.samAccountName | Should -Be 'jdoe'
        $result.preview.reviewCategory | Should -Be 'ExistingUserChanges'
        $result.preview.reviewCaseType | Should -Be 'RehireCase'
        (@($result.preview.operatorActions) | ConvertTo-Json -Depth 10) | Should -Match 'Confirm account reuse'
        $result.changedAttributes[0].targetAttribute | Should -Be 'GivenName'
        $result.operations[0].operationType | Should -Be 'UpdateAttributes'
    }

    It 'uses identity-only fallback for minimal worker preview when previewQuery is not configured' {
        $configPath = Join-Path $TestDrive 'preview-minimal-config.json'
        $mappingPath = Join-Path $TestDrive 'preview-minimal-mapping.json'

        (New-StatusConfigContent -StatePath (Join-Path $TestDrive 'state-minimal.json') -ReportDirectory (Join-Path $TestDrive 'reports-minimal')) | Set-Content -Path $configPath
        '{}' | Set-Content -Path $mappingPath
        Import-Module "$PSScriptRoot/../src/Modules/SyncFactors/SuccessFactors.psm1" -Force -DisableNameChecking
        Mock Get-SfWorkerById {
            [pscustomobject]@{
                personIdExternal = '1001'
            }
        }

        $result = & "$PSScriptRoot/../scripts/Invoke-SyncFactorsWorkerPreview.ps1" -ConfigPath $configPath -MappingConfigPath $mappingPath -WorkerId '1001' -PreviewMode Minimal -AsJson | ConvertFrom-Json -Depth 20

        $result.artifactType | Should -Be 'WorkerFetchPreview'
        $result.previewMode | Should -Be 'minimal'
        $result.rawPropertyNames | Should -Be @('personIdExternal')
        $result.rawWorker.personIdExternal | Should -Be '1001'
    }

    It 'returns preflight details from the preflight script in json mode' {
        Import-Module "$PSScriptRoot/../src/Modules/SyncFactors/Sync.psm1" -Force -DisableNameChecking
        Mock Test-SyncFactorsPreflight {
            [pscustomobject]@{
                success = $true
                configPath = 'config.json'
                mappingConfigPath = 'mapping.json'
                identityField = 'personIdExternal'
                identityAttribute = 'employeeID'
                statePath = 'state.json'
                stateDirectoryExists = $true
                reportDirectory = 'reports'
                reportDirectoryExists = $true
                mappingCount = 3
            }
        }

        $result = & "$PSScriptRoot/../scripts/Invoke-SyncFactorsPreflight.ps1" -ConfigPath 'config.json' -MappingConfigPath 'mapping.json' -AsJson | ConvertFrom-Json

        $result.success | Should -BeTrue
        $result.mappingCount | Should -Be 3
    }

    It 'writes schema export files and returns their paths in json mode' {
        $configPath = Join-Path $TestDrive 'schema-config.json'
        $outputDirectory = Join-Path $TestDrive 'schema-output'
        '{}' | Set-Content -Path $configPath

        Import-Module "$PSScriptRoot/../src/Modules/SyncFactors/Config.psm1" -Force -DisableNameChecking
        Import-Module "$PSScriptRoot/../src/Modules/SyncFactors/SuccessFactors.psm1" -Force -DisableNameChecking
        Mock Get-SyncFactorsConfig {
            [pscustomobject]@{
                reporting = [pscustomobject]@{
                    outputDirectory = (Join-Path $TestDrive 'reports')
                }
                successFactors = [pscustomobject]@{
                    baseUrl = 'https://tenant.example.com/odata/v2'
                }
            }
        }
        Mock Get-SfODataSchemaExport {
            [pscustomobject]@{
                artifactType = 'SchemaExport'
                exportedAt = '2026-03-20T12:00:00.0000000Z'
                metadataUri = 'https://tenant.example.com/odata/v2/$metadata'
                entitySetName = 'PerPerson'
                entityTypeName = 'PerPerson'
                configuredSelect = @('personIdExternal')
                configuredExpand = @('employmentNav')
                pathValidations = @(
                    [pscustomobject]@{
                        path = 'personIdExternal'
                        pathType = 'select'
                        isValid = $true
                        failureReason = $null
                        failureSegment = $null
                    }
                )
                entities = @(
                    [pscustomobject]@{
                        name = 'PerPerson'
                        exists = $true
                        keyProperties = @('personIdExternal')
                        propertyCount = 1
                        properties = @('personIdExternal')
                        navigationProperties = @()
                    }
                )
                metadataXml = '<edmx:Edmx />'
            }
        }

        $result = & "$PSScriptRoot/../scripts/Invoke-SyncFactorsSchemaExport.ps1" -ConfigPath $configPath -OutputDirectory $outputDirectory -AsJson | ConvertFrom-Json -Depth 20

        $result.artifactType | Should -Be 'SchemaExport'
        $result.entitySetName | Should -Be 'PerPerson'
        (Test-Path -Path $result.metadataPath -PathType Leaf) | Should -BeTrue
        (Test-Path -Path $result.summaryPath -PathType Leaf) | Should -BeTrue
        ((Get-Content -Path $result.metadataPath -Raw).Trim()) | Should -Be '<edmx:Edmx />'
    }

    It 'runs the worker sync entry script and returns the scoped run in json mode' {
        $configPath = Join-Path $TestDrive 'worker-sync-config.json'
        $mappingPath = Join-Path $TestDrive 'worker-sync-mapping.json'
        $invokeStubPath = Join-Path $TestDrive 'invoke-worker-sync-stub.ps1'
        $reportPath = Join-Path $TestDrive 'syncfactors-Full.json'

        (New-StatusConfigContent -StatePath (Join-Path $TestDrive 'state.json') -ReportDirectory (Join-Path $TestDrive 'reports')) | Set-Content -Path $configPath
        '{}' | Set-Content -Path $mappingPath
        @"
{
  "runId": "worker-sync-123",
  "mode": "Full",
  "status": "Succeeded",
  "artifactType": "WorkerSync",
  "workerScope": {
    "identityField": "personIdExternal",
    "workerId": "1001"
  }
}
"@ | Set-Content -Path $reportPath
        @"
param(
    [string]`$ConfigPath,
    [string]`$MappingConfigPath,
    [string]`$Mode,
    [string]`$WorkerId
)

'$reportPath'
"@ | Set-Content -Path $invokeStubPath

        Mock Join-Path {
            if ($ChildPath -eq 'src/Invoke-SyncFactors.ps1') {
                return $invokeStubPath
            }

            return [System.IO.Path]::Combine($Path, $ChildPath)
        }

        $result = & "$PSScriptRoot/../scripts/Invoke-SyncFactorsWorkerSync.ps1" -ConfigPath $configPath -MappingConfigPath $mappingPath -WorkerId '1001' -AsJson | ConvertFrom-Json -Depth 20

        $result.status | Should -Be 'Succeeded'
        $result.mode | Should -Be 'Full'
        $result.artifactType | Should -Be 'WorkerSync'
        $result.workerScope.workerId | Should -Be '1001'
    }

    It 'requires three confirmations before deleting managed OU users and resetting sync state' {
        $configPath = Join-Path $TestDrive 'fresh-reset-config.json'
        $statePath = Join-Path $TestDrive 'fresh-reset-state.json'
        $reportDirectory = Join-Path $TestDrive 'fresh-reset-reports'

        (New-StatusConfigContent -StatePath $statePath -ReportDirectory $reportDirectory) | Set-Content -Path $configPath

        Import-Module "$PSScriptRoot/../src/Modules/SyncFactors/ActiveDirectorySync.psm1" -Force -DisableNameChecking
        Import-Module "$PSScriptRoot/../src/Modules/SyncFactors/State.psm1" -Force -DisableNameChecking

        $global:RemovedUsers = @()
        $global:SavedResetState = $null
        $global:SavedResetStatePath = $null

        Mock Get-SyncFactorsManagedOus {
            @(
                'OU=Employees,DC=example,DC=com',
                'OU=Graveyard,DC=example,DC=com'
            )
        }
        Mock Get-SyncFactorsUsersInOrganizationalUnits {
            @(
                [pscustomobject]@{
                    SamAccountName = 'jdoe'
                    DistinguishedName = 'CN=Jamie Doe,OU=Employees,DC=example,DC=com'
                },
                [pscustomobject]@{
                    SamAccountName = 'adoe'
                    DistinguishedName = 'CN=Alex Doe,OU=Graveyard,DC=example,DC=com'
                }
            )
        }
        Mock Remove-SyncFactorsUser {
            param($Config, $User)
            $global:RemovedUsers += $User.SamAccountName
        }
        Mock Save-SyncFactorsState {
            param($State, $Path)
            $global:SavedResetState = $State
            $global:SavedResetStatePath = $Path
        }
        $responses = [System.Collections.Generic.Queue[string]]::new()
        $responses.Enqueue('DELETE')
        $responses.Enqueue('2')
        $responses.Enqueue('DELETE ALL SYNCED OU USERS')
        Mock Read-Host { $responses.Dequeue() }

        & "$PSScriptRoot/../scripts/Invoke-SyncFactorsFreshSyncReset.ps1" -ConfigPath $configPath | Out-Null

        $global:RemovedUsers | Should -Be @('jdoe', 'adoe')
        $global:SavedResetStatePath | Should -Be $statePath
        $global:SavedResetState.checkpoint | Should -Be $null
        @($global:SavedResetState.workers.Keys).Count | Should -Be 0
        $resetLogs = @(Get-ChildItem -Path $reportDirectory -Filter 'syncfactors-fresh-reset-*.log' -File)
        $resetLogs.Count | Should -Be 1
        $resetLogContent = Get-Content -Path $resetLogs[0].FullName -Raw
        $resetLogContent | Should -Match 'Discovered AD user objects: 2'
        $resetLogContent | Should -Match 'Preview report:'
        $resetLogContent | Should -Match 'Deleting user: samAccountName=jdoe'
        $resetLogContent | Should -Match 'Deleted user: samAccountName=adoe'
        $resetLogContent | Should -Match 'Reset sync state:'
        $previewReports = @(Get-ChildItem -Path $reportDirectory -Filter 'syncfactors-ResetPreview-*.json' -File)
        $previewReports.Count | Should -Be 1
        $previewReport = Get-Content -Path $previewReports[0].FullName -Raw | ConvertFrom-Json -Depth 20
        $previewReport.artifactType | Should -Be 'FreshSyncResetPreview'
        $previewReport.status | Should -Be 'Preview'
        $previewReport.deletions.Count | Should -Be 2
        $previewReport.operations.Count | Should -Be 2
        Assert-MockCalled Read-Host -Times 3 -Exactly
        Assert-MockCalled Remove-SyncFactorsUser -Times 2 -Exactly
        Assert-MockCalled Save-SyncFactorsState -Times 1 -Exactly
    }

    It 'writes the fresh reset log to an explicit requested log path' {
        $configPath = Join-Path $TestDrive 'fresh-reset-config-custom-log.json'
        $statePath = Join-Path $TestDrive 'fresh-reset-state-custom-log.json'
        $reportDirectory = Join-Path $TestDrive 'fresh-reset-custom-log-reports'
        $requestedLogPath = Join-Path $TestDrive 'custom-logs/fresh-reset.log'

        (New-StatusConfigContent -StatePath $statePath -ReportDirectory $reportDirectory) | Set-Content -Path $configPath

        Import-Module "$PSScriptRoot/../src/Modules/SyncFactors/ActiveDirectorySync.psm1" -Force -DisableNameChecking
        Import-Module "$PSScriptRoot/../src/Modules/SyncFactors/State.psm1" -Force -DisableNameChecking

        Mock Get-SyncFactorsManagedOus { @('OU=Employees,DC=example,DC=com') }
        Mock Get-SyncFactorsUsersInOrganizationalUnits {
            @(
                [pscustomobject]@{
                    SamAccountName = 'jdoe'
                    DistinguishedName = 'CN=Jamie Doe,OU=Employees,DC=example,DC=com'
                }
            )
        }
        Mock Remove-SyncFactorsUser {}
        Mock Save-SyncFactorsState {}
        $responses = [System.Collections.Generic.Queue[string]]::new()
        $responses.Enqueue('DELETE')
        $responses.Enqueue('1')
        $responses.Enqueue('DELETE ALL SYNCED OU USERS')
        Mock Read-Host { $responses.Dequeue() }

        & "$PSScriptRoot/../scripts/Invoke-SyncFactorsFreshSyncReset.ps1" -ConfigPath $configPath -LogPath $requestedLogPath | Out-Null

        Test-Path -Path $requestedLogPath -PathType Leaf | Should -BeTrue
        (Get-Content -Path $requestedLogPath -Raw) | Should -Match 'SuccessFactors Fresh Sync Reset'
    }

    It 'prompts for placeholder secret values and stores them in process environment variables' {
        $configPath = Join-Path $TestDrive 'interactive-config.json'
        $mappingPath = Join-Path $TestDrive 'interactive-mapping.json'
        $invokeStubPath = Join-Path $TestDrive 'invoke-sync-stub.ps1'

        (New-InteractiveConfigContent -ClientId 'companyid' -ClientSecret 'replace-me' -DefaultPassword 'replace-this-password' -AdServer 'replace-me' -AdUsername 'replace-this-username' -AdBindPassword 'replace-this-password') | Set-Content -Path $configPath
        '{}' | Set-Content -Path $mappingPath
        @'
param(
    [string]$ConfigPath,
    [string]$MappingConfigPath,
    [string]$Mode,
    [switch]$DryRun
)

[pscustomobject]@{
    configPath = $ConfigPath
    mappingConfigPath = $MappingConfigPath
    mode = $Mode
    dryRun = [bool]$DryRun
    env = @{
        clientId = [System.Environment]::GetEnvironmentVariable('TEST_SF_CLIENT_ID', 'Process')
        clientSecret = [System.Environment]::GetEnvironmentVariable('TEST_SF_CLIENT_SECRET', 'Process')
        defaultPassword = [System.Environment]::GetEnvironmentVariable('TEST_AD_PASSWORD', 'Process')
        adServer = [System.Environment]::GetEnvironmentVariable('TEST_AD_SERVER', 'Process')
        adUsername = [System.Environment]::GetEnvironmentVariable('TEST_AD_USERNAME', 'Process')
        adBindPassword = [System.Environment]::GetEnvironmentVariable('TEST_AD_BIND_PASSWORD', 'Process')
    }
} | ConvertTo-Json -Depth 10 -Compress
'@ | Set-Content -Path $invokeStubPath

        foreach ($name in @('TEST_SF_CLIENT_ID', 'TEST_SF_CLIENT_SECRET', 'TEST_AD_PASSWORD', 'TEST_AD_SERVER', 'TEST_AD_USERNAME', 'TEST_AD_BIND_PASSWORD')) {
            [System.Environment]::SetEnvironmentVariable($name, $null, 'Process')
        }

        Mock Join-Path {
            $invokeStubPath
        } -ParameterFilter { $ChildPath -eq 'src/Invoke-SyncFactors.ps1' }

        $promptValues = @{
            'Enter the SuccessFactors OAuth client id' = 'interactive-client-id'
            'Enter the SuccessFactors OAuth client secret' = 'interactive-client-secret'
            'Enter the default AD password for newly created users' = 'interactive-default-password'
            'Enter the AD server or domain controller hostname' = 'dc01.example.com'
            'Enter the AD bind username' = 'EXAMPLE\svc_sfadsync'
            'Enter the AD bind password' = 'interactive-bind-password'
        }

        Mock Read-Host {
            if ($AsSecureString) {
                return ConvertTo-SecureString -String $promptValues[$Prompt] -AsPlainText -Force
            }

            return $promptValues[$Prompt]
        }

        try {
            function global:Get-CimInstance {
                [pscustomobject]@{ PartOfDomain = $false }
            }

            $result = & "$PSScriptRoot/../scripts/Invoke-SyncFactorsInteractive.ps1" -ConfigPath $configPath -MappingConfigPath $mappingPath -Mode Full -DryRun | ConvertFrom-Json -Depth 10

            $result.mode | Should -Be 'Full'
            $result.dryRun | Should -BeTrue
            $result.env.clientId | Should -Be 'interactive-client-id'
            $result.env.clientSecret | Should -Be 'interactive-client-secret'
            $result.env.defaultPassword | Should -Be 'interactive-default-password'
            $result.env.adServer | Should -Be 'dc01.example.com'
            $result.env.adUsername | Should -Be 'EXAMPLE\svc_sfadsync'
            $result.env.adBindPassword | Should -Be 'interactive-bind-password'
            Assert-MockCalled Read-Host -Times 6 -Exactly
        } finally {
            Remove-Item Function:\Get-CimInstance -ErrorAction SilentlyContinue
            foreach ($name in @('TEST_SF_CLIENT_ID', 'TEST_SF_CLIENT_SECRET', 'TEST_AD_PASSWORD', 'TEST_AD_SERVER', 'TEST_AD_USERNAME', 'TEST_AD_BIND_PASSWORD')) {
                [System.Environment]::SetEnvironmentVariable($name, $null, 'Process')
            }
        }
    }

    It 'skips prompting when environment-backed secrets are already populated' {
        $configPath = Join-Path $TestDrive 'interactive-config-existing-env.json'
        $mappingPath = Join-Path $TestDrive 'interactive-mapping-existing-env.json'
        $invokeStubPath = Join-Path $TestDrive 'invoke-sync-stub-existing-env.ps1'

        (New-InteractiveConfigContent -ClientId 'replace-me' -ClientSecret 'replace-this-secret' -DefaultPassword 'replace-this-password') | Set-Content -Path $configPath
        '{}' | Set-Content -Path $mappingPath
        @'
param(
    [string]$ConfigPath,
    [string]$MappingConfigPath,
    [string]$Mode,
    [switch]$DryRun
)

[pscustomobject]@{
    configPath = $ConfigPath
    mappingConfigPath = $MappingConfigPath
    mode = $Mode
    dryRun = [bool]$DryRun
    env = @{
        clientId = [System.Environment]::GetEnvironmentVariable('TEST_SF_CLIENT_ID', 'Process')
        clientSecret = [System.Environment]::GetEnvironmentVariable('TEST_SF_CLIENT_SECRET', 'Process')
        defaultPassword = [System.Environment]::GetEnvironmentVariable('TEST_AD_PASSWORD', 'Process')
        adServer = [System.Environment]::GetEnvironmentVariable('TEST_AD_SERVER', 'Process')
        adUsername = [System.Environment]::GetEnvironmentVariable('TEST_AD_USERNAME', 'Process')
        adBindPassword = [System.Environment]::GetEnvironmentVariable('TEST_AD_BIND_PASSWORD', 'Process')
    }
} | ConvertTo-Json -Depth 10 -Compress
'@ | Set-Content -Path $invokeStubPath

        [System.Environment]::SetEnvironmentVariable('TEST_SF_CLIENT_ID', 'env-client-id', 'Process')
        [System.Environment]::SetEnvironmentVariable('TEST_SF_CLIENT_SECRET', 'env-client-secret', 'Process')
        [System.Environment]::SetEnvironmentVariable('TEST_AD_PASSWORD', 'env-default-password', 'Process')
        [System.Environment]::SetEnvironmentVariable('TEST_AD_SERVER', $null, 'Process')
        [System.Environment]::SetEnvironmentVariable('TEST_AD_USERNAME', $null, 'Process')
        [System.Environment]::SetEnvironmentVariable('TEST_AD_BIND_PASSWORD', $null, 'Process')

        Mock Join-Path {
            $invokeStubPath
        } -ParameterFilter { $ChildPath -eq 'src/Invoke-SyncFactors.ps1' }
        Mock Read-Host { throw 'Read-Host should not be called when required process environment variables are already set.' }

        try {
            function global:Get-CimInstance {
                [pscustomobject]@{ PartOfDomain = $true }
            }

            $result = & "$PSScriptRoot/../scripts/Invoke-SyncFactorsInteractive.ps1" -ConfigPath $configPath -MappingConfigPath $mappingPath | ConvertFrom-Json -Depth 10

            $result.mode | Should -Be 'Delta'
            $result.dryRun | Should -BeFalse
            $result.env.clientId | Should -Be 'env-client-id'
            $result.env.clientSecret | Should -Be 'env-client-secret'
            $result.env.defaultPassword | Should -Be 'env-default-password'
            $result.env.adServer | Should -BeNullOrEmpty
            $result.env.adUsername | Should -BeNullOrEmpty
            $result.env.adBindPassword | Should -BeNullOrEmpty
            Assert-MockCalled Read-Host -Times 0 -Exactly
        } finally {
            Remove-Item Function:\Get-CimInstance -ErrorAction SilentlyContinue
            foreach ($name in @('TEST_SF_CLIENT_ID', 'TEST_SF_CLIENT_SECRET', 'TEST_AD_PASSWORD', 'TEST_AD_SERVER', 'TEST_AD_USERNAME', 'TEST_AD_BIND_PASSWORD')) {
                [System.Environment]::SetEnvironmentVariable($name, $null, 'Process')
            }
        }
    }

    It 're-prompts until blank interactive values are replaced with non-empty values' {
        $configPath = Join-Path $TestDrive 'interactive-config-retry.json'
        $mappingPath = Join-Path $TestDrive 'interactive-mapping-retry.json'
        $invokeStubPath = Join-Path $TestDrive 'invoke-sync-stub-retry.ps1'

        (New-InteractiveConfigContent -ClientId 'replace-me' -ClientSecret 'replace-this-secret' -DefaultPassword 'replace-this-password') | Set-Content -Path $configPath
        '{}' | Set-Content -Path $mappingPath
        @'
param(
    [string]$ConfigPath,
    [string]$MappingConfigPath,
    [string]$Mode,
    [switch]$DryRun
)

[pscustomobject]@{
    env = @{
        clientId = [System.Environment]::GetEnvironmentVariable('TEST_SF_CLIENT_ID', 'Process')
        clientSecret = [System.Environment]::GetEnvironmentVariable('TEST_SF_CLIENT_SECRET', 'Process')
        defaultPassword = [System.Environment]::GetEnvironmentVariable('TEST_AD_PASSWORD', 'Process')
    }
} | ConvertTo-Json -Depth 10 -Compress
'@ | Set-Content -Path $invokeStubPath

        foreach ($name in @('TEST_SF_CLIENT_ID', 'TEST_SF_CLIENT_SECRET', 'TEST_AD_PASSWORD')) {
            [System.Environment]::SetEnvironmentVariable($name, $null, 'Process')
        }

        Mock Join-Path {
            $invokeStubPath
        } -ParameterFilter { $ChildPath -eq 'src/Invoke-SyncFactors.ps1' }

        $promptResponses = @{
            'Enter the SuccessFactors OAuth client id' = @('', 'interactive-client-id')
            'Enter the SuccessFactors OAuth client secret' = @(' ', 'interactive-client-secret')
            'Enter the default AD password for newly created users' = @(' ', 'interactive-default-password')
        }
        $promptIndexes = @{}

        Mock Read-Host {
            if (-not $promptIndexes.ContainsKey($Prompt)) {
                $promptIndexes[$Prompt] = 0
            }

            $response = $promptResponses[$Prompt][$promptIndexes[$Prompt]]
            $promptIndexes[$Prompt] += 1

            if ($AsSecureString) {
                return ConvertTo-SecureString -String $response -AsPlainText -Force
            }

            return $response
        }

        try {
            function global:Get-CimInstance {
                [pscustomobject]@{ PartOfDomain = $true }
            }

            $result = & "$PSScriptRoot/../scripts/Invoke-SyncFactorsInteractive.ps1" -ConfigPath $configPath -MappingConfigPath $mappingPath | ConvertFrom-Json -Depth 10

            $result.env.clientId | Should -Be 'interactive-client-id'
            $result.env.clientSecret | Should -Be 'interactive-client-secret'
            $result.env.defaultPassword | Should -Be 'interactive-default-password'
            Assert-MockCalled Read-Host -Times 6 -Exactly
        } finally {
            Remove-Item Function:\Get-CimInstance -ErrorAction SilentlyContinue
            foreach ($name in @('TEST_SF_CLIENT_ID', 'TEST_SF_CLIENT_SECRET', 'TEST_AD_PASSWORD')) {
                [System.Environment]::SetEnvironmentVariable($name, $null, 'Process')
            }
        }
    }

    It 'returns sync status from the SQLite operational store in json mode' {
        $configPath = Join-Path $TestDrive 'status-config.json'
        $statePath = Join-Path $TestDrive 'state.json'
        $reportDir = Join-Path $TestDrive 'reports'
        $runtimeStatusPath = Join-Path $TestDrive 'runtime-status.json'
        $sqlitePath = Join-Path $TestDrive 'syncfactors.db'

        New-Item -Path $reportDir -ItemType Directory -Force | Out-Null
        (New-StatusConfigContent -StatePath $statePath -ReportDirectory $reportDir) | Set-Content -Path $configPath
        Initialize-StatusSqliteFixture `
            -StatePath $statePath `
            -DatabasePath $sqlitePath `
            -Checkpoint '2026-03-12T21:00:00' `
            -Workers @(
                [pscustomobject]@{ workerId = '1001'; suppressed = $true; deleteAfter = (Get-Date).AddDays(-1).ToString('o') },
                [pscustomobject]@{ workerId = '1002'; suppressed = $false }
            ) `
            -CurrentRun ([pscustomobject]@{
                runId = 'run-active'
                status = 'InProgress'
                mode = 'Delta'
                dryRun = $true
                stage = 'ProcessingWorkers'
                startedAt = '2026-03-12T21:40:00'
                lastUpdatedAt = '2026-03-12T21:41:00'
                completedAt = $null
                currentWorkerId = '1002'
                lastAction = 'Updated attributes for worker 1002.'
                processedWorkers = 3
                totalWorkers = 5
                creates = 1
                updates = 1
                enables = 0
                disables = 0
                graveyardMoves = 0
                deletions = 0
                quarantined = 0
                conflicts = 0
                guardrailFailures = 0
                manualReview = 0
                unchanged = 1
                errorMessage = $null
            }) `
            -Runs @(
                [pscustomobject]@{
                    runId = 'run-123'
                    path = (Join-Path $reportDir 'syncfactors-Delta-20260312-220000.json')
                    artifactType = 'SyncReport'
                    mode = 'Delta'
                    dryRun = $false
                    status = 'Succeeded'
                    startedAt = '2026-03-12T21:30:00'
                    completedAt = '2026-03-12T21:35:00'
                    durationSeconds = 300
                    reversibleOperations = 1
                    creates = 1
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
                    reportJson = @{ runId = 'run-123'; operations = @(@{ operationType = 'CreateUser' }); creates = @(@{}); updates = @(); enables = @(); disables = @(); graveyardMoves = @(); deletions = @(); quarantined = @(); conflicts = @(); guardrailFailures = @(); manualReview = @(); unchanged = @() }
                },
                [pscustomobject]@{
                    runId = 'run-122'
                    path = (Join-Path $reportDir 'syncfactors-Full-20260312-210000.json')
                    artifactType = 'SyncReport'
                    mode = 'Full'
                    dryRun = $true
                    status = 'Failed'
                    startedAt = '2026-03-12T20:30:00'
                    completedAt = '2026-03-12T20:40:00'
                    durationSeconds = 600
                    reversibleOperations = 0
                    creates = 0
                    updates = 0
                    enables = 0
                    disables = 1
                    graveyardMoves = 0
                    deletions = 0
                    quarantined = 0
                    conflicts = 1
                    guardrailFailures = 1
                    manualReview = 0
                    unchanged = 0
                    reportJson = @{ runId = 'run-122'; operations = @(); creates = @(); updates = @(); enables = @(); disables = @(@{}); graveyardMoves = @(); deletions = @(); quarantined = @(); conflicts = @(@{}); guardrailFailures = @(@{}); manualReview = @(); unchanged = @() }
                }
            )

        $result = & "$PSScriptRoot/../scripts/Get-SyncFactorsStatus.ps1" -ConfigPath $configPath -AsJson | ConvertFrom-Json -Depth 10

        $result.totalTrackedWorkers | Should -Be 2
        $result.suppressedWorkers | Should -Be 1
        $result.pendingDeletionWorkers | Should -Be 1
        $result.latestReport.status | Should -Be 'Succeeded'
        $result.latestReport.creates | Should -Be 1
        $result.latestRun.mode | Should -Be 'Delta'
        $result.currentRun.status | Should -Be 'InProgress'
        $result.currentRun.stage | Should -Be 'ProcessingWorkers'
        $result.currentRun.currentWorkerId | Should -Be '1002'
        @($result.recentRuns).Count | Should -Be 2
        $result.recentRuns[1].guardrailFailures | Should -Be 1
        $result.paths.runtimeStatusPath | Should -Be $runtimeStatusPath
    }

    It 'returns zeroed latest report details when no SQLite runs exist' {
        $configPath = Join-Path $TestDrive 'status-config-empty.json'
        $emptyRoot = Join-Path $TestDrive 'empty-status'
        $statePath = Join-Path $emptyRoot 'state-empty.json'
        $reportDir = Join-Path $emptyRoot 'reports-empty'
        $sqlitePath = Join-Path $emptyRoot 'syncfactors.db'

        New-Item -Path $emptyRoot -ItemType Directory -Force | Out-Null
        New-Item -Path $reportDir -ItemType Directory -Force | Out-Null
        (New-StatusConfigContent -StatePath $statePath -ReportDirectory $reportDir) | Set-Content -Path $configPath
        Initialize-StatusSqliteFixture -StatePath $statePath -DatabasePath $sqlitePath -Checkpoint $null -Workers @() -Runs @()

        $result = & "$PSScriptRoot/../scripts/Get-SyncFactorsStatus.ps1" -ConfigPath $configPath -AsJson | ConvertFrom-Json -Depth 10

        $result.totalTrackedWorkers | Should -Be 0
        $result.latestReport.path | Should -Be $null
        $result.latestReport.creates | Should -Be 0
        $result.latestReport.status | Should -Be $null
        $result.currentRun.status | Should -Be 'Idle'
        @($result.recentRuns).Count | Should -Be 0
    }

    It 'throws when the SQLite operational store is missing' {
        $root = Join-Path $TestDrive 'missing-sqlite'
        $configPath = Join-Path $root 'status-config-missing-sqlite.json'
        $statePath = Join-Path $root 'state-missing-sqlite.json'
        $reportDir = Join-Path $root 'reports-missing-sqlite'

        New-Item -Path $root -ItemType Directory -Force | Out-Null
        New-Item -Path $reportDir -ItemType Directory -Force | Out-Null
        (New-StatusConfigContent -StatePath $statePath -ReportDirectory $reportDir) | Set-Content -Path $configPath

        { & "$PSScriptRoot/../scripts/Get-SyncFactorsStatus.ps1" -ConfigPath $configPath -AsJson | Out-Null } | Should -Throw -ExpectedMessage '*SQLite operational store is required*'
    }

    It 'renders the monitor view in text mode with current and recent run details' {
        $configPath = Join-Path $TestDrive 'monitor-config.json'
        $statePath = Join-Path $TestDrive 'monitor-state.json'
        $reportDir = Join-Path $TestDrive 'monitor-reports'
        $runtimeStatusPath = Join-Path $TestDrive 'runtime-status.json'
        $sqlitePath = Join-Path $TestDrive 'syncfactors.db'

        New-Item -Path $reportDir -ItemType Directory -Force | Out-Null
        (New-StatusConfigContent -StatePath $statePath -ReportDirectory $reportDir) | Set-Content -Path $configPath
        Initialize-StatusSqliteFixture `
            -StatePath $statePath `
            -DatabasePath $sqlitePath `
            -Checkpoint '2026-03-12T21:00:00' `
            -Workers @() `
            -CurrentRun ([pscustomobject]@{
                runId = 'run-active'
                status = 'InProgress'
                mode = 'Delta'
                dryRun = $false
                stage = 'ProcessingWorkers'
                startedAt = '2026-03-12T21:40:00'
                lastUpdatedAt = '2026-03-12T21:41:00'
                completedAt = $null
                currentWorkerId = '1002'
                lastAction = 'Updated attributes for worker 1002.'
                processedWorkers = 3
                totalWorkers = 5
                creates = 1
                updates = 1
                enables = 0
                disables = 0
                graveyardMoves = 0
                deletions = 0
                quarantined = 0
                conflicts = 0
                guardrailFailures = 0
                manualReview = 0
                unchanged = 1
                errorMessage = $null
            }) `
            -Runs @(
                [pscustomobject]@{
                    runId = 'run-123'
                    path = (Join-Path $reportDir 'syncfactors-Delta-20260312-220000.json')
                    artifactType = 'SyncReport'
                    mode = 'Delta'
                    dryRun = $false
                    status = 'Succeeded'
                    startedAt = '2026-03-12T21:30:00'
                    completedAt = '2026-03-12T21:35:00'
                    durationSeconds = 300
                    reversibleOperations = 1
                    creates = 1
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
                    reportJson = @{ runId = 'run-123'; operations = @(@{ operationType = 'CreateUser' }); creates = @(@{}); updates = @(); enables = @(); disables = @(); graveyardMoves = @(); deletions = @(); quarantined = @(); conflicts = @(); guardrailFailures = @(); manualReview = @(); unchanged = @() }
                }
            )
        '{bad json' | Set-Content -Path $runtimeStatusPath

        $result = & "$PSScriptRoot/../scripts/Watch-SyncFactorsMonitor.ps1" -ConfigPath $configPath -RunOnce -AsText

        $result | Should -Match 'SuccessFactors AD Sync Monitor'
        $result | Should -Match 'SuccessFactors auth: oauth \(body client auth\)'
        $result | Should -Match 'Stage: ProcessingWorkers'
        $result | Should -Match 'Updated attributes for worker 1002'
        $result | Should -Match 'Succeeded'
    }

    It 'renders an error banner when the monitor hits corrupt SQLite runtime status data' {
        $configPath = Join-Path $TestDrive 'monitor-error-config.json'
        $statePath = Join-Path $TestDrive 'monitor-error-state.json'
        $reportDir = Join-Path $TestDrive 'monitor-error-reports'
        $sqlitePath = Join-Path $TestDrive 'syncfactors.db'

        New-Item -Path $reportDir -ItemType Directory -Force | Out-Null
        (New-StatusConfigContent -StatePath $statePath -ReportDirectory $reportDir) | Set-Content -Path $configPath
        Initialize-StatusSqliteFixture `
            -StatePath $statePath `
            -DatabasePath $sqlitePath `
            -Checkpoint $null `
            -Workers @() `
            -CurrentRun ([pscustomobject]@{
                runId = 'run-bad'
                status = 'InProgress'
                mode = 'Delta'
                dryRun = $false
                stage = 'ProcessingWorkers'
                startedAt = '2026-03-12T21:40:00'
                lastUpdatedAt = '2026-03-12T21:41:00'
                completedAt = $null
                currentWorkerId = '1002'
                lastAction = 'Broken snapshot'
                processedWorkers = 1
                totalWorkers = 5
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
                errorMessage = $null
            }) `
            -Runs @()
        sqlite3 $sqlitePath "UPDATE runtime_status SET snapshot_json = '{bad json' WHERE state_path = '$($statePath.Replace("'", "''"))';" | Out-Null

        $result = & "$PSScriptRoot/../scripts/Watch-SyncFactorsMonitor.ps1" -ConfigPath $configPath -RunOnce -AsText

        $result | Should -Match 'Monitor error'
    }

    It 'installs syncfactors shims that point at the dashboard with explicit config paths' {
        $projectRoot = Join-Path $TestDrive 'install-project'
        $configDirectory = Join-Path $projectRoot 'config'
        $scriptsDirectory = Join-Path $projectRoot 'scripts'
        $installDirectory = Join-Path $TestDrive 'bin'
        $configPath = Join-Path $configDirectory 'tenant.sync-config.json'
        $mappingPath = Join-Path $configDirectory 'tenant.mapping-config.json'

        New-Item -Path $configDirectory -ItemType Directory -Force | Out-Null
        New-Item -Path $scriptsDirectory -ItemType Directory -Force | Out-Null
        '{}' | Set-Content -Path $configPath
        '{}' | Set-Content -Path $mappingPath
        '# dashboard stub' | Set-Content -Path (Join-Path $scriptsDirectory 'Watch-SyncFactorsMonitor.ps1')
        '# helper stub' | Set-Content -Path (Join-Path $scriptsDirectory 'Set-SyncFactorsTerminalCommandConfig.ps1')

        $result = & "$PSScriptRoot/../scripts/Install-SyncFactorsTerminalCommand.ps1" `
            -ProjectRoot $projectRoot `
            -InstallDirectory $installDirectory `
            -ConfigPath $configPath `
            -MappingConfigPath $mappingPath `
            -SkipPathUpdate

        $result.commandName | Should -Be 'syncfactors'
        $result.configPath | Should -Be ([System.IO.Path]::GetFullPath($configPath))
        $result.mappingConfigPath | Should -Be ([System.IO.Path]::GetFullPath($mappingPath))
        $result.pathUpdated | Should -BeFalse

        Test-Path -Path $result.shellCommandPath | Should -BeTrue
        Test-Path -Path $result.cmdCommandPath | Should -BeTrue
        Test-Path -Path $result.ps1CommandPath | Should -BeTrue
        Test-Path -Path $result.helperShellCommandPath | Should -BeTrue
        Test-Path -Path $result.helperCmdCommandPath | Should -BeTrue
        Test-Path -Path $result.helperPs1CommandPath | Should -BeTrue
        Test-Path -Path $result.metadataPath | Should -BeTrue

        (Get-Content -Path $result.shellCommandPath -Raw) | Should -Match ([regex]::Escape([System.IO.Path]::GetFullPath((Join-Path $scriptsDirectory 'Watch-SyncFactorsMonitor.ps1'))))
        (Get-Content -Path $result.shellCommandPath -Raw) | Should -Match ([regex]::Escape([System.IO.Path]::GetFullPath($configPath)))
        (Get-Content -Path $result.shellCommandPath -Raw) | Should -Match ([regex]::Escape([System.IO.Path]::GetFullPath($mappingPath)))
        (Get-Content -Path $result.cmdCommandPath -Raw) | Should -Match 'pwsh'
        (Get-Content -Path $result.ps1CommandPath -Raw) | Should -Match 'MappingConfigPath'
        $result.configCommandName | Should -Be 'syncfactors-config'
        (Get-Content -Path $result.helperPs1CommandPath -Raw) | Should -Match 'ShowCurrent'
        ((Get-Content -Path $result.metadataPath -Raw) | ConvertFrom-Json).commandName | Should -Be 'syncfactors'
    }

    It 'runs the generated syncfactors powershell shim with named config forwarding and extra monitor args' {
        $projectRoot = Join-Path $TestDrive 'invoke-install-project'
        $configDirectory = Join-Path $projectRoot 'config'
        $scriptsDirectory = Join-Path $projectRoot 'scripts'
        $installDirectory = Join-Path $TestDrive 'invoke-bin'
        $configPath = Join-Path $configDirectory 'tenant.sync-config.json'
        $mappingPath = Join-Path $configDirectory 'tenant.mapping-config.json'
        $dashboardPath = Join-Path $scriptsDirectory 'Watch-SyncFactorsMonitor.ps1'

        New-Item -Path $configDirectory -ItemType Directory -Force | Out-Null
        New-Item -Path $scriptsDirectory -ItemType Directory -Force | Out-Null
        '{}' | Set-Content -Path $configPath
        '{}' | Set-Content -Path $mappingPath
        '# helper stub' | Set-Content -Path (Join-Path $scriptsDirectory 'Set-SyncFactorsTerminalCommandConfig.ps1')
        @'
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ConfigPath,
    [string]$MappingConfigPath,
    [ValidateRange(1, 3600)]
    [int]$RefreshIntervalSeconds = 60,
    [switch]$PauseAutoRefresh,
    [switch]$RunOnce
)

[pscustomobject]@{
    configPath = $ConfigPath
    mappingConfigPath = $MappingConfigPath
    refreshIntervalSeconds = $RefreshIntervalSeconds
    pauseAutoRefresh = [bool]$PauseAutoRefresh
    runOnce = [bool]$RunOnce
} | ConvertTo-Json -Compress
'@ | Set-Content -Path $dashboardPath

        $installResult = & "$PSScriptRoot/../scripts/Install-SyncFactorsTerminalCommand.ps1" `
            -ProjectRoot $projectRoot `
            -InstallDirectory $installDirectory `
            -ConfigPath $configPath `
            -MappingConfigPath $mappingPath `
            -SkipPathUpdate

        $result = & $installResult.ps1CommandPath -RefreshIntervalSeconds 9 -PauseAutoRefresh -RunOnce | ConvertFrom-Json

        $result.configPath | Should -Be ([System.IO.Path]::GetFullPath($configPath))
        $result.mappingConfigPath | Should -Be ([System.IO.Path]::GetFullPath($mappingPath))
        $result.refreshIntervalSeconds | Should -Be 9
        $result.pauseAutoRefresh | Should -BeTrue
        $result.runOnce | Should -BeTrue
    }

    It 'auto-discovers local config files and persists PATH updates for the installed command' {
        $projectRoot = Join-Path $TestDrive 'auto-install-project'
        $configDirectory = Join-Path $projectRoot 'config'
        $scriptsDirectory = Join-Path $projectRoot 'scripts'
        $installDirectory = Join-Path $TestDrive 'auto-bin'
        $profilePath = Join-Path $TestDrive 'profiles/.zprofile'
        $originalPath = $env:PATH
        $originalShell = $env:SHELL

        New-Item -Path $configDirectory -ItemType Directory -Force | Out-Null
        New-Item -Path $scriptsDirectory -ItemType Directory -Force | Out-Null
        '{}' | Set-Content -Path (Join-Path $configDirectory 'local.real.sync-config.json')
        '{}' | Set-Content -Path (Join-Path $configDirectory 'local.real.mapping-config.json')
        '# dashboard stub' | Set-Content -Path (Join-Path $scriptsDirectory 'Watch-SyncFactorsMonitor.ps1')
        '# helper stub' | Set-Content -Path (Join-Path $scriptsDirectory 'Set-SyncFactorsTerminalCommandConfig.ps1')

        try {
            $env:PATH = '/usr/bin'
            $env:SHELL = '/bin/zsh'

            $result = & "$PSScriptRoot/../scripts/Install-SyncFactorsTerminalCommand.ps1" `
                -ProjectRoot $projectRoot `
                -InstallDirectory $installDirectory `
                -ShellProfilePath $profilePath

            $result.configPath | Should -Be ([System.IO.Path]::GetFullPath((Join-Path $configDirectory 'local.real.sync-config.json')))
            $result.mappingConfigPath | Should -Be ([System.IO.Path]::GetFullPath((Join-Path $configDirectory 'local.real.mapping-config.json')))
            $result.pathUpdated | Should -BeTrue
            $result.currentSessionPathUpdated | Should -BeTrue
            $result.shellProfilePath | Should -Be ([System.IO.Path]::GetFullPath($profilePath))

            ($env:PATH -split [System.IO.Path]::PathSeparator) | Should -Contain ([System.IO.Path]::GetFullPath($installDirectory))
            (Get-Content -Path $profilePath -Raw) | Should -Match ([regex]::Escape([System.IO.Path]::GetFullPath($installDirectory)))
        } finally {
            $env:PATH = $originalPath
            $env:SHELL = $originalShell
        }
    }

    It 'repoints syncfactors defaults through the installed config helper command' {
        $projectRoot = Join-Path $TestDrive 'helper-install-project'
        $configDirectory = Join-Path $projectRoot 'config'
        $scriptsDirectory = Join-Path $projectRoot 'scripts'
        $installDirectory = Join-Path $TestDrive 'helper-bin'
        $initialConfigPath = Join-Path $configDirectory 'tenant-a.sync-config.json'
        $initialMappingPath = Join-Path $configDirectory 'tenant-a.mapping-config.json'
        $nextConfigPath = Join-Path $configDirectory 'tenant-b.sync-config.json'
        $nextMappingPath = Join-Path $configDirectory 'tenant-b.mapping-config.json'
        $dashboardPath = Join-Path $scriptsDirectory 'Watch-SyncFactorsMonitor.ps1'

        New-Item -Path $configDirectory -ItemType Directory -Force | Out-Null
        New-Item -Path $scriptsDirectory -ItemType Directory -Force | Out-Null
        '{}' | Set-Content -Path $initialConfigPath
        '{}' | Set-Content -Path $initialMappingPath
        '{}' | Set-Content -Path $nextConfigPath
        '{}' | Set-Content -Path $nextMappingPath
        Copy-Item -Path "$PSScriptRoot/../scripts/Set-SyncFactorsTerminalCommandConfig.ps1" -Destination (Join-Path $scriptsDirectory 'Set-SyncFactorsTerminalCommandConfig.ps1')
        Copy-Item -Path "$PSScriptRoot/../scripts/Install-SyncFactorsTerminalCommand.ps1" -Destination (Join-Path $scriptsDirectory 'Install-SyncFactorsTerminalCommand.ps1')
        @'
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ConfigPath,
    [string]$MappingConfigPath
)

[pscustomobject]@{
    configPath = $ConfigPath
    mappingConfigPath = $MappingConfigPath
} | ConvertTo-Json -Compress
'@ | Set-Content -Path $dashboardPath

        $installResult = & "$PSScriptRoot/../scripts/Install-SyncFactorsTerminalCommand.ps1" `
            -ProjectRoot $projectRoot `
            -InstallDirectory $installDirectory `
            -ConfigPath $initialConfigPath `
            -MappingConfigPath $initialMappingPath `
            -SkipPathUpdate

        $showCurrent = & $installResult.helperPs1CommandPath -ShowCurrent | ConvertTo-Json -Compress | ConvertFrom-Json
        $showCurrent.configPath | Should -Be ([System.IO.Path]::GetFullPath($initialConfigPath))
        $showCurrent.mappingConfigPath | Should -Be ([System.IO.Path]::GetFullPath($initialMappingPath))

        $updateResult = & $installResult.helperPs1CommandPath `
            -ConfigPath $nextConfigPath `
            -MappingConfigPath $nextMappingPath

        $updateResult.configPath | Should -Be ([System.IO.Path]::GetFullPath($nextConfigPath))
        $updateResult.mappingConfigPath | Should -Be ([System.IO.Path]::GetFullPath($nextMappingPath))

        $dashboardRun = & $installResult.ps1CommandPath | ConvertFrom-Json
        $dashboardRun.configPath | Should -Be ([System.IO.Path]::GetFullPath($nextConfigPath))
        $dashboardRun.mappingConfigPath | Should -Be ([System.IO.Path]::GetFullPath($nextMappingPath))
    }

    It 'uninstalls syncfactors shims and the install metadata file' {
        $projectRoot = Join-Path $TestDrive 'uninstall-project'
        $configDirectory = Join-Path $projectRoot 'config'
        $scriptsDirectory = Join-Path $projectRoot 'scripts'
        $installDirectory = Join-Path $TestDrive 'uninstall-bin'
        $configPath = Join-Path $configDirectory 'tenant.sync-config.json'
        $mappingPath = Join-Path $configDirectory 'tenant.mapping-config.json'

        New-Item -Path $configDirectory -ItemType Directory -Force | Out-Null
        New-Item -Path $scriptsDirectory -ItemType Directory -Force | Out-Null
        '{}' | Set-Content -Path $configPath
        '{}' | Set-Content -Path $mappingPath
        '# dashboard stub' | Set-Content -Path (Join-Path $scriptsDirectory 'Watch-SyncFactorsMonitor.ps1')
        '# helper stub' | Set-Content -Path (Join-Path $scriptsDirectory 'Set-SyncFactorsTerminalCommandConfig.ps1')

        $installResult = & "$PSScriptRoot/../scripts/Install-SyncFactorsTerminalCommand.ps1" `
            -ProjectRoot $projectRoot `
            -InstallDirectory $installDirectory `
            -ConfigPath $configPath `
            -MappingConfigPath $mappingPath `
            -SkipPathUpdate

        $uninstallResult = & "$PSScriptRoot/../scripts/Install-SyncFactorsTerminalCommand.ps1" `
            -InstallDirectory $installDirectory `
            -Uninstall

        $uninstallResult.removed | Should -BeTrue
        $uninstallResult.removedPaths.Count | Should -Be 7
        Test-Path -Path $installResult.shellCommandPath | Should -BeFalse
        Test-Path -Path $installResult.cmdCommandPath | Should -BeFalse
        Test-Path -Path $installResult.ps1CommandPath | Should -BeFalse
        Test-Path -Path $installResult.helperShellCommandPath | Should -BeFalse
        Test-Path -Path $installResult.helperCmdCommandPath | Should -BeFalse
        Test-Path -Path $installResult.helperPs1CommandPath | Should -BeFalse
        Test-Path -Path $installResult.metadataPath | Should -BeFalse
    }

    It 'can uninstall syncfactors and remove the installer PATH update' {
        $projectRoot = Join-Path $TestDrive 'uninstall-path-project'
        $configDirectory = Join-Path $projectRoot 'config'
        $scriptsDirectory = Join-Path $projectRoot 'scripts'
        $installDirectory = Join-Path $TestDrive 'uninstall-path-bin'
        $profilePath = Join-Path $TestDrive 'profiles/.zprofile'
        $originalPath = $env:PATH
        $originalShell = $env:SHELL

        New-Item -Path $configDirectory -ItemType Directory -Force | Out-Null
        New-Item -Path $scriptsDirectory -ItemType Directory -Force | Out-Null
        '{}' | Set-Content -Path (Join-Path $configDirectory 'local.real.sync-config.json')
        '{}' | Set-Content -Path (Join-Path $configDirectory 'local.real.mapping-config.json')
        '# dashboard stub' | Set-Content -Path (Join-Path $scriptsDirectory 'Watch-SyncFactorsMonitor.ps1')
        '# helper stub' | Set-Content -Path (Join-Path $scriptsDirectory 'Set-SyncFactorsTerminalCommandConfig.ps1')

        try {
            $env:PATH = '/usr/bin'
            $env:SHELL = '/bin/zsh'

            & "$PSScriptRoot/../scripts/Install-SyncFactorsTerminalCommand.ps1" `
                -ProjectRoot $projectRoot `
                -InstallDirectory $installDirectory `
                -ShellProfilePath $profilePath | Out-Null

            $result = & "$PSScriptRoot/../scripts/Install-SyncFactorsTerminalCommand.ps1" `
                -InstallDirectory $installDirectory `
                -ShellProfilePath $profilePath `
                -Uninstall `
                -RemovePathUpdate

            $result.pathUpdated | Should -BeTrue
            $result.currentSessionPathUpdated | Should -BeTrue
            ($env:PATH -split [System.IO.Path]::PathSeparator) | Should -Not -Contain ([System.IO.Path]::GetFullPath($installDirectory))
            (Get-Content -Path $profilePath -Raw) | Should -Not -Match ([regex]::Escape([System.IO.Path]::GetFullPath($installDirectory)))
        } finally {
            $env:PATH = $originalPath
            $env:SHELL = $originalShell
        }
    }

    It 'runs the synthetic dry-run entry script as a bounded smoke test' {
        $outputDirectory = Join-Path $TestDrive 'synthetic-output'
        $expectedReportPath = Join-Path $outputDirectory 'synthetic-report.json'
        $reportPathFile = Join-Path $outputDirectory 'synthetic-report-path.txt'

        New-Item -Path $outputDirectory -ItemType Directory -Force | Out-Null
        @'
{
  "status": "Succeeded",
  "creates": [],
  "updates": [],
  "disables": [],
  "deletions": [],
  "conflicts": [],
  "guardrailFailures": [],
  "quarantined": [],
  "unchanged": []
}
'@ | Set-Content -Path $expectedReportPath
        $expectedReportPath | Set-Content -Path $reportPathFile

        Mock Invoke-Pester { [pscustomobject]@{ FailedCount = 0 } }

        $result = & "$PSScriptRoot/../scripts/Invoke-SyntheticSyncFactorsDryRun.ps1" -UserCount 10 -ManagerCount 2 -OutputDirectory $outputDirectory -AsJson | ConvertFrom-Json -Depth 10

        $result.userCount | Should -Be 10
        $result.managerCount | Should -Be 2
        $result.reportPath | Should -Be $expectedReportPath
        Test-Path -Path (Join-Path $outputDirectory 'SyntheticDryRunHarness.Tests.ps1') | Should -BeTrue
        Assert-MockCalled Invoke-Pester -Times 1 -Exactly
    }
}
