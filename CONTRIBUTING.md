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
3. Run the primary validation entrypoint when practical:

```powershell
pwsh ./scripts/Validate-SyncFactors.ps1
```

4. For focused .NET-only changes, the minimum local check is:

```powershell
dotnet test ./SyncFactors.Next.sln
```

5. If your change affects the operator browser bundle, run the frontend checks:

```powershell
cd ./src/SyncFactors.Api
npm ci --ignore-scripts
npm run test:ui
npm run build:ui
```

6. If your change affects security scanning behavior, run the local repository scan:

```bash
trivy fs --severity HIGH,CRITICAL --ignore-unfixed --scanners vuln,secret,misconfig .
```

## Pull Request Expectations
- Describe the problem and the user-visible impact.
- Call out config or rollout implications.
- Include test coverage for behavior changes when practical.
- Keep sample values obviously fake.

## Coding Notes
- Prefer environment-backed secrets over plaintext sample values.
- Avoid destructive Active Directory behavior changes without clear tests and documentation.
- Keep live-write controls behind the shared effective write settings so deployment-level dry-run mode remains stronger than ordinary sync config.
- Add explicit `Compile`, `Content`, or Razor entries when adding files to projects that disable default item inclusion.
