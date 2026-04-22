using System.Diagnostics;
using System.Text.Json;

namespace SyncFactors.Api.Tests;

public sealed class LocalConfigFormatScriptTests
{
    [Fact]
    public async Task UpdateLocalConfigScript_RewritesSyncConfigShapeWithoutOverwritingValueLists()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("syncfactors-local-config-sync");
        try
        {
            var samplePath = Path.Combine(tempDirectory.FullName, "sample.sync-config.json");
            var localPath = Path.Combine(tempDirectory.FullName, "local.sync-config.json");

            await File.WriteAllTextAsync(samplePath,
                """
                {
                  "successFactors": {
                    "baseUrl": "https://sample.test/odata/v2",
                    "query": {
                      "select": ["userId", "jobTitle"],
                      "expand": ["companyNav"],
                      "inactiveStatusValues": ["T"]
                    }
                  },
                  "ad": {
                    "transport": {
                      "trustedCertificateThumbprints": ["AAA111"]
                    },
                    "ouRoutingRules": [
                      {
                        "match": {
                          "company": "CORP",
                          "department": "IT"
                        },
                        "targetOu": "OU=IT,DC=sample,DC=test",
                        "enabled": true
                      },
                      {
                        "match": {
                          "company": "CORP",
                          "department": "HR"
                        },
                        "targetOu": "OU=HR,DC=sample,DC=test",
                        "enabled": true
                      }
                    ],
                    "licensingGroups": ["CN=Sample-Group,DC=sample,DC=test"]
                  },
                  "sync": {
                    "leaveStatusValues": ["LOA"],
                    "autoDeleteFromGraveyard": false
                  },
                  "approval": {
                    "requireFor": ["DisableUser", "DeleteUser"]
                  },
                  "alerts": {
                    "smtp": {
                      "to": ["ops@sample.test"]
                    }
                  }
                }
                """);

            await File.WriteAllTextAsync(localPath,
                """
                {
                  "obsoleteRootKey": true,
                  "successFactors": {
                    "baseUrl": "https://local.test/odata/v2",
                    "query": {
                      "select": ["jobTitle", "userId", "unexpectedField"],
                      "expand": ["companyNav", "costCenterNav"],
                      "inactiveStatusValues": ["A", "T"]
                    }
                  },
                  "ad": {
                    "transport": {
                      "trustedCertificateThumbprints": ["LOCAL111", "LOCAL222"]
                    },
                    "ouRoutingRules": [
                      {
                        "match": {
                          "department": "IT",
                          "company": "CORP"
                        },
                        "targetOu": "OU=LocalIT,DC=local,DC=test",
                        "enabled": false,
                        "obsolete": "remove-me"
                      },
                      {
                        "match": {
                          "company": "CORP",
                          "department": "Finance"
                        },
                        "targetOu": "OU=Finance,DC=local,DC=test",
                        "enabled": false
                      }
                    ],
                    "licensingGroups": ["CN=Local-Only,DC=local,DC=test"]
                  },
                  "sync": {
                    "leaveStatusValues": ["SABBATICAL"]
                  },
                  "approval": {
                    "requireFor": ["DeleteUser", "DisableUser", "MoveToGraveyardOu"]
                  },
                  "alerts": {
                    "smtp": {
                      "to": ["local-ops@local.test"]
                    }
                  }
                }
                """);

            var result = await InvokePowerShellFileAsync(
                workingDirectory: GetRepositoryRoot(),
                filePath: GetRepositoryFile("scripts/Update-LocalSyncFactorsConfig.ps1"),
                "-LocalPath", localPath,
                "-SamplePath", samplePath,
                "-NoBackup");

            Assert.Equal(0, result.ExitCode);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(localPath));
            var root = document.RootElement;

