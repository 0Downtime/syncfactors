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
3. Run the primary .NET test suite:

```powershell
dotnet test ./SyncFactors.Next.sln
```

4. If your change affects security scanning behavior, run the local repository scan:

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
