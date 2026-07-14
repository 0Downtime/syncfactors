Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Describe 'Assert-CiWorkflowSecurity governance policy' {
    BeforeAll {
        $scriptPath = Join-Path $PSScriptRoot '../scripts/Assert-CiWorkflowSecurity.ps1'

        function New-WorkflowFixture {
            param(
                [Parameter(Mandatory)]
                [string] $Root,

                [Parameter(Mandatory)]
                [hashtable] $Workflows
            )

            $workflowDirectory = Join-Path $Root '.github/workflows'
            New-Item -ItemType Directory -Path $workflowDirectory -Force | Out-Null

            foreach ($workflow in $Workflows.GetEnumerator()) {
                Set-Content -Path (Join-Path $workflowDirectory $workflow.Key) -Value $workflow.Value -NoNewline
            }
        }

        function Invoke-WorkflowPolicy {
            param(
                [Parameter(Mandatory)]
                [string] $Root
            )

            $output = & pwsh -NoLogo -NoProfile -File $scriptPath -RepositoryRoot $Root 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw ($output -join [Environment]::NewLine)
            }
        }
    }

    It 'accepts trusted bot auto-merge and post-merge release gates' {
        $fixtureRoot = Join-Path $TestDrive 'trusted-bot'
        New-WorkflowFixture -Root $fixtureRoot -Workflows @{
            'auto-merge.yml' = @'
name: Auto Merge
on:
  pull_request_target:
    types: [opened]
permissions:
  contents: write
  pull-requests: write
jobs:
  enable:
    if: github.event.pull_request.base.ref == 'main' && github.actor == 'dependabot[bot]'
    runs-on: ubuntu-latest
    steps:
      - run: gh pr merge --auto --merge "$PR_URL"
'@
            'test.yml' = @'
name: Test
on:
  push:
    branches: [main]
'@
            'security.yml' = @'
name: Security Scans
on:
  push:
    branches: [main]
'@
            'release.yml' = @'
name: Release
on:
  push:
    branches: [main]
'@
        }

        { Invoke-WorkflowPolicy -Root $fixtureRoot } | Should -Not -Throw
    }

    It 'rejects auto-merge without a trusted bot or reviewed opt-in guard' {
        $fixtureRoot = Join-Path $TestDrive 'untrusted-auto-merge'
        New-WorkflowFixture -Root $fixtureRoot -Workflows @{
            'auto-merge.yml' = @'
name: Auto Merge
on:
  pull_request_target:
jobs:
  enable:
    if: github.event.pull_request.base.ref == 'main'
    runs-on: ubuntu-latest
    steps:
      - run: gh pr merge --auto --merge "$PR_URL"
'@
            'test.yml' = "name: Test`non:`n  push:`n    branches: [main]"
            'security.yml' = "name: Security Scans`non:`n  push:`n    branches: [main]"
            'release.yml' = "name: Release`non:`n  push:`n    branches: [main]"
        }

        { Invoke-WorkflowPolicy -Root $fixtureRoot } | Should -Throw '*trusted bot or an explicit reviewed opt-in*'
    }

    It 'accepts the maintainer-reviewed auto-merge opt-in label' {
        $fixtureRoot = Join-Path $TestDrive 'reviewed-opt-in'
        New-WorkflowFixture -Root $fixtureRoot -Workflows @{
            'auto-merge.yml' = @'
name: Auto Merge
on:
  pull_request_target:
    types: [opened, labeled]
jobs:
  enable:
    if: github.event.pull_request.base.ref == 'main' && contains(github.event.pull_request.labels.*.name, 'automerge:approved')
    runs-on: ubuntu-latest
    steps:
      - run: gh pr merge --auto --merge "$PR_URL"
'@
            'test.yml' = "name: Test`non:`n  push:`n    branches: [main]"
            'security.yml' = "name: Security Scans`non:`n  push:`n    branches: [main]"
            'release.yml' = "name: Release`non:`n  push:`n    branches: [main]"
        }

        { Invoke-WorkflowPolicy -Root $fixtureRoot } | Should -Not -Throw
    }

    It 'rejects a reviewed opt-in that cannot trigger when the label is applied' {
        $fixtureRoot = Join-Path $TestDrive 'opt-in-without-label-event'
        New-WorkflowFixture -Root $fixtureRoot -Workflows @{
            'auto-merge.yml' = @'
name: Auto Merge
on:
  pull_request_target:
    types: [opened]
jobs:
  enable:
    if: contains(github.event.pull_request.labels.*.name, 'automerge:approved')
    runs-on: ubuntu-latest
    steps:
      - run: gh pr merge --auto --merge "$PR_URL"
'@
            'test.yml' = "name: Test`non:`n  push:`n    branches: [main]"
            'security.yml' = "name: Security Scans`non:`n  push:`n    branches: [main]"
            'release.yml' = "name: Release`non:`n  push:`n    branches: [main]"
        }

        { Invoke-WorkflowPolicy -Root $fixtureRoot } | Should -Throw '*must listen for labeled events*'
    }

    It 'rejects post-merge workflows that do not run on main pushes' {
        $fixtureRoot = Join-Path $TestDrive 'missing-release-push'
        New-WorkflowFixture -Root $fixtureRoot -Workflows @{
            'auto-merge.yml' = @'
name: Auto Merge
on:
  pull_request_target:
jobs:
  enable:
    if: github.actor == 'dependabot[bot]'
    runs-on: ubuntu-latest
    steps:
      - run: gh pr merge --auto --merge "$PR_URL"
'@
            'test.yml' = "name: Test`non:`n  push:`n    branches: [main]"
            'security.yml' = "name: Security Scans`non:`n  push:`n    branches: [main]"
            'release.yml' = "name: Release`non:`n  workflow_dispatch:"
        }

        { Invoke-WorkflowPolicy -Root $fixtureRoot } | Should -Throw '*must run on pushes to main*'
    }
}