            Assert.False(root.TryGetProperty("obsoleteRootKey", out _));
            Assert.Equal("https://local.test/odata/v2", root.GetProperty("successFactors").GetProperty("baseUrl").GetString());
            Assert.Equal(
                ["userId", "jobTitle"],
                root.GetProperty("successFactors").GetProperty("query").GetProperty("select").EnumerateArray().Select(item => item.GetString()!).ToArray());
            Assert.Equal(
                ["companyNav"],
                root.GetProperty("successFactors").GetProperty("query").GetProperty("expand").EnumerateArray().Select(item => item.GetString()!).ToArray());
            Assert.Equal(
                ["T"],
                root.GetProperty("successFactors").GetProperty("query").GetProperty("inactiveStatusValues").EnumerateArray().Select(item => item.GetString()!).ToArray());
            Assert.Equal(
                ["LOCAL111", "LOCAL222"],
                root.GetProperty("ad").GetProperty("transport").GetProperty("trustedCertificateThumbprints").EnumerateArray().Select(item => item.GetString()!).ToArray());
            Assert.Equal(
                ["CN=Local-Only,DC=local,DC=test"],
                root.GetProperty("ad").GetProperty("licensingGroups").EnumerateArray().Select(item => item.GetString()!).ToArray());
            Assert.Equal(
                ["SABBATICAL"],
                root.GetProperty("sync").GetProperty("leaveStatusValues").EnumerateArray().Select(item => item.GetString()!).ToArray());
            Assert.False(root.GetProperty("sync").GetProperty("autoDeleteFromGraveyard").GetBoolean());
            Assert.Equal(
                ["DisableUser", "DeleteUser"],
                root.GetProperty("approval").GetProperty("requireFor").EnumerateArray().Select(item => item.GetString()!).ToArray());
            Assert.Equal(
                ["local-ops@local.test"],
                root.GetProperty("alerts").GetProperty("smtp").GetProperty("to").EnumerateArray().Select(item => item.GetString()!).ToArray());

            var routingRules = root.GetProperty("ad").GetProperty("ouRoutingRules").EnumerateArray().ToArray();
            Assert.Equal(2, routingRules.Length);
            Assert.Equal("IT", routingRules[0].GetProperty("match").GetProperty("department").GetString());
            Assert.Equal("OU=LocalIT,DC=local,DC=test", routingRules[0].GetProperty("targetOu").GetString());
            Assert.False(routingRules[0].GetProperty("enabled").GetBoolean());
            Assert.False(routingRules[0].TryGetProperty("obsolete", out _));
            Assert.Equal("HR", routingRules[1].GetProperty("match").GetProperty("department").GetString());
            Assert.Equal("OU=HR,DC=sample,DC=test", routingRules[1].GetProperty("targetOu").GetString());
            Assert.True(routingRules[1].GetProperty("enabled").GetBoolean());
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task UpdateLocalConfigScript_RewritesMappingConfigByTargetIdentity()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("syncfactors-local-mapping-sync");
        try
        {
            var samplePath = Path.Combine(tempDirectory.FullName, "sample.mapping-config.json");
            var localPath = Path.Combine(tempDirectory.FullName, "local.mapping-config.json");

            await File.WriteAllTextAsync(samplePath,
                """
                {
                  "mappings": [
                    {
                      "source": "personIdExternal",
                      "target": "employeeID",
                      "enabled": true,
                      "required": true,
                      "transform": "Trim"
                    },
                    {
                      "source": "jobTitle",
                      "target": "title",
                      "enabled": true,
                      "required": false,
                      "transform": "Trim"
                    }
                  ]
                }
                """);

            await File.WriteAllTextAsync(localPath,
                """
                {
                  "mappings": [
                    {
                      "source": "legacyTitle",
                      "target": "title",
                      "enabled": false,
                      "required": false,
                      "transform": "Lower",
                      "obsolete": "remove-me"
                    },
                    {
                      "source": "legacyEmployeeId",
                      "target": "employeeID",
                      "enabled": false,
                      "required": false,
                      "transform": "Lower"
                    },
                    {
                      "source": "legacyValue",
                      "target": "obsoleteTarget",
                      "enabled": true,
                      "required": false,
                      "transform": "Trim"
                    }
                  ]
                }
                """);

            var result = await InvokePowerShellFileAsync(
                workingDirectory: GetRepositoryRoot(),
                filePath: GetRepositoryFile("scripts/Update-LocalSyncFactorsConfig.ps1"),
                "-LocalPath", localPath,
                "-SamplePath", samplePath,
                "-NoBackup");

            Assert.Equal(0, result.ExitCode);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(localPath));
            var mappings = document.RootElement.GetProperty("mappings").EnumerateArray().ToArray();
            Assert.Equal(2, mappings.Length);
            Assert.Equal("employeeID", mappings[0].GetProperty("target").GetString());
            Assert.Equal("legacyEmployeeId", mappings[0].GetProperty("source").GetString());
            Assert.False(mappings[0].GetProperty("enabled").GetBoolean());
            Assert.False(mappings[0].GetProperty("required").GetBoolean());
            Assert.Equal("Lower", mappings[0].GetProperty("transform").GetString());

