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

    It 'rejects a publishing release job that does not depend on verified CI checks' {
        $fixtureRoot = Join-Path $TestDrive 'ungated-release-publication'
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
            'release.yml' = @'
name: Release
on:
  push:
    branches: [main]
jobs:
  emergency-publish:
    runs-on: ubuntu-latest
    steps:
      - run: gh release create v0.1.1
'@
        }

        { Invoke-WorkflowPolicy -Root $fixtureRoot } | Should -Throw '*must depend on verify-required-ci-checks*'
    }

    It 'rejects release gates that accept a same-name GitHub Actions check without workflow and job provenance' {
        $fixtureRoot = Join-Path $TestDrive 'spoofable-release-check'
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
            'release.yml' = @'
name: Release
on:
  push:
    branches: [main]
jobs:
  verify-required-ci-checks:
    runs-on: ubuntu-latest
    steps:
      - shell: pwsh
        run: |
          $requiredChecks = @('dotnet')
          $latestByName = @{}
          if ($check.app.slug -ne 'github-actions' -or $check.details_url -notmatch '^https://github\.com/[^/]+/[^/]+/actions/runs/\d+/job/\d+$') { throw 'untrusted-provenance' }
  prerelease:
    needs: [verify-required-ci-checks]
    runs-on: ubuntu-latest
    steps:
      - run: gh release create v0.1.1
'@
        }

        { Invoke-WorkflowPolicy -Root $fixtureRoot } | Should -Throw '*workflow and job provenance*'
    }

    It 'gates prerelease and stable publication on the complete verified CI check set' {
        $releaseWorkflow = Get-Content -Path (Join-Path $PSScriptRoot '../.github/workflows/release.yml') -Raw

        $releaseWorkflow | Should -Match '(?ms)^  verify-required-ci-checks:.*?''dotnet''.*?''GitHub Workflow Security Policy''.*?''Semgrep Security SAST''.*?''Gitleaks Secret Scan''.*?''Trivy Repository Scan''.*?''Analyze \(csharp, none\)''.*?''Analyze \(javascript-typescript, none\)'''
        $releaseWorkflow | Should -Match '(?ms)^  prerelease:.*?^    needs: \[select-runner, verify-required-ci-checks\]'
        $releaseWorkflow | Should -Match '(?ms)^  stable-release:.*?^    needs: \[select-runner, verify-required-ci-checks\]'
        $releaseWorkflow | Should -Match '\$check\.app\.slug -ne ''github-actions'''
        $releaseWorkflow | Should -Match '\$check\.details_url -notmatch'
    }

    It 'rejects same-name checks unless their workflow and job provenance matches the release policy' {
        $releaseWorkflow = Get-Content -Path (Join-Path $PSScriptRoot '../.github/workflows/release.yml') -Raw

        $releaseWorkflow | Should -Match "(?ms)checkName\s*=\s*'dotnet'.*?workflowFile\s*=\s*'test\.yml'.*?jobName\s*=\s*'dotnet'"
        $releaseWorkflow | Should -Match "(?ms)checkName\s*=\s*'GitHub Workflow Security Policy'.*?workflowFile\s*=\s*'security\.yml'.*?jobName\s*=\s*'GitHub Workflow Security Policy'"
        $releaseWorkflow | Should -Match "(?ms)checkName\s*=\s*'Semgrep Security SAST'.*?workflowFile\s*=\s*'security\.yml'.*?jobName\s*=\s*'Semgrep Security SAST'"
        $releaseWorkflow | Should -Match "(?ms)checkName\s*=\s*'Gitleaks Secret Scan'.*?workflowFile\s*=\s*'security\.yml'.*?jobName\s*=\s*'Gitleaks Secret Scan'"
        $releaseWorkflow | Should -Match "(?ms)checkName\s*=\s*'Trivy Repository Scan'.*?workflowFile\s*=\s*'security\.yml'.*?jobName\s*=\s*'Trivy Repository Scan'"
        $releaseWorkflow | Should -Match "(?ms)checkName\s*=\s*'Analyze \(csharp, none\)'.*?workflowFile\s*=\s*'codeql\.yml'.*?jobName\s*=\s*'Analyze \(csharp, none\)'"
        $releaseWorkflow | Should -Match "(?ms)checkName\s*=\s*'Analyze \(javascript-typescript, none\)'.*?workflowFile\s*=\s*'codeql\.yml'.*?jobName\s*=\s*'Analyze \(javascript-typescript, none\)'"

        $releaseWorkflow | Should -Match 'actions/workflows/\$workflowFile'
        $releaseWorkflow | Should -Match '\$run\.workflow_id -ne \$workflowIds\[\$requiredCheck\.workflowFile\]'
        $releaseWorkflow | Should -Match '\$job\.id -eq \[Int64\]\$jobId -and \$job\.name -eq \$requiredCheck\.jobName'
        $releaseWorkflow | Should -Match '(?s)\$matchingChecks\.Count -eq 0.*?\$incompleteChecks \+= "\$\(\$requiredCheck\.checkName\)=missing"'
        $releaseWorkflow | Should -Not -Match '\$latestByName'
    }

    It 'keeps untrusted same-name checks pending until the provenance deadline' {
            $releaseWorkflow = Get-Content -Path (Join-Path $PSScriptRoot '../.github/workflows/release.yml') -Raw

            $releaseWorkflow | Should -Match '\$untrustedSameName = @\{\}'
            $releaseWorkflow | Should -Match '(?s)if \(\$null -eq \$trustedCheck\) \{.*?\$incompleteChecks \+= "\$\(\$requiredCheck\.checkName\)=untrusted-provenance".*?\$untrustedSameName\[.*?\] = @\('
            $releaseWorkflow | Should -Match '(?s)if \(\(Get-Date\) -ge \$deadline\) \{.*?\$failedChecks \+= "\$untrustedName=untrusted-provenance"'
            $releaseWorkflow | Should -Not -Match '\$latestByName'
            $releaseWorkflow | Should -Not -Match '\$incompleteChecks \+= "\$\( \$requiredCheck\.checkName\)=untrusted-provenance"'
        }

    It 'gates Azure DevOps packaging on SonarQube Community Build analysis' {
        $pipeline = Get-Content -Path (Join-Path $PSScriptRoot '../azure-pipelines.deploy.yml') -Raw

        $pipeline | Should -Match '(?m)^\s*sonarQubeServiceConnection:\s*SonarQube\s*$'
        $pipeline | Should -Match '(?m)^\s*sonarProjectKey:\s*0Downtime_sf-ad-sync\s*$'
        $pipeline | Should -Match '(?ms)SonarQubePrepare@8.*?scannerMode:\s*dotnet.*?projectKey:\s*\$\(sonarProjectKey\)'
        $pipeline | Should -Match 'sonar\.cs\.opencover\.reportsPaths='
        $pipeline | Should -Match 'sonar\.javascript\.lcov\.reportPaths='
        $pipeline | Should -Match '(?ms)SonarQubeAnalyze@8.*?SonarQubePublish@8.*?Publish applications'
        $pipeline | Should -Not -Match '(?i)SONAR_TOKEN|sonar\.token'
    }
}
