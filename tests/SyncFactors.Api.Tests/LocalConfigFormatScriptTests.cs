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
            await File.WriteAllTextAsync(localCodexRunPath, "{}");
            File.Delete(Path.Combine(configRoot, "local.empjob-confirmed.mapping-config.json"));

            var result = await InvokePowerShellFileAsync(
                workingDirectory: tempRepo.FullName,
                filePath: Path.Combine(tempRepo.FullName, "scripts", "Update-LocalSyncFactorsConfig.ps1"));

            Assert.Equal(0, result.ExitCode);

            using var codexRunDocument = JsonDocument.Parse(await File.ReadAllTextAsync(localCodexRunPath));
            Assert.True(codexRunDocument.RootElement.GetProperty("git").GetProperty("pullBeforeStackStart").GetBoolean());
            Assert.True(File.Exists(Path.Combine(configRoot, "local.empjob-confirmed.mapping-config.json")));
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
            Assert.Contains("Local config drift blocked startup because the launcher could not prompt", combinedOutput);
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

    private static async Task<DirectoryInfo> CreateMinimalScriptRepoAsync()
    {
        var tempRepo = Directory.CreateTempSubdirectory("syncfactors-script-repo");
        var repoRoot = GetRepositoryRoot();

        foreach (var relativePath in new[]
        {
            "scripts/Sync-LocalConfigFormat.ps1",
            "scripts/Update-LocalSyncFactorsConfig.ps1",
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

    private static async Task<PowerShellProcessResult> InvokePowerShellFileAsync(string workingDirectory, string filePath, params string[] arguments)
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

    private sealed record PowerShellProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
