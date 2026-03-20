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
    }

    It 'delegates the main sync entry script to Invoke-SfAdSyncRun' {
        Import-Module "$PSScriptRoot/../src/Modules/SfAdSync/Sync.psm1" -Force -DisableNameChecking
        Mock Invoke-SfAdSyncRun { 'report.json' }

        $result = & "$PSScriptRoot/../src/Invoke-SfAdSync.ps1" -ConfigPath 'config.json' -MappingConfigPath 'mapping.json' -Mode Review -DryRun -WorkerId '1001'

        Assert-MockCalled Invoke-SfAdSyncRun -Times 1 -Exactly -ParameterFilter {
            $ConfigPath -eq 'config.json' -and
            $MappingConfigPath -eq 'mapping.json' -and
            $Mode -eq 'Review' -and
            $DryRun -and
            $WorkerId -eq '1001'
        }
        $result | Should -Be 'report.json'
    }

    It 'delegates the rollback entry script to Invoke-SfAdRollback' {
        Import-Module "$PSScriptRoot/../src/Modules/SfAdSync/Rollback.psm1" -Force -DisableNameChecking
        Mock Invoke-SfAdRollback {}

        & "$PSScriptRoot/../scripts/Undo-SfAdSyncRun.ps1" -ReportPath 'report.json' -ConfigPath 'config.json' -DryRun

        Assert-MockCalled Invoke-SfAdRollback -Times 1 -Exactly -ParameterFilter {
            $ReportPath -eq 'report.json' -and
            $ConfigPath -eq 'config.json' -and
            $DryRun
        }
    }

    It 'runs the first-sync review entry script and returns the review summary in json mode' {
        $configPath = Join-Path $TestDrive 'review-config.json'
        $mappingPath = Join-Path $TestDrive 'review-mapping.json'
        $invokeStubPath = Join-Path $TestDrive 'invoke-review-stub.ps1'
        $reportPath = Join-Path $TestDrive 'sf-ad-sync-Review.json'

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
            if ($ChildPath -eq 'src/Invoke-SfAdSync.ps1') {
                return $invokeStubPath
            }

            return [System.IO.Path]::Combine($Path, $ChildPath)
        }

        $result = & "$PSScriptRoot/../scripts/Invoke-SfAdFirstSyncReview.ps1" -ConfigPath $configPath -MappingConfigPath $mappingPath -AsJson | ConvertFrom-Json -Depth 10

        $result.mode | Should -Be 'Review'
        $result.status | Should -Be 'Succeeded'
        $result.reviewSummary.existingUsersMatched | Should -Be 2
        $result.reviewSummary.proposedCreates | Should -Be 1
    }

    It 'runs the worker preview entry script and returns the scoped preview in json mode' {
        $configPath = Join-Path $TestDrive 'preview-config.json'
        $mappingPath = Join-Path $TestDrive 'preview-mapping.json'
        $invokeStubPath = Join-Path $TestDrive 'invoke-preview-stub.ps1'
        $reportPath = Join-Path $TestDrive 'sf-ad-sync-Review.json'

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
    [string]`$WorkerId
)

'$reportPath'
"@ | Set-Content -Path $invokeStubPath

        Mock Join-Path {
            if ($ChildPath -eq 'src/Invoke-SfAdSync.ps1') {
                return $invokeStubPath
            }

            return [System.IO.Path]::Combine($Path, $ChildPath)
        }

        $result = & "$PSScriptRoot/../scripts/Invoke-SfAdWorkerPreview.ps1" -ConfigPath $configPath -MappingConfigPath $mappingPath -WorkerId '1001' -AsJson | ConvertFrom-Json -Depth 20

        $result.artifactType | Should -Be 'WorkerPreview'
        $result.workerScope.workerId | Should -Be '1001'
        $result.preview.samAccountName | Should -Be 'jdoe'
        $result.preview.reviewCategory | Should -Be 'ExistingUserChanges'
        $result.changedAttributes[0].targetAttribute | Should -Be 'GivenName'
        $result.operations[0].operationType | Should -Be 'UpdateAttributes'
    }

    It 'returns preflight details from the preflight script in json mode' {
        Import-Module "$PSScriptRoot/../src/Modules/SfAdSync/Sync.psm1" -Force -DisableNameChecking
        Mock Test-SfAdSyncPreflight {
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

        $result = & "$PSScriptRoot/../scripts/Invoke-SfAdPreflight.ps1" -ConfigPath 'config.json' -MappingConfigPath 'mapping.json' -AsJson | ConvertFrom-Json

        $result.success | Should -BeTrue
        $result.mappingCount | Should -Be 3
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
        } -ParameterFilter { $ChildPath -eq 'src/Invoke-SfAdSync.ps1' }

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

            $result = & "$PSScriptRoot/../scripts/Invoke-SfAdSyncInteractive.ps1" -ConfigPath $configPath -MappingConfigPath $mappingPath -Mode Full -DryRun | ConvertFrom-Json -Depth 10

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
        } -ParameterFilter { $ChildPath -eq 'src/Invoke-SfAdSync.ps1' }
        Mock Read-Host { throw 'Read-Host should not be called when required process environment variables are already set.' }

        try {
            function global:Get-CimInstance {
                [pscustomobject]@{ PartOfDomain = $true }
            }

            $result = & "$PSScriptRoot/../scripts/Invoke-SfAdSyncInteractive.ps1" -ConfigPath $configPath -MappingConfigPath $mappingPath | ConvertFrom-Json -Depth 10

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
        } -ParameterFilter { $ChildPath -eq 'src/Invoke-SfAdSync.ps1' }

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

            $result = & "$PSScriptRoot/../scripts/Invoke-SfAdSyncInteractive.ps1" -ConfigPath $configPath -MappingConfigPath $mappingPath | ConvertFrom-Json -Depth 10

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

    It 'returns sync status from local state and report files in json mode' {
        $configPath = Join-Path $TestDrive 'status-config.json'
        $statePath = Join-Path $TestDrive 'state.json'
        $reportDir = Join-Path $TestDrive 'reports'
        $reportPath = Join-Path $reportDir 'sf-ad-sync-Delta-20260312-220000.json'
        $olderReportPath = Join-Path $reportDir 'sf-ad-sync-Full-20260312-210000.json'
        $runtimeStatusPath = Join-Path $TestDrive 'runtime-status.json'

        New-Item -Path $reportDir -ItemType Directory -Force | Out-Null
        (New-StatusConfigContent -StatePath $statePath -ReportDirectory $reportDir) | Set-Content -Path $configPath

        @{
            checkpoint = '2026-03-12T21:00:00'
            workers = @{
                '1001' = @{
                    suppressed = $true
                    deleteAfter = (Get-Date).AddDays(-1).ToString('o')
                }
                '1002' = @{
                    suppressed = $false
                }
            }
        } | ConvertTo-Json -Depth 10 | Set-Content -Path $statePath

        @{
            runId = 'run-123'
            mode = 'Delta'
            dryRun = $false
            startedAt = '2026-03-12T21:30:00'
            completedAt = '2026-03-12T21:35:00'
            status = 'Succeeded'
            operations = @(@{ operationType = 'CreateUser' })
            creates = @(@{})
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
        @{
            runId = 'run-122'
            mode = 'Full'
            dryRun = $true
            startedAt = '2026-03-12T20:30:00'
            completedAt = '2026-03-12T20:40:00'
            status = 'Failed'
            operations = @()
            creates = @()
            updates = @()
            enables = @()
            disables = @(@{})
            graveyardMoves = @()
            deletions = @()
            quarantined = @()
            conflicts = @(@{})
            guardrailFailures = @(@{})
            manualReview = @()
            unchanged = @()
        } | ConvertTo-Json -Depth 10 | Set-Content -Path $olderReportPath
        @{
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
        } | ConvertTo-Json -Depth 10 | Set-Content -Path $runtimeStatusPath

        $result = & "$PSScriptRoot/../scripts/Get-SfAdSyncStatus.ps1" -ConfigPath $configPath -AsJson | ConvertFrom-Json -Depth 10

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

    It 'returns zeroed latest report details when no report files exist' {
        $configPath = Join-Path $TestDrive 'status-config-empty.json'
        $emptyRoot = Join-Path $TestDrive 'empty-status'
        $statePath = Join-Path $emptyRoot 'state-empty.json'
        $reportDir = Join-Path $emptyRoot 'reports-empty'

        New-Item -Path $emptyRoot -ItemType Directory -Force | Out-Null
        New-Item -Path $reportDir -ItemType Directory -Force | Out-Null
        (New-StatusConfigContent -StatePath $statePath -ReportDirectory $reportDir) | Set-Content -Path $configPath
        @{ checkpoint = $null; workers = @{} } | ConvertTo-Json -Depth 10 | Set-Content -Path $statePath

        $result = & "$PSScriptRoot/../scripts/Get-SfAdSyncStatus.ps1" -ConfigPath $configPath -AsJson | ConvertFrom-Json -Depth 10

        $result.totalTrackedWorkers | Should -Be 0
        $result.latestReport.path | Should -Be $null
        $result.latestReport.creates | Should -Be 0
        $result.latestReport.status | Should -Be $null
        $result.currentRun.status | Should -Be 'Idle'
        @($result.recentRuns).Count | Should -Be 0
    }

    It 'throws when the state json is corrupt' {
        $configPath = Join-Path $TestDrive 'status-config-corrupt-state.json'
        $statePath = Join-Path $TestDrive 'state-corrupt.json'
        $reportDir = Join-Path $TestDrive 'reports-corrupt-state'

        New-Item -Path $reportDir -ItemType Directory -Force | Out-Null
        (New-StatusConfigContent -StatePath $statePath -ReportDirectory $reportDir) | Set-Content -Path $configPath
        '{bad json' | Set-Content -Path $statePath

        { & "$PSScriptRoot/../scripts/Get-SfAdSyncStatus.ps1" -ConfigPath $configPath -AsJson | Out-Null } | Should -Throw
    }

    It 'throws when the latest report json is corrupt' {
        $configPath = Join-Path $TestDrive 'status-config-corrupt-report.json'
        $statePath = Join-Path $TestDrive 'state-valid.json'
        $reportDir = Join-Path $TestDrive 'reports-corrupt-report'
        $reportPath = Join-Path $reportDir 'sf-ad-sync-Delta-corrupt.json'

        New-Item -Path $reportDir -ItemType Directory -Force | Out-Null
        (New-StatusConfigContent -StatePath $statePath -ReportDirectory $reportDir) | Set-Content -Path $configPath
        @{ checkpoint = $null; workers = @{} } | ConvertTo-Json -Depth 10 | Set-Content -Path $statePath
        '{bad json' | Set-Content -Path $reportPath

        { & "$PSScriptRoot/../scripts/Get-SfAdSyncStatus.ps1" -ConfigPath $configPath -AsJson | Out-Null } | Should -Throw
    }

    It 'renders the monitor view in text mode with current and recent run details' {
        $configPath = Join-Path $TestDrive 'monitor-config.json'
        $statePath = Join-Path $TestDrive 'monitor-state.json'
        $reportDir = Join-Path $TestDrive 'monitor-reports'
        $reportPath = Join-Path $reportDir 'sf-ad-sync-Delta-20260312-220000.json'
        $runtimeStatusPath = Join-Path $TestDrive 'runtime-status.json'

        New-Item -Path $reportDir -ItemType Directory -Force | Out-Null
        (New-StatusConfigContent -StatePath $statePath -ReportDirectory $reportDir) | Set-Content -Path $configPath
        @{ checkpoint = '2026-03-12T21:00:00'; workers = @{} } | ConvertTo-Json -Depth 10 | Set-Content -Path $statePath
        @{
            runId = 'run-123'
            mode = 'Delta'
            dryRun = $false
            startedAt = '2026-03-12T21:30:00'
            completedAt = '2026-03-12T21:35:00'
            status = 'Succeeded'
            operations = @(@{ operationType = 'CreateUser' })
            creates = @(@{})
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
        @{
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
        } | ConvertTo-Json -Depth 10 | Set-Content -Path $runtimeStatusPath

        $result = & "$PSScriptRoot/../scripts/Watch-SfAdSyncMonitor.ps1" -ConfigPath $configPath -RunOnce -AsText

        $result | Should -Match 'SuccessFactors AD Sync Monitor'
        $result | Should -Match 'Stage: ProcessingWorkers'
        $result | Should -Match 'Updated attributes for worker 1002'
        $result | Should -Match 'Succeeded'
    }

    It 'renders an error banner when the monitor hits corrupt runtime status json' {
        $configPath = Join-Path $TestDrive 'monitor-error-config.json'
        $statePath = Join-Path $TestDrive 'monitor-error-state.json'
        $reportDir = Join-Path $TestDrive 'monitor-error-reports'
        $runtimeStatusPath = Join-Path $TestDrive 'runtime-status.json'

        New-Item -Path $reportDir -ItemType Directory -Force | Out-Null
        (New-StatusConfigContent -StatePath $statePath -ReportDirectory $reportDir) | Set-Content -Path $configPath
        @{ checkpoint = $null; workers = @{} } | ConvertTo-Json -Depth 10 | Set-Content -Path $statePath
        '{bad json' | Set-Content -Path $runtimeStatusPath

        $result = & "$PSScriptRoot/../scripts/Watch-SfAdSyncMonitor.ps1" -ConfigPath $configPath -RunOnce -AsText

        $result | Should -Match 'Monitor error'
    }

    It 'installs synctui shims that point at the dashboard with explicit config paths' {
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
        '# dashboard stub' | Set-Content -Path (Join-Path $scriptsDirectory 'Watch-SfAdSyncMonitor.ps1')

        $result = & "$PSScriptRoot/../scripts/Install-SfAdSyncTerminalCommand.ps1" `
            -ProjectRoot $projectRoot `
            -InstallDirectory $installDirectory `
            -ConfigPath $configPath `
            -MappingConfigPath $mappingPath `
            -SkipPathUpdate

        $result.commandName | Should -Be 'synctui'
        $result.configPath | Should -Be ([System.IO.Path]::GetFullPath($configPath))
        $result.mappingConfigPath | Should -Be ([System.IO.Path]::GetFullPath($mappingPath))
        $result.pathUpdated | Should -BeFalse

        Test-Path -Path $result.shellCommandPath | Should -BeTrue
        Test-Path -Path $result.cmdCommandPath | Should -BeTrue
        Test-Path -Path $result.ps1CommandPath | Should -BeTrue
        Test-Path -Path $result.metadataPath | Should -BeTrue

        (Get-Content -Path $result.shellCommandPath -Raw) | Should -Match ([regex]::Escape([System.IO.Path]::GetFullPath((Join-Path $scriptsDirectory 'Watch-SfAdSyncMonitor.ps1'))))
        (Get-Content -Path $result.shellCommandPath -Raw) | Should -Match ([regex]::Escape([System.IO.Path]::GetFullPath($configPath)))
        (Get-Content -Path $result.shellCommandPath -Raw) | Should -Match ([regex]::Escape([System.IO.Path]::GetFullPath($mappingPath)))
        (Get-Content -Path $result.cmdCommandPath -Raw) | Should -Match 'pwsh'
        (Get-Content -Path $result.ps1CommandPath -Raw) | Should -Match 'MappingConfigPath'
        ((Get-Content -Path $result.metadataPath -Raw) | ConvertFrom-Json).commandName | Should -Be 'synctui'
    }

    It 'runs the generated synctui powershell shim with named config forwarding and extra monitor args' {
        $projectRoot = Join-Path $TestDrive 'invoke-install-project'
        $configDirectory = Join-Path $projectRoot 'config'
        $scriptsDirectory = Join-Path $projectRoot 'scripts'
        $installDirectory = Join-Path $TestDrive 'invoke-bin'
        $configPath = Join-Path $configDirectory 'tenant.sync-config.json'
        $mappingPath = Join-Path $configDirectory 'tenant.mapping-config.json'
        $dashboardPath = Join-Path $scriptsDirectory 'Watch-SfAdSyncMonitor.ps1'

        New-Item -Path $configDirectory -ItemType Directory -Force | Out-Null
        New-Item -Path $scriptsDirectory -ItemType Directory -Force | Out-Null
        '{}' | Set-Content -Path $configPath
        '{}' | Set-Content -Path $mappingPath
        @'
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ConfigPath,
    [string]$MappingConfigPath,
    [ValidateRange(1, 3600)]
    [int]$RefreshIntervalSeconds = 3,
    [switch]$RunOnce
)

