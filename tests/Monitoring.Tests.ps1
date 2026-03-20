Describe 'Monitoring module' {
    BeforeAll {
        Import-Module "$PSScriptRoot/../src/Modules/SfAdSync/Monitoring.psm1" -Force
    }

    It 'includes config and mapping paths in run summaries' {
        $reportDirectory = Join-Path $TestDrive 'reports'
        $reportPath = Join-Path $reportDirectory 'sf-ad-sync-Delta-20260312-220000.json'
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

        $result = @(Get-SfAdRecentRunSummaries -Directory $reportDirectory -Limit 5)

        $result[0].configPath | Should -Be 'config.json'
        $result[0].mappingConfigPath | Should -Be 'mapping.json'
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

        $resolved = Resolve-SfAdMonitorMappingConfigPath -Status $status

        $resolved | Should -Be (Resolve-Path -Path $mappingPath).Path
    }

    It 'includes all operation buckets in the dashboard browser' {
        $bucketNames = @(Get-SfAdMonitorBucketDefinitions | ForEach-Object { $_.Name })

        $bucketNames | Should -Contain 'creates'
        $bucketNames | Should -Contain 'updates'
        $bucketNames | Should -Contain 'enables'
        $bucketNames | Should -Contain 'disables'
        $bucketNames | Should -Contain 'graveyardMoves'
        $bucketNames | Should -Contain 'deletions'
        $bucketNames | Should -Contain 'unchanged'
    }

    It 'uses review-first bucket ordering for review artifacts' {
        $bucketNames = @(Get-SfAdMonitorBucketDefinitions -Mode Review | ForEach-Object { $_.Name })

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
        $uiState = New-SfAdMonitorUiState
        $uiState.filterText = 'finance'

        $items = @(Get-SfAdMonitorFilteredBucketItems -BucketSelection $bucketSelection -UiState $uiState)

        $items.Count | Should -Be 1
        $items[0].workerId | Should -Be '1002'
    }

    It 'includes all run shortcuts in the default dashboard shortcut help' {
        $uiState = New-SfAdMonitorUiState

        $uiState.autoRefreshEnabled | Should -BeTrue
        $uiState.statusMessage | Should -Match 't toggle auto-refresh'
        $uiState.statusMessage | Should -Match 'd delta dry-run'
        $uiState.statusMessage | Should -Match 's delta sync'
        $uiState.statusMessage | Should -Match 'f full dry-run'
        $uiState.statusMessage | Should -Match 'a full sync'
        $uiState.statusMessage | Should -Match 'w worker preview'
        $uiState.statusMessage | Should -Match 'v review'
    }

    It 'finds the selected operation journal entry for the selected bucket object' {
        $reportPath = Join-Path $TestDrive 'sf-ad-sync-Delta-20260312-220000.json'
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
        $uiState = New-SfAdMonitorUiState
        $uiState.selectedBucketIndex = 5

        $operation = Get-SfAdMonitorSelectedBucketOperation -Status $status -UiState $uiState

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

        $lines = @(Format-SfAdMonitorSelectedObjectLines -SelectedItem $selectedItem -SelectedOperation $selectedOperation)

        ($lines -join "`n") | Should -Match 'Item: workerId=1001'
        ($lines -join "`n") | Should -Match 'Δ title: Old Title -> New Title'
        ($lines -join "`n") | Should -Match 'Δ department: Finance -> Engineering'
    }

    It 'formats create and delete style operations without generic blobs' {
        $createLines = @(Format-SfAdMonitorSelectedObjectLines `
                -SelectedItem ([pscustomobject]@{ workerId = '1001'; samAccountName = 'jdoe' }) `
                -SelectedOperation ([pscustomobject]@{
                    operationType = 'CreateUser'
                    target = [pscustomobject]@{ samAccountName = 'jdoe' }
                    before = $null
                    after = [pscustomobject]@{ samAccountName = 'jdoe'; enabled = 'True' }
                }))
        $deleteLines = @(Format-SfAdMonitorSelectedObjectLines `
                -SelectedItem ([pscustomobject]@{ workerId = '1002'; samAccountName = 'adoe' }) `
                -SelectedOperation ([pscustomobject]@{
                    operationType = 'DeleteUser'
                    target = [pscustomobject]@{ samAccountName = 'adoe' }
                    before = [pscustomobject]@{ samAccountName = 'adoe'; enabled = 'False' }
                    after = $null
                }))

        ($createLines -join "`n") | Should -Match 'Δ samAccountName: \(unset\) -> jdoe'
        ($deleteLines -join "`n") | Should -Match 'Δ samAccountName: adoe -> \(unset\)'
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
        $uiState = New-SfAdMonitorUiState
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

        Mock Get-SfAdMonitorSelectedBucket { $bucketSelection } -ModuleName Monitoring

        $workerState = Get-SfAdMonitorSelectedWorkerState -Status $status -UiState $uiState

        $workerState.workerId | Should -Be '1002'
        $workerState.suppressed | Should -BeTrue
    }

    It 'formats dashboard view with selected run and selected bucket details' {
        $reportPath = Join-Path $TestDrive 'sf-ad-sync-Delta-20260312-220000.json'
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
        $uiState = New-SfAdMonitorUiState
        $uiState.filterText = 'manager'

        $lines = @(Format-SfAdMonitorDashboardView -Status $status -UiState $uiState)

        ($lines -join "`n") | Should -Match 'SuccessFactors AD Sync Dashboard'
        ($lines -join "`n") | Should -Match 'AutoRefresh: On'
        ($lines -join "`n") | Should -Match 'Detail: Quarantined'
        ($lines -join "`n") | Should -Match 'Filter: manager'
        ($lines -join "`n") | Should -Match 'Diagnostics:'
        ($lines -join "`n") | Should -Match 'Selected Object'
        ($lines -join "`n") | Should -Match 'Operation: no matching reversible operation'
        ($lines -join "`n") | Should -Match 'workerId=1002'
        ($lines -join "`n") | Should -Match 'Tracked: workerId=1002'
        ($lines -join "`n") | Should -Not -Match 'Config:'
        ($lines -join "`n") | Should -Not -Match 'Context:'
        ($lines -join "`n") | Should -Not -Match 'Paths:'
    }

    It 'formats review artifacts with review summary and mapped field details' {
        $reportPath = Join-Path $TestDrive 'sf-ad-sync-Review-20260312-220000.json'
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
                    }
                }
            )
        }
        $uiState = New-SfAdMonitorUiState

        $lines = @(Format-SfAdMonitorDashboardView -Status $status -UiState $uiState)

        ($lines -join "`n") | Should -Match 'First Sync Review Summary'
        ($lines -join "`n") | Should -Match 'Review: existing=2 changed=1 aligned=1 creates=1 offboarding=0'
        ($lines -join "`n") | Should -Match 'Map: department -> department \[Trim\]'
        ($lines -join "`n") | Should -Match 'Finance -> Sales'
    }

    It 'formats worker preview artifacts as review-style summaries' {
        $reportPath = Join-Path $TestDrive 'sf-ad-sync-Review-20260312-220000.json'
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
                    }
                }
            )
        }
        $uiState = New-SfAdMonitorUiState

        $lines = @(Format-SfAdMonitorDashboardView -Status $status -UiState $uiState)

        ($lines -join "`n") | Should -Match 'Worker Preview Summary'
        ($lines -join "`n") | Should -Match 't auto-refresh'
        ($lines -join "`n") | Should -Match 'Review: existing=1 changed=1 aligned=0 creates=0 offboarding=0'
        ($lines -join "`n") | Should -Match 'Finance -> Sales'
        ($lines -join "`n") | Should -Match 'd delta dry-run'
        ($lines -join "`n") | Should -Match 's delta sync'
        ($lines -join "`n") | Should -Match 'f full dry-run'
        ($lines -join "`n") | Should -Match 'a full sync'
        ($lines -join "`n") | Should -Match 'w worker preview'
    }

    It 'uses the selected run rather than latest run in the summary panel' {
        $selectedReportPath = Join-Path $TestDrive 'sf-ad-sync-Review-20260312-220000.json'
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
        $uiState = New-SfAdMonitorUiState
        $uiState.selectedRunIndex = 1

        $lines = @(Format-SfAdMonitorDashboardView -Status $status -UiState $uiState)

        ($lines -join "`n") | Should -Match 'First Sync Review Summary'
        ($lines -join "`n") | Should -Match 'Status: Succeeded    Mode: Review'
        ($lines -join "`n") | Should -Match 'Totals: C=0 U=1 D=0 X=0'
        ($lines -join "`n") | Should -Not -Match 'Totals: C=9 U=8 D=0 X=0'
    }

    It 'limits recent runs and detail rows in the compact dashboard' {
        $reportPath = Join-Path $TestDrive 'sf-ad-sync-Delta-20260312-220000.json'
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
        $uiState = New-SfAdMonitorUiState
        $uiState.selectedBucketIndex = 4

        $lines = @(Format-SfAdMonitorDashboardView -Status $status -UiState $uiState)
        $joined = $lines -join "`n"
        $recentRunLines = @($lines | Where-Object { $_ -match '^\s+[>]?\s*Succeeded\s+Delta' })
        $detailLines = @($lines | Where-Object { $_ -match '^[>-] .*workerId=' })

        $recentRunLines.Count | Should -Be 5
        $detailLines.Count | Should -Be 4
        $joined | Should -Match '\.\.\. 1 older runs'
        $joined | Should -Match '\.\.\. 2 more'
    }
}
