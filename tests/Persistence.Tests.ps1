Describe 'SQLite persistence helpers' {
    BeforeAll {
        Import-Module "$PSScriptRoot/../src/Modules/SyncFactors/Persistence.psm1" -Force
    }

    It 'derives a default SQLite path from the state path' {
        $sqlitePath = Get-SyncFactorsSqlitePath -StatePath '/tmp/syncfactors/state/runtime-state.json'

        $sqlitePath | Should -Be '/tmp/syncfactors/state/syncfactors.db'
    }

    It 'prefers an explicit persistence.sqlitePath from config' {
        $config = [pscustomobject]@{
            persistence = [pscustomobject]@{
                sqlitePath = '/var/lib/syncfactors/custom.db'
            }
            state = [pscustomobject]@{
                path = '/tmp/ignored/state.json'
            }
        }

        $sqlitePath = Get-SyncFactorsSqlitePath -Config $config

        $sqlitePath | Should -Be '/var/lib/syncfactors/custom.db'
    }

    It 'imports existing JSON artifacts into SQLite' {
        $root = Join-Path $TestDrive 'sqlite-import'
        $stateDirectory = Join-Path $root 'state'
        $reportDirectory = Join-Path $root 'reports'
        $reviewDirectory = Join-Path $reportDirectory 'review'
        $statePath = Join-Path $stateDirectory 'sync-state.json'
        $runtimeStatusPath = Join-Path $stateDirectory 'runtime-status.json'

        New-Item -Path $stateDirectory -ItemType Directory -Force | Out-Null
        New-Item -Path $reviewDirectory -ItemType Directory -Force | Out-Null

        @'
{
  "checkpoint": "2026-03-22T10:00:00",
  "workers": {
    "1001": {
      "adObjectGuid": "guid-1",
      "distinguishedName": "CN=Jamie Doe,OU=Employees,DC=example,DC=com",
      "suppressed": false,
      "firstDisabledAt": null,
      "deleteAfter": null,
      "lastSeenStatus": "active"
    }
  }
}
'@ | Set-Content -Path $statePath

        @'
{
  "runId": "run-1",
  "status": "Succeeded",
  "stage": "Completed",
  "startedAt": "2026-03-22T10:00:00Z",
  "lastUpdatedAt": "2026-03-22T10:05:00Z",
  "completedAt": "2026-03-22T10:05:00Z",
  "processedWorkers": 1,
  "totalWorkers": 1,
  "lastAction": "Run Succeeded.",
  "runtimeStatusPath": "/tmp/runtime-status.json"
}
'@ | Set-Content -Path $runtimeStatusPath

        @'
{
  "runId": "run-1",
  "artifactType": "SyncReport",
  "mode": "Delta",
  "dryRun": false,
  "status": "Succeeded",
  "startedAt": "2026-03-22T10:00:00Z",
  "completedAt": "2026-03-22T10:05:00Z",
  "statePath": "__STATE_PATH__",
  "configPath": "__CONFIG_PATH__",
  "mappingConfigPath": "__MAPPING_PATH__",
  "operations": [],
  "creates": [{ "workerId": "1001", "samAccountName": "jdoe" }],
  "updates": [],
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
'@.Replace('__STATE_PATH__', $statePath.Replace('\', '\\')).Replace('__CONFIG_PATH__', (Join-Path $root 'config.json').Replace('\', '\\')).Replace('__MAPPING_PATH__', (Join-Path $root 'mapping.json').Replace('\', '\\')) | Set-Content -Path (Join-Path $reportDirectory 'syncfactors-Delta-20260322-100500.json')

        $config = [pscustomobject]@{
            state = [pscustomobject]@{
                path = $statePath
            }
            reporting = [pscustomobject]@{
                outputDirectory = $reportDirectory
                reviewOutputDirectory = $reviewDirectory
            }
            persistence = [pscustomobject]@{
                sqlitePath = (Join-Path $stateDirectory 'syncfactors.db')
            }
        }

        $result = Import-SyncFactorsJsonArtifactsToSqlite -Config $config
        $workerRows = sqlite3 -json $result.sqlitePath 'select count(*) as count from worker_state;'
        $runRows = sqlite3 -json $result.sqlitePath 'select count(*) as count from runs;'
        $runtimeRows = sqlite3 -json $result.sqlitePath 'select count(*) as count from runtime_status;'

        $result.stateImported | Should -BeTrue
        $result.runtimeStatusImported | Should -BeTrue
        $result.reportsImported | Should -Be 1
        (($workerRows | ConvertFrom-Json)[0].count) | Should -Be 1
        (($runRows | ConvertFrom-Json)[0].count) | Should -Be 1
        (($runtimeRows | ConvertFrom-Json)[0].count) | Should -Be 1
    }
}
