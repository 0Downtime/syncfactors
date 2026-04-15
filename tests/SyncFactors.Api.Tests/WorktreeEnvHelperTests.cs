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

    private static async Task InvokeWorktreeEnvHelperAsync(string action, string envFilePath, string variableName)
    {
        using var process = StartWorktreeEnvHelperProcess(action, envFilePath, variableName);
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
        using var process = StartWorktreeEnvHelperProcess("get-worktree-env-value-state", envFilePath, variableName);
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

    private static Process StartWorktreeEnvHelperProcess(string action, string envFilePath, string variableName)
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

        var process = Process.Start(startInfo);
        Assert.NotNull(process);
        return process!;
    }

    private static string GetRepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
