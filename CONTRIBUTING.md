# Contributing

Thanks for contributing to `syncfactors`.

## Before You Open A Change
- Open an issue first for behavior changes, larger refactors, or new features.
- Keep pull requests focused on one concern.
- Update documentation and sample config when behavior changes.
- Do not include real tenant data, credentials, or directory exports in commits.

## Development Workflow
1. Create a branch from `main`.
2. Make your changes.
3. Run the local test suite:

```powershell
pwsh ./scripts/Invoke-TestSuite.ps1 -Detailed -Coverage
```

4. If you changed PowerShell modules or scripts, run static analysis:

```powershell
$paths = @('./src', './scripts')
foreach ($path in $paths) {
  Invoke-ScriptAnalyzer -Path $path -Recurse -Settings ./PSScriptAnalyzerSettings.psd1 -Severity Error,Warning
}
```

5. If your change affects security scanning behavior, run the local repository scan:

```bash
trivy fs --severity HIGH,CRITICAL --ignore-unfixed --scanners vuln,secret,misconfig .
```

## Pull Request Expectations
- Describe the problem and the user-visible impact.
- Call out config or rollout implications.
- Include test coverage for behavior changes when practical.
- Keep sample values obviously fake.

## Coding Notes
- PowerShell code should stay compatible with the versions exercised by CI.
- Prefer environment-backed secrets over plaintext sample values.
- Avoid destructive Active Directory behavior changes without clear tests and documentation.
