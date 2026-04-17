using System.Diagnostics;
using System.Text.Json;

namespace SyncFactors.Api.Tests;

public sealed class WorktreeEnvHelperTests
{
    [Fact]
    public async Task SetWorktreeEnvPlaceholder_BlanksExistingAssignmentAndPreservesOtherLines()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("syncfactors-worktree-env");
        try
        {
            var envFilePath = Path.Combine(tempDirectory.FullName, ".env.worktree");
            await File.WriteAllTextAsync(envFilePath,
                """
                SYNCFACTORS_RUN_PROFILE=mock
                # keep this comment
                SF_AD_SYNC_SF_PASSWORD=super-secret
                OTHER_SETTING=value
                """);

            await InvokeWorktreeEnvHelperAsync("set-worktree-env-placeholder", envFilePath, "SF_AD_SYNC_SF_PASSWORD");

            var content = await File.ReadAllTextAsync(envFilePath);
            Assert.Contains("SYNCFACTORS_RUN_PROFILE=mock", content);
            Assert.Contains("# keep this comment", content);
            Assert.Contains("SF_AD_SYNC_SF_PASSWORD=", content);
            Assert.DoesNotContain("SF_AD_SYNC_SF_PASSWORD=super-secret", content);
            Assert.Contains("OTHER_SETTING=value", content);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SetWorktreeEnvPlaceholder_AppendsMissingAssignment()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("syncfactors-worktree-env");
        try
        {
            var envFilePath = Path.Combine(tempDirectory.FullName, ".env.worktree");
            await File.WriteAllTextAsync(envFilePath,
                """
                SYNCFACTORS_RUN_PROFILE=mock
                OTHER_SETTING=value
                """);

            await InvokeWorktreeEnvHelperAsync("set-worktree-env-placeholder", envFilePath, "SF_AD_SYNC_AD_SERVER");

            var lines = await File.ReadAllLinesAsync(envFilePath);
            Assert.Contains("SYNCFACTORS_RUN_PROFILE=mock", lines);
            Assert.Contains("OTHER_SETTING=value", lines);
            Assert.Equal("SF_AD_SYNC_AD_SERVER=", lines[^1]);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SetWorktreeEnvValue_BlanksTemplateAndRemovesDuplicateAssignments()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("syncfactors-worktree-env");
        try
        {
            var envFilePath = Path.Combine(tempDirectory.FullName, ".env.worktree");
            await File.WriteAllTextAsync(envFilePath,
                """
                # SYNCFACTORS__AUTH__OIDC__CLIENTSECRET=
                OTHER_SETTING=value
                SYNCFACTORS__AUTH__OIDC__CLIENTSECRET=first-secret
                SYNCFACTORS__AUTH__OIDC__CLIENTSECRET=second-secret
                """);

            await InvokeWorktreeEnvHelperAsync("set-worktree-env-value", envFilePath, "SYNCFACTORS__AUTH__OIDC__CLIENTSECRET", string.Empty);

            var lines = await File.ReadAllLinesAsync(envFilePath);
            Assert.Contains("OTHER_SETTING=value", lines);
            Assert.Equal(1, lines.Count(line => line == "SYNCFACTORS__AUTH__OIDC__CLIENTSECRET="));
            Assert.DoesNotContain(lines, line => line.Contains("first-secret", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Contains("second-secret", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.StartsWith("# SYNCFACTORS__AUTH__OIDC__CLIENTSECRET=", StringComparison.Ordinal));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GetWorktreeEnvValueState_DistinguishesPresentBlankMissingAndValuedEntries()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("syncfactors-worktree-env");
        try
        {
            var envFilePath = Path.Combine(tempDirectory.FullName, ".env.worktree");
            await File.WriteAllTextAsync(envFilePath,
                """
                BLANK_VALUE=
                WHITESPACE_VALUE=   
                REAL_VALUE=domain-user
                """);

            var blankState = await InvokeWorktreeEnvHelperJsonAsync(envFilePath, "BLANK_VALUE");
            Assert.True(blankState.GetProperty("Found").GetBoolean());
            Assert.False(blankState.GetProperty("HasValue").GetBoolean());
            Assert.Equal(string.Empty, blankState.GetProperty("Value").GetString());

            var whitespaceState = await InvokeWorktreeEnvHelperJsonAsync(envFilePath, "WHITESPACE_VALUE");
            Assert.True(whitespaceState.GetProperty("Found").GetBoolean());
            Assert.False(whitespaceState.GetProperty("HasValue").GetBoolean());
            Assert.Equal(string.Empty, whitespaceState.GetProperty("Value").GetString());

            var realValueState = await InvokeWorktreeEnvHelperJsonAsync(envFilePath, "REAL_VALUE");
            Assert.True(realValueState.GetProperty("Found").GetBoolean());
            Assert.True(realValueState.GetProperty("HasValue").GetBoolean());
            Assert.Equal("domain-user", realValueState.GetProperty("Value").GetString());

            var missingState = await InvokeWorktreeEnvHelperJsonAsync(envFilePath, "MISSING_VALUE");
            Assert.False(missingState.GetProperty("Found").GetBoolean());
            Assert.False(missingState.GetProperty("HasValue").GetBoolean());
            Assert.Equal(JsonValueKind.Null, missingState.GetProperty("Value").ValueKind);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SyncWorktreeEnvFormat_RewritesFileToSampleLayoutAndKeepsLocalValues()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("syncfactors-worktree-env");
        try
        {
            var samplePath = Path.Combine(tempDirectory.FullName, ".env.worktree.example");
            var envFilePath = Path.Combine(tempDirectory.FullName, ".env.worktree");

            await File.WriteAllTextAsync(samplePath,
                """
                SYNCFACTORS_RUN_PROFILE=mock
                # Optional comment
                # SYNCFACTORS__AUTH__MODE=oidc
                # SYNCFACTORS__AUTH__OIDC__CLIENTID=
                SF_AD_SYNC_SF_USERNAME=
                SF_AD_SYNC_SF_PASSWORD=
                """);

            await File.WriteAllTextAsync(envFilePath,
                """
                SF_AD_SYNC_SF_PASSWORD=secret-value
                UNUSED_SETTING=remove-me
                SYNCFACTORS__AUTH__MODE=hybrid
                SYNCFACTORS_RUN_PROFILE=real
                SYNCFACTORS__AUTH__OIDC__CLIENTID=client-id
                """);

            var result = await InvokePowerShellCommandAsync(
                GetRepositoryRoot(),
                string.Join(
                    Environment.NewLine,
                    $". '{GetRepositoryFile(Path.Combine("scripts", "codex", "WorktreeEnv.ps1")).Replace("'", "''")}'",
                    $"Sync-WorktreeEnvFormat -SampleConfigPath '{samplePath.Replace("'", "''")}' -LocalConfigPath '{envFilePath.Replace("'", "''")}' -NoBackup | Out-Null"));

            Assert.Equal(0, result.ExitCode);

            var content = await File.ReadAllTextAsync(envFilePath);
            Assert.Equal(
                """
                SYNCFACTORS_RUN_PROFILE=real
                # Optional comment
                SYNCFACTORS__AUTH__MODE=hybrid
                SYNCFACTORS__AUTH__OIDC__CLIENTID=client-id
                SF_AD_SYNC_SF_USERNAME=
                SF_AD_SYNC_SF_PASSWORD=secret-value

                """.ReplaceLineEndings(),
                content.ReplaceLineEndings());
            Assert.DoesNotContain("UNUSED_SETTING=remove-me", content, StringComparison.Ordinal);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    private static async Task InvokeWorktreeEnvHelperAsync(string action, string envFilePath, string variableName, string? value = null)
    {
        using var process = StartWorktreeEnvHelperProcess(action, envFilePath, variableName, value);
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Invoke-WorktreeEnvHelper.ps1 failed with exit code {process.ExitCode}.{Environment.NewLine}{standardOutput}{standardError}");
        }
    }

    private static async Task<JsonElement> InvokeWorktreeEnvHelperJsonAsync(string envFilePath, string variableName)
    {
        using var process = StartWorktreeEnvHelperProcess("get-worktree-env-value-state", envFilePath, variableName, null);
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Invoke-WorktreeEnvHelper.ps1 failed with exit code {process.ExitCode}.{Environment.NewLine}{standardOutput}{standardError}");
        }

        using var document = JsonDocument.Parse(standardOutput);
        return document.RootElement.Clone();
    }

    private static Process StartWorktreeEnvHelperProcess(string action, string envFilePath, string variableName, string? value)
    {
        var helperPath = Path.Combine(GetRepositoryRoot(), "scripts", "codex", "Invoke-WorktreeEnvHelper.ps1");
        var startInfo = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = GetRepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(helperPath);
        startInfo.ArgumentList.Add("-Action");
        startInfo.ArgumentList.Add(action);
        startInfo.ArgumentList.Add("-EnvFilePath");
        startInfo.ArgumentList.Add(envFilePath);
        startInfo.ArgumentList.Add("-VariableName");
        startInfo.ArgumentList.Add(variableName);
        if (value is not null)
        {
            startInfo.ArgumentList.Add("-Value");
            startInfo.ArgumentList.Add(value);
        }

        var process = Process.Start(startInfo);
        Assert.NotNull(process);
        return process!;
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> InvokePowerShellCommandAsync(string workingDirectory, string command)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        var standardOutput = await process!.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, standardOutput, standardError);
    }

    private static string GetRepositoryFile(string relativePath) =>
        Path.Combine(GetRepositoryRoot(), relativePath);

    private static string GetRepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