            Assert.Equal("title", mappings[1].GetProperty("target").GetString());
            Assert.Equal("legacyTitle", mappings[1].GetProperty("source").GetString());
            Assert.False(mappings[1].GetProperty("enabled").GetBoolean());
            Assert.Equal("Lower", mappings[1].GetProperty("transform").GetString());
            Assert.False(mappings[1].TryGetProperty("obsolete", out _));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TestConfigDrift_DoesNotRewriteUntilSyncRuns()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("syncfactors-config-drift-check");
        try
        {
            var samplePath = Path.Combine(tempDirectory.FullName, "sample.json");
            var localPath = Path.Combine(tempDirectory.FullName, "local.json");
            const string originalLocalJson =
                """
                {
                  "git": {
                    "pullBeforeStackStart": false,
                    "obsolete": true
                  }
                }
                """;

            await File.WriteAllTextAsync(samplePath,
                """
                {
                  "git": {
                    "pullBeforeStackStart": true
                  }
                }
                """);
            await File.WriteAllTextAsync(localPath, originalLocalJson);

            var command = string.Join(
                Environment.NewLine,
                $"Set-Location '{GetRepositoryRoot().Replace("'", "''")}'",
                $". '{GetRepositoryFile("scripts/Sync-LocalConfigFormat.ps1").Replace("'", "''")}'",
                $"$status = Test-ConfigDrift -SampleConfigPath '{samplePath.Replace("'", "''")}' -LocalConfigPath '{localPath.Replace("'", "''")}'",
                "$status | Select-Object Drifted | ConvertTo-Json -Compress");

            var result = await InvokePowerShellCommandAsync(GetRepositoryRoot(), command);
            Assert.Equal(0, result.ExitCode);

            using var document = JsonDocument.Parse(result.StandardOutput);
            Assert.True(document.RootElement.GetProperty("Drifted").GetBoolean());
            Assert.Equal(originalLocalJson, await File.ReadAllTextAsync(localPath));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task UpdateLocalConfigScript_WithoutArguments_SyncsTrackedConfigsForRepoLayout()
    {
        var tempRepo = await CreateMinimalScriptRepoAsync();
        try
        {
            var configRoot = Path.Combine(tempRepo.FullName, "config");
            var localCodexRunPath = Path.Combine(configRoot, "local.codex-run.json");
            var envFilePath = Path.Combine(tempRepo.FullName, ".env.worktree");
            await File.WriteAllTextAsync(localCodexRunPath, "{}");
            await File.WriteAllTextAsync(envFilePath,
                """
                SF_AD_SYNC_SF_PASSWORD=secret-value
                UNUSED_SETTING=remove-me
                SYNCFACTORS_RUN_PROFILE=real
                """);
            File.Delete(Path.Combine(configRoot, "local.empjob-confirmed.mapping-config.json"));

            var result = await InvokePowerShellFileAsync(
                workingDirectory: tempRepo.FullName,
                filePath: Path.Combine(tempRepo.FullName, "scripts", "Update-LocalSyncFactorsConfig.ps1"));

            Assert.Equal(0, result.ExitCode);

            using var codexRunDocument = JsonDocument.Parse(await File.ReadAllTextAsync(localCodexRunPath));
            Assert.True(codexRunDocument.RootElement.GetProperty("git").GetProperty("pullBeforeStackStart").GetBoolean());
            Assert.True(File.Exists(Path.Combine(configRoot, "local.empjob-confirmed.mapping-config.json")));

            var envContent = await File.ReadAllTextAsync(envFilePath);
            Assert.Contains("SYNCFACTORS_RUN_PROFILE=real", envContent);
            Assert.Contains("SF_AD_SYNC_SF_PASSWORD=secret-value", envContent);
            Assert.DoesNotContain("UNUSED_SETTING=remove-me", envContent, StringComparison.Ordinal);
            Assert.DoesNotContain("SF_AD_SYNC_SF_PASSWORD=secret-value" + Environment.NewLine + "SYNCFACTORS_RUN_PROFILE=real", envContent, StringComparison.Ordinal);
        }
        finally
        {
            tempRepo.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RunScript_HeadlessFailure_ExplainsHowToFixConfigDrift()
    {
        var tempRepo = await CreateMinimalScriptRepoAsync();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempRepo.FullName, "config", "local.codex-run.json"),
                """
                {
                  "git": {
                    "pullBeforeStackStart": true,
                    "obsolete": true
                  }
                }
                """);

            var result = await InvokePowerShellFileAsync(
                workingDirectory: tempRepo.FullName,
                filePath: Path.Combine(tempRepo.FullName, "scripts", "codex", "run.ps1"),
                "-Service", "mock",
                "-SkipBuild");

            Assert.NotEqual(0, result.ExitCode);
            var combinedOutput = result.StandardOutput + result.StandardError;
            Assert.Contains("Local config/env drift blocked startup", combinedOutput);
            Assert.Contains("headless session", combinedOutput);
            Assert.Contains("Update-LocalSyncFactorsConfig.ps1", combinedOutput);
            Assert.Contains("./scripts/codex/run.ps1", combinedOutput);
            Assert.Contains("-Service mock", combinedOutput);
            Assert.Contains("-Profile mock", combinedOutput);
            Assert.Contains("-SkipBuild", combinedOutput);
            Assert.Contains("config/local.codex-run.json", combinedOutput);
        }
        finally
        {
            tempRepo.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RunScript_StackGitPullUpdate_RelaunchesBeforeLaterConfigValidation()
    {
        var tempRepo = await CreateMinimalScriptRepoAsync();
        try
        {
            var runScriptPath = Path.Combine(tempRepo.FullName, "scripts", "codex", "run.ps1");
            var runScript = await File.ReadAllTextAsync(runScriptPath);
            const string marker = "$scriptPath = $MyInvocation.MyCommand.Path";
            Assert.Contains(marker, runScript);
            runScript = runScript.Replace(
                marker,
                marker + Environment.NewLine + "Add-Content -Path (Join-Path (Get-Location) 'relaunch-count.txt') -Value 'start'",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(runScriptPath, runScript);

            await File.WriteAllTextAsync(
                Path.Combine(tempRepo.FullName, "config", "local.codex-run.json"),
                """
                {
                  "git": {
                    "pullBeforeStackStart": true,
                    "obsolete": true
                  }
                }
                """);

            var fakeGitDirectory = Path.Combine(tempRepo.FullName, ".test-bin");
            Directory.CreateDirectory(fakeGitDirectory);
            await CreateFakeGitAsync(fakeGitDirectory, Path.Combine(tempRepo.FullName, "fake-git-head.txt"));

            var result = await InvokePowerShellFileAsync(
                workingDirectory: tempRepo.FullName,
                filePath: runScriptPath,
                environment: new Dictionary<string, string?>
                {
                    ["PATH"] = fakeGitDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
                },
                "-Service", "stack",
                "-SkipBuild");

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Local config/env drift blocked startup", result.StandardOutput + result.StandardError);

            var relaunchEntries = await File.ReadAllLinesAsync(Path.Combine(tempRepo.FullName, "relaunch-count.txt"));
            Assert.Equal(2, relaunchEntries.Length);

            var headState = await File.ReadAllTextAsync(Path.Combine(tempRepo.FullName, "fake-git-head.txt"));
            Assert.Equal("updated", headState.Trim());
        }
        finally
        {
            tempRepo.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RunScript_HeadlessFailure_ExplainsHowToFixWorktreeEnvDrift()
    {
        var tempRepo = await CreateMinimalScriptRepoAsync();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempRepo.FullName, ".env.worktree"),
                """
                UNUSED_SETTING=remove-me
                SYNCFACTORS_RUN_PROFILE=mock
                """);

            var result = await InvokePowerShellFileAsync(
                workingDirectory: tempRepo.FullName,
                filePath: Path.Combine(tempRepo.FullName, "scripts", "codex", "run.ps1"),
                "-Service", "mock",
                "-SkipBuild");

            Assert.NotEqual(0, result.ExitCode);
            var combinedOutput = result.StandardOutput + result.StandardError;
            Assert.Contains("Local config/env drift blocked startup", combinedOutput);
            Assert.Contains("headless session", combinedOutput);
            Assert.Contains("Update-LocalSyncFactorsConfig.ps1", combinedOutput);
            Assert.Contains(".env.worktree", combinedOutput);
        }
        finally
        {
            tempRepo.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RunScript_AdOuPrecheckFailure_BlocksStartupWithOuDetails()
    {
        var tempRepo = await CreateMinimalScriptRepoAsync();
        try
        {
            var envFilePath = Path.Combine(tempRepo.FullName, ".env.worktree");
            var envContent = await File.ReadAllTextAsync(envFilePath);
            envContent = envContent.Replace(
                "SF_AD_SYNC_AD_SERVER=" + Environment.NewLine,
                "SF_AD_SYNC_AD_SERVER=stub.example.test" + Environment.NewLine,
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(envFilePath, envContent);

            await File.WriteAllTextAsync(
                Path.Combine(tempRepo.FullName, "scripts", "Test-SyncFactorsActiveDirectoryOuAccess.ps1"),
                """
                function Assert-SyncFactorsConfiguredAdOusAccessible {
                    param([string]$ConfigPath)

                    throw "Configured AD OU precheck failed against LDAP server 'stub.example.test'. defaultActiveOu='OU=Missing,DC=example,DC=com' failed: directory object was not found."
                }
                """);

            var result = await InvokePowerShellFileAsync(
                workingDirectory: tempRepo.FullName,
                filePath: Path.Combine(tempRepo.FullName, "scripts", "codex", "run.ps1"),
                "-Service", "worker",
                "-SkipBuild");

            Assert.NotEqual(0, result.ExitCode);
            var combinedOutput = result.StandardOutput + result.StandardError;
            Assert.Contains("Configured AD OU precheck failed", combinedOutput);
            Assert.Contains("defaultActiveOu='OU=Missing,DC=example,DC=com'", combinedOutput);
            Assert.DoesNotContain("dotnet build failed", combinedOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            tempRepo.Delete(recursive: true);
        }
    }

    private static async Task<DirectoryInfo> CreateMinimalScriptRepoAsync()
    {
        var tempRepo = Directory.CreateTempSubdirectory("syncfactors-script-repo");
        var repoRoot = GetRepositoryRoot();

        foreach (var relativePath in new[]
        {
            "scripts/SyncFactorsBackup.ps1",
            "scripts/SyncFactorsJson.ps1",
            "scripts/Sync-LocalConfigFormat.ps1",
            "scripts/Update-LocalSyncFactorsConfig.ps1",
            "scripts/Test-SyncFactorsActiveDirectoryOuAccess.ps1",
            "scripts/Start-SyncFactorsCommon.ps1",
            "scripts/codex/run.ps1",
            "scripts/codex/Load-WorktreeEnv.ps1",
            "scripts/codex/WorktreeEnv.ps1",
            ".env.worktree.example"
        })
        {
            await CopyFileAsync(Path.Combine(repoRoot, relativePath), Path.Combine(tempRepo.FullName, relativePath));
        }

        await CopyFileAsync(
            Path.Combine(repoRoot, ".env.worktree.example"),
            Path.Combine(tempRepo.FullName, ".env.worktree"));

        var configRoot = Path.Combine(tempRepo.FullName, "config");
        Directory.CreateDirectory(configRoot);

        await CopySampleToLocalAsync(tempRepo.FullName, "sample.codex-run.json", "local.codex-run.json");
        await CopySampleToLocalAsync(tempRepo.FullName, "sample.mock-successfactors.real-ad.sync-config.json", "local.mock-successfactors.real-ad.sync-config.json");
        await CopySampleToLocalAsync(tempRepo.FullName, "sample.real-successfactors.real-ad.sync-config.json", "local.real-successfactors.real-ad.sync-config.json");
        await CopySampleToLocalAsync(tempRepo.FullName, "sample.empjob-confirmed.mapping-config.json", "local.syncfactors.mapping-config.json");
        await CopySampleToLocalAsync(tempRepo.FullName, "sample.empjob-confirmed.mapping-config.json", "local.empjob-confirmed.mapping-config.json");

        return tempRepo;
    }

    private static async Task CopySampleToLocalAsync(string tempRepoRoot, string sampleFileName, string localFileName)
    {
        var sourcePath = GetRepositoryFile(Path.Combine("config", sampleFileName));
        var tempSamplePath = Path.Combine(tempRepoRoot, "config", sampleFileName);
        var tempLocalPath = Path.Combine(tempRepoRoot, "config", localFileName);
        await CopyFileAsync(sourcePath, tempSamplePath);
        await CopyFileAsync(sourcePath, tempLocalPath);
    }

    private static async Task CopyFileAsync(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var source = File.OpenRead(sourcePath);
        await using var destination = File.Create(destinationPath);
        await source.CopyToAsync(destination);
    }

    private static async Task<PowerShellProcessResult> InvokePowerShellFileAsync(string workingDirectory, string filePath, params string[] arguments) =>
        await InvokePowerShellFileAsync(workingDirectory, filePath, environment: null, arguments);

    private static async Task<PowerShellProcessResult> InvokePowerShellFileAsync(
        string workingDirectory,
        string filePath,
        IReadOnlyDictionary<string, string?>? environment,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(filePath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var entry in environment)
            {
                startInfo.Environment[entry.Key] = entry.Value;
            }
        }

        return await InvokeProcessAsync(startInfo);
    }

    private static async Task<PowerShellProcessResult> InvokePowerShellCommandAsync(string workingDirectory, string command)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        return await InvokeProcessAsync(startInfo);
    }

    private static async Task<PowerShellProcessResult> InvokeProcessAsync(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        process!.StandardInput.Close();

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new PowerShellProcessResult(
            process.ExitCode,
            await standardOutputTask,
            await standardErrorTask);
    }

    private static string GetRepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string GetRepositoryFile(string relativePath) =>
        Path.Combine(GetRepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static async Task CreateFakeGitAsync(string binDirectory, string headStatePath)
    {
        await File.WriteAllTextAsync(headStatePath, "initial" + Environment.NewLine);

        var fakeGitScriptPath = Path.Combine(binDirectory, "fake-git.ps1");
        var escapedHeadStatePath = headStatePath.Replace("'", "''");
        await File.WriteAllTextAsync(
            fakeGitScriptPath,
            $$"""
            $arguments = @($args)
            if ($arguments.Length -ge 2 -and $arguments[0] -eq '-C') {
                $arguments = $arguments[2..($arguments.Length - 1)]
            }

            $headStatePath = '{{escapedHeadStatePath}}'
            if ($arguments.Length -eq 2 -and $arguments[0] -eq 'branch' -and $arguments[1] -eq '--show-current') {
                Write-Output 'main'
                exit 0
            }

            if ($arguments.Length -eq 4 -and $arguments[0] -eq 'rev-parse' -and $arguments[1] -eq '--abbrev-ref' -and $arguments[2] -eq '--symbolic-full-name' -and $arguments[3] -eq '@{upstream}') {
                Write-Output 'origin/main'
                exit 0
            }

            if ($arguments.Length -eq 2 -and $arguments[0] -eq 'rev-parse' -and $arguments[1] -eq 'HEAD') {
                $state = (Get-Content -Path $headStatePath -Raw).Trim()
                if ($state -eq 'initial') {
                    Write-Output 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
                }
                else {
                    Write-Output 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
                }

                exit 0
            }

            if ($arguments.Length -eq 2 -and $arguments[0] -eq 'pull' -and $arguments[1] -eq '--ff-only') {
                $state = (Get-Content -Path $headStatePath -Raw).Trim()
                if ($state -eq 'initial') {
                    Set-Content -Path $headStatePath -Value 'updated'
                }

                exit 0
            }

            throw "Unexpected fake git command: $($arguments -join ' ')"
            """);

        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(
                Path.Combine(binDirectory, "git.cmd"),
                $"""
                @echo off
                pwsh -NoProfile -File "{fakeGitScriptPath}" %*
                """);
            return;
        }

        var wrapperPath = Path.Combine(binDirectory, "git");
        var escapedScriptPath = fakeGitScriptPath.Replace("\"", "\\\"");
        await File.WriteAllTextAsync(
            wrapperPath,
            $$"""
            #!/usr/bin/env bash
            exec pwsh -NoProfile -File "{{escapedScriptPath}}" "$@"
            """);
        File.SetUnixFileMode(
            wrapperPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private sealed record PowerShellProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
