Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Describe 'Production bootstrap safety policy' {
    BeforeAll {
        $repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).ProviderPath
    }

    It 'defaults API and worker runtime settings to dry-run-only' {
        foreach ($relativePath in @(
            'src/SyncFactors.Api/Program.cs',
            'src/SyncFactors.Worker/Program.cs'
        )) {
            $program = Get-Content -Path (Join-Path $repositoryRoot $relativePath) -Raw
            $program | Should -Match '(?s)GetValue<bool\?>\("SyncFactors:Runtime:DryRunOnly"\)\s*\?\?\s*true'
        }

        $appSettings = Get-Content -Path (Join-Path $repositoryRoot 'src/SyncFactors.Api/appsettings.json') -Raw | ConvertFrom-Json
        $appSettings.SyncFactors.Runtime.DryRunOnly | Should -BeTrue
    }

    It 'ships the real production sample with live writes disabled and conservative approval gates' {
        $sample = Get-Content -Path (Join-Path $repositoryRoot 'config/sample.real-successfactors.real-ad.sync-config.json') -Raw | ConvertFrom-Json

        $sample.sync.realSyncEnabled | Should -BeFalse
        $sample.safety.maxCreatesPerRun | Should -Be 25
        $sample.safety.maxDisablesPerRun | Should -Be 10
        $sample.safety.maxDeletionsPerRun | Should -Be 5
        $sample.approval.enabled | Should -BeTrue
        @($sample.approval.requireFor) | Should -Be @('DisableUser', 'DeleteUser', 'MoveToGraveyardOu')
    }

    It 'requires and preserves a secret audit-integrity key before installing Production services' {
        $installerPath = Join-Path $repositoryRoot 'scripts/Install-SyncFactorsWindowsServices.ps1'
        $tokens = $null
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($installerPath, [ref]$tokens, [ref]$parseErrors)
        @($parseErrors) | Should -BeNullOrEmpty

        $parameters = @{}
        foreach ($parameter in $ast.ParamBlock.Parameters) {
            $parameters[$parameter.Name.VariablePath.UserPath] = $parameter.StaticType.FullName
        }
        $parameters.SecurityAuditIntegrityKey | Should -Be ([Security.SecureString].FullName)
        $parameters.EnableLiveWrites | Should -Be ([switch].FullName)

        $installer = Get-Content -Path $installerPath -Raw
        $existingLookup = $installer.IndexOf("Get-ServiceEnvironmentValue -ServiceNames @(`$ApiServiceName, `$WorkerServiceName) -Name 'SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY'", [StringComparison]::Ordinal)
        $environmentLookup = $installer.IndexOf('$env:SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY', [StringComparison]::Ordinal)
        $requiredCheck = $installer.IndexOf("throw 'A security audit integrity key is required before Production services can be installed.'", [StringComparison]::Ordinal)

        $existingLookup | Should -BeGreaterThan -1
        $environmentLookup | Should -BeGreaterThan $existingLookup
        $requiredCheck | Should -BeGreaterThan $environmentLookup
        $installer | Should -Match '"SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY=\$securityAuditIntegrityKeyPlainText"'
        $installer | Should -Match '"SyncFactors__Runtime__DryRunOnly=\$dryRunOnly"'
        $installer | Should -Not -Match '(?i)securityAuditIntegrityKey\s*='
    }

    It 'passes the audit-integrity key through deployment before Production services start' {
        $patchScript = Get-Content -Path (Join-Path $repositoryRoot 'scripts/Deploy-SyncFactorsWindowsPatch.ps1') -Raw
        $patchScript | Should -Match '\[Security\.SecureString\]\$SecurityAuditIntegrityKey'
        $patchScript | Should -Match '@\(''-SecurityAuditIntegrityKey'', \$SecurityAuditIntegrityKey\)'

        $pipeline = Get-Content -Path (Join-Path $repositoryRoot 'azure-pipelines.deploy.yml') -Raw
        $pipeline | Should -Match '(?m)^  securityAuditIntegrityKey: ''''\s*$'
        $pipeline | Should -Match '(?m)^  enableLiveWrites: false\s*$'
        $pipeline | Should -Match '(?s)if \(-not \[string\]::IsNullOrWhiteSpace\(\$securityAuditIntegrityKeyValue\)\) \{\s*\$env:SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY = \$securityAuditIntegrityKeyValue\s*\}'
        $pipeline | Should -Match '(?s)finally \{\s*\$env:SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY = \$previousIntegrityKey\s*\$securityAuditIntegrityKeyValue = \$null\s*\}'
        $pipeline | Should -Not -Match "@\('-SecurityAuditIntegrityKey',"
        $pipeline | Should -Not -Match 'securityAuditIntegrityKey must be set for a fresh Production service installation'
        $pipeline | Should -Match '\$installArgs \+= ''-EnableLiveWrites'''

        $keyInput = $pipeline.IndexOf("`$securityAuditIntegrityKeyValue = '`$(securityAuditIntegrityKey)'", [StringComparison]::Ordinal)
        $installInvocation = $pipeline.IndexOf('& $pwsh -NoProfile -ExecutionPolicy Bypass -File $installScript @installArgs', [StringComparison]::Ordinal)
        $serviceStart = $pipeline.IndexOf('Start-Service -Name $apiServiceName', [StringComparison]::Ordinal)
        $keyInput | Should -BeGreaterThan -1
        $installInvocation | Should -BeGreaterThan $keyInput
        $serviceStart | Should -BeGreaterThan $installInvocation
    }

    It 'documents secret-safe key provisioning and the explicit live-write opt-in' {
        $readme = Get-Content -Path (Join-Path $repositoryRoot 'README.md') -Raw

        $readme | Should -Match '\$integrityKey = Read-Host ''SyncFactors security audit integrity key'' -AsSecureString'
        $readme | Should -Match '-SecurityAuditIntegrityKey \$integrityKey'
        $readme | Should -Match '`-EnableLiveWrites`'
        $readme | Should -Match '`securityAuditIntegrityKey`.*Mark it secret'
        $readme | Should -Match 'existing service environment.*preserv'
    }
}
