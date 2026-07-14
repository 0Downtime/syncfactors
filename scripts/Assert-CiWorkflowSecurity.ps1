 [CmdletBinding()]
param(
    [string] $RepositoryRoot = (Join-Path $PSScriptRoot '..')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path $RepositoryRoot).Path
$errors = [System.Collections.Generic.List[string]]::new()

function Add-PolicyError {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $Message
    )

    $relativePath = [System.IO.Path]::GetRelativePath($repoRoot, $Path)
    $errors.Add("${relativePath}: ${Message}")
}

function Test-CheckoutCredentialPersistence {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo] $File,

        [string[]] $Lines,

        [Parameter(Mandatory)]
        [string] $CheckoutPattern,

        [Parameter(Mandatory)]
        [string] $CredentialPattern,

        [Parameter(Mandatory)]
        [string] $Message
    )

    for ($lineIndex = 0; $lineIndex -lt $Lines.Count; $lineIndex++) {
        $line = $Lines[$lineIndex]
        if ($line -match $CheckoutPattern) {
            $window = ($Lines[$lineIndex..([Math]::Min($lineIndex + 8, $Lines.Count - 1))] -join "`n")
            if ($window -notmatch $CredentialPattern) {
                Add-PolicyError $File.FullName "line $($lineIndex + 1): $Message"
            }
        }
    }
}

function Test-NpmInstallHardening {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo] $File,

        [string[]] $Lines
    )

    for ($lineIndex = 0; $lineIndex -lt $Lines.Count; $lineIndex++) {
        $line = $Lines[$lineIndex]
        if ($line -match '\bnpm\s+(?:ci|install)\b' -and $line -notmatch '--ignore-scripts') {
            Add-PolicyError $File.FullName "line $($lineIndex + 1): npm install commands in CI must include --ignore-scripts."
        }
    }
}

$githubWorkflowRoot = Join-Path $repoRoot '.github/workflows'
if (Test-Path -Path $githubWorkflowRoot -PathType Container) {
    $githubWorkflowFiles = Get-ChildItem -Path (Join-Path $githubWorkflowRoot '*') -File -Include '*.yml', '*.yaml'
    foreach ($workflowFile in $githubWorkflowFiles) {
        $content = Get-Content -Path $workflowFile.FullName -Raw
        $lines = @(Get-Content -Path $workflowFile.FullName)
        $hasPullRequest = $content -match '(?m)^\s+pull_request:\s*$|^\s+pull_request\s*$'
        $hasPullRequestTarget = $content -match '(?m)^\s+pull_request_target:\s*$|^\s+pull_request_target\s*$'

        if ($hasPullRequestTarget) {
            if ($content -match 'actions/checkout@') {
                Add-PolicyError $workflowFile.FullName 'pull_request_target workflows must not check out repository code.'
            }

            if ($content -match 'github\.event\.pull_request\.head\.(ref|repo\.clone_url|repo\.ssh_url)') {
                Add-PolicyError $workflowFile.FullName 'pull_request_target workflows must not reference attacker-controlled PR head refs or clone URLs.'
            }

            if ($content -match 'secrets\.') {
                Add-PolicyError $workflowFile.FullName 'pull_request_target workflows must not expose repository secrets.'
            }
        }

        if ($workflowFile.Name -match '^auto-merge\.ya?ml$' -and $content -match '\bgh\s+pr\s+merge\s+--auto\b') {
            $hasTrustedBotGuard = $content -match "github\.actor\s*==\s*'dependabot\[bot\]'"
            $hasReviewedOptInGuard = $content -match "contains\(\s*github\.event\.pull_request\.labels\.\*\.name\s*,\s*'automerge:approved'\s*\)"
            if (-not ($hasTrustedBotGuard -or $hasReviewedOptInGuard)) {
                Add-PolicyError $workflowFile.FullName 'automatic merge must be limited to a trusted bot or an explicit reviewed opt-in.'
            }

            $hasLabeledTrigger = $content -match '(?m)^\s*-\s*labeled\s*$|^\s*types:\s*\[[^\]]*\blabeled\b[^\]]*\]\s*$'
            if ($hasReviewedOptInGuard -and -not $hasLabeledTrigger) {
                Add-PolicyError $workflowFile.FullName 'reviewed auto-merge opt-ins must listen for labeled events.'
            }
        }

        if ($workflowFile.Name -match '^(test|security|release)\.ya?ml$') {
            $runsOnMainPush = $content -match '(?ms)^\s*push:\s*(?:\r?\n\s*branches:\s*(?:\r?\n\s*-\s*main\b|\[\s*main\s*\]))'
            if (-not $runsOnMainPush) {
                Add-PolicyError $workflowFile.FullName 'must run on pushes to main so merged changes are tested, scanned, and released.'
            }
        }

        if ($hasPullRequest -and $content -match 'self-hosted') {
            $guardsExternalForks =
                $content -match 'github\.event\.pull_request\.head\.repo\.full_name\s*(?:!=|==)\s*github\.repository' -or
                $content -match 'github\.event\.pull_request\.head\.repo\.fork\s*==\s*false' -or
                ($content -match 'github\.event\.pull_request\.head\.repo\.full_name' -and $content -match '\$PR_HEAD_REPOSITORY"\s*!=\s*"\$REPO')

            if (-not $guardsExternalForks) {
                Add-PolicyError $workflowFile.FullName 'pull_request workflows using self-hosted runners must route external fork PRs to GitHub-hosted runners.'
            }
        }

        Test-NpmInstallHardening -File $workflowFile -Lines $lines
        Test-CheckoutCredentialPersistence `
            -File $workflowFile `
            -Lines $lines `
            -CheckoutPattern 'uses:\s+actions/checkout@' `
            -CredentialPattern 'persist-credentials:\s*false' `
            -Message 'actions/checkout steps must set persist-credentials: false.'
    }
}

$azurePipelineFiles = Get-ChildItem -Path (Join-Path $repoRoot '*') -File -Include 'azure-pipelines*.yml', 'azure-pipelines*.yaml'
foreach ($pipelineFile in $azurePipelineFiles) {
    $content = Get-Content -Path $pipelineFile.FullName -Raw
    $lines = @(Get-Content -Path $pipelineFile.FullName)

    Test-NpmInstallHardening -File $pipelineFile -Lines $lines
    Test-CheckoutCredentialPersistence `
        -File $pipelineFile `
        -Lines $lines `
        -CheckoutPattern '^\s*-\s*checkout:\s*self\s*$' `
        -CredentialPattern 'persistCredentials:\s*false' `
        -Message 'Azure DevOps checkout steps must set persistCredentials: false.'

    $isDeploymentPipeline = $content -match '(?m)^\s*-\s*stage:\s*Deploy\s*$|^\s*-\s*deployment:\s*'
    if ($isDeploymentPipeline) {
        if ($content -notmatch '(?m)^\s*pr:\s*none\s*$') {
            Add-PolicyError $pipelineFile.FullName 'deployment pipelines must not run automatically for pull requests.'
        }

        if ($content -notmatch "variables\['Build\.Reason'\].*'PullRequest'") {
            Add-PolicyError $pipelineFile.FullName 'deployment stages must explicitly block Build.Reason PullRequest.'
        }
    }

    if ($content -match 'System\.AccessToken' -and $content -notmatch '(?m)^\s*pr:\s*none\s*$') {
        Add-PolicyError $pipelineFile.FullName 'pipelines using System.AccessToken must not run automatically for pull requests.'
    }
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Host "ERROR: $_" }
    exit 1
}

Write-Host 'CI workflow security policy passed.'