[pscustomobject]@{
    configPath = $ConfigPath
    mappingConfigPath = $MappingConfigPath
    refreshIntervalSeconds = $RefreshIntervalSeconds
    runOnce = [bool]$RunOnce
} | ConvertTo-Json -Compress
'@ | Set-Content -Path $dashboardPath

        $installResult = & "$PSScriptRoot/../scripts/Install-SfAdSyncTerminalCommand.ps1" `
            -ProjectRoot $projectRoot `
            -InstallDirectory $installDirectory `
            -ConfigPath $configPath `
            -MappingConfigPath $mappingPath `
            -SkipPathUpdate

        $result = & $installResult.ps1CommandPath -RefreshIntervalSeconds 9 -RunOnce | ConvertFrom-Json

        $result.configPath | Should -Be ([System.IO.Path]::GetFullPath($configPath))
        $result.mappingConfigPath | Should -Be ([System.IO.Path]::GetFullPath($mappingPath))
        $result.refreshIntervalSeconds | Should -Be 9
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
        '# dashboard stub' | Set-Content -Path (Join-Path $scriptsDirectory 'Watch-SfAdSyncMonitor.ps1')

        try {
            $env:PATH = '/usr/bin'
            $env:SHELL = '/bin/zsh'

            $result = & "$PSScriptRoot/../scripts/Install-SfAdSyncTerminalCommand.ps1" `
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

    It 'uninstalls synctui shims and the install metadata file' {
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
        '# dashboard stub' | Set-Content -Path (Join-Path $scriptsDirectory 'Watch-SfAdSyncMonitor.ps1')

        $installResult = & "$PSScriptRoot/../scripts/Install-SfAdSyncTerminalCommand.ps1" `
            -ProjectRoot $projectRoot `
            -InstallDirectory $installDirectory `
            -ConfigPath $configPath `
            -MappingConfigPath $mappingPath `
            -SkipPathUpdate

        $uninstallResult = & "$PSScriptRoot/../scripts/Install-SfAdSyncTerminalCommand.ps1" `
            -InstallDirectory $installDirectory `
            -Uninstall

        $uninstallResult.removed | Should -BeTrue
        $uninstallResult.removedPaths.Count | Should -Be 4
        Test-Path -Path $installResult.shellCommandPath | Should -BeFalse
        Test-Path -Path $installResult.cmdCommandPath | Should -BeFalse
        Test-Path -Path $installResult.ps1CommandPath | Should -BeFalse
        Test-Path -Path $installResult.metadataPath | Should -BeFalse
    }

    It 'can uninstall synctui and remove the installer PATH update' {
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
        '# dashboard stub' | Set-Content -Path (Join-Path $scriptsDirectory 'Watch-SfAdSyncMonitor.ps1')

        try {
            $env:PATH = '/usr/bin'
            $env:SHELL = '/bin/zsh'

            & "$PSScriptRoot/../scripts/Install-SfAdSyncTerminalCommand.ps1" `
                -ProjectRoot $projectRoot `
                -InstallDirectory $installDirectory `
                -ShellProfilePath $profilePath | Out-Null

            $result = & "$PSScriptRoot/../scripts/Install-SfAdSyncTerminalCommand.ps1" `
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

        $result = & "$PSScriptRoot/../scripts/Invoke-SyntheticSfAdDryRun.ps1" -UserCount 10 -ManagerCount 2 -OutputDirectory $outputDirectory -AsJson | ConvertFrom-Json -Depth 10

        $result.userCount | Should -Be 10
        $result.managerCount | Should -Be 2
        $result.reportPath | Should -Be $expectedReportPath
        Test-Path -Path (Join-Path $outputDirectory 'SyntheticDryRunHarness.Tests.ps1') | Should -BeTrue
        Assert-MockCalled Invoke-Pester -Times 1 -Exactly
    }
}
