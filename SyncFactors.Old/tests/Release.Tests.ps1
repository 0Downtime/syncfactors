Describe 'Get-SyncFactorsReleaseInfo' {
    It 'builds prerelease metadata from the base version, run number, and commit sha' {
        $versionPath = Join-Path $TestDrive 'VERSION'
        '0.1.0' | Set-Content -Path $versionPath

        $info = & "$PSScriptRoot/../scripts/Get-SyncFactorsReleaseInfo.ps1" `
            -Channel Prerelease `
            -VersionPath $versionPath `
            -RunNumber 42 `
            -CommitSha 'ABCDEF1234567890'

        $info.baseVersion | Should -Be '0.1.0'
        $info.version | Should -Be '0.1.0-dev.42+sha.abcdef1'
        $info.tag | Should -Be 'v0.1.0-dev.42+sha.abcdef1'
        $info.isPrerelease | Should -BeTrue
        $info.commitSha | Should -Be 'abcdef1234567890'
    }

    It 'builds stable release metadata when the requested version matches the VERSION file' {
        $versionPath = Join-Path $TestDrive 'VERSION'
        '0.2.0' | Set-Content -Path $versionPath

        $info = & "$PSScriptRoot/../scripts/Get-SyncFactorsReleaseInfo.ps1" `
            -Channel Stable `
            -VersionPath $versionPath `
            -Version '0.2.0' `
            -CommitSha '0123456789abcdef'

        $info.version | Should -Be '0.2.0'
        $info.tag | Should -Be 'v0.2.0'
        $info.isPrerelease | Should -BeFalse
    }

    It 'rejects stable release versions that do not match the VERSION file' {
        $versionPath = Join-Path $TestDrive 'VERSION'
        '0.2.0' | Set-Content -Path $versionPath

        {
            & "$PSScriptRoot/../scripts/Get-SyncFactorsReleaseInfo.ps1" `
                -Channel Stable `
                -VersionPath $versionPath `
                -Version '0.3.0' `
                -CommitSha '0123456789abcdef'
        } | Should -Throw '*must match the VERSION file value*'
    }
}

Describe 'New-SyncFactorsReleaseBundle' {
    It 'packages only the deployment bundle contents' {
        $repoRoot = Join-Path $TestDrive 'repo'
        $outputPath = Join-Path $TestDrive 'syncfactors-0.1.0.zip'

        foreach ($path in @('src', 'scripts', 'config', '.github', 'tests')) {
            New-Item -Path (Join-Path $repoRoot $path) -ItemType Directory -Force | Out-Null
        }

        'module content' | Set-Content -Path (Join-Path $repoRoot 'src/module.psm1')
        'script content' | Set-Content -Path (Join-Path $repoRoot 'scripts/run.ps1')
        'config content' | Set-Content -Path (Join-Path $repoRoot 'config/sample.json')
        'workflow content' | Set-Content -Path (Join-Path $repoRoot '.github/workflows.yml')
        'test content' | Set-Content -Path (Join-Path $repoRoot 'tests/release.tests.ps1')
        'readme' | Set-Content -Path (Join-Path $repoRoot 'README.md')
        'license' | Set-Content -Path (Join-Path $repoRoot 'LICENSE')
        'security' | Set-Content -Path (Join-Path $repoRoot 'SECURITY.md')
        'contributing' | Set-Content -Path (Join-Path $repoRoot 'CONTRIBUTING.md')

        $bundle = & "$PSScriptRoot/../scripts/New-SyncFactorsReleaseBundle.ps1" -RepoRoot $repoRoot -OutputPath $outputPath

        $bundle.bundlePath | Should -Be ([System.IO.Path]::GetFullPath($outputPath))
        Test-Path -Path $outputPath -PathType Leaf | Should -BeTrue

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [System.IO.Compression.ZipFile]::OpenRead($outputPath)
        try {
            $entries = @($archive.Entries.FullName)
            $entries | Should -Contain 'src/module.psm1'
            $entries | Should -Contain 'scripts/run.ps1'
            $entries | Should -Contain 'config/sample.json'
            $entries | Should -Contain 'README.md'
            $entries | Should -Contain 'LICENSE'
            $entries | Should -Contain 'SECURITY.md'
            $entries | Should -Contain 'CONTRIBUTING.md'
            @($entries | Where-Object { $_ -like '.github/*' }).Count | Should -Be 0
            @($entries | Where-Object { $_ -like 'tests/*' }).Count | Should -Be 0
        } finally {
            $archive.Dispose()
        }
    }
}
