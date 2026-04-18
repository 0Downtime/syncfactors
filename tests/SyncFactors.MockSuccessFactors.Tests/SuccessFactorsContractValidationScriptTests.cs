using System.Diagnostics;
using System.Text.Json;

namespace SyncFactors.MockSuccessFactors.Tests;

public sealed class SuccessFactorsContractValidationScriptTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public async Task ValidateSuccessFactorsContractScript_PassesForCheckedInSampleExport()
    {
        using var temp = new TempContractValidationWorkspace();
        var inputPath = Path.Combine(ProjectRoot, "config", "mock-successfactors", "sample-export.json");
        var reportPath = Path.Combine(temp.Root, "contract-report.json");

        var result = await InvokePowerShellFileAsync(
            workingDirectory: ProjectRoot,
            filePath: Path.Combine(ProjectRoot, "scripts", "Validate-SuccessFactorsContract.ps1"),
            "-InputPath", inputPath,
            "-ReportPath", reportPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Result: PASSED", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(reportPath));

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
        Assert.Equal("passed", report.RootElement.GetProperty("result").GetString());
        Assert.Equal("odata-export", report.RootElement.GetProperty("detectedFormat").GetString());
        Assert.Equal(1, report.RootElement.GetProperty("workerCount").GetInt32());
    }

    [Fact]
    public async Task ValidateSuccessFactorsContractScript_FailsForDuplicateIdentityAndUnknownStatus()
    {
        using var temp = new TempContractValidationWorkspace();
        var inputPath = Path.Combine(temp.Root, "invalid-fixtures.json");
        var reportPath = Path.Combine(temp.Root, "invalid-report.json");

        await File.WriteAllTextAsync(
            inputPath,
            """
            {
              "workers": [
                {
                  "personIdExternal": "10001",
                  "userId": "alex.one",
                  "userName": "alex.one",
                  "email": "alex.one@example.com",
                  "firstName": "Alex",
                  "lastName": "One",
                  "startDate": "not-a-date",
                  "employmentStatus": "A"
                },
                {
                  "personIdExternal": "10001",
                  "userId": "alex.one",
                  "userName": "alex.one",
                  "email": "alex.two@example.com",
                  "firstName": "Alex",
                  "lastName": "Two",
                  "startDate": "2026-04-01T00:00:00Z",
                  "employmentStatus": "UNKNOWN"
                }
              ]
            }
            """);

        var result = await InvokePowerShellFileAsync(
            workingDirectory: ProjectRoot,
            filePath: Path.Combine(ProjectRoot, "scripts", "Validate-SuccessFactorsContract.ps1"),
            "-InputPath", inputPath,
            "-ReportPath", reportPath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Result: FAILED", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("duplicate-worker-id", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("duplicate-user-id", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unknown-employment-status", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("invalid-date", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(reportPath));

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
        Assert.Equal("failed", report.RootElement.GetProperty("result").GetString());
        Assert.True(report.RootElement.GetProperty("errors").GetArrayLength() >= 4);
    }

    private static async Task<ProcessResult> InvokePowerShellFileAsync(string workingDirectory, string filePath, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(filePath);
        foreach (var argument in arguments) {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start pwsh.");
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        return new ProcessResult(
            process.ExitCode,
            await standardOutputTask,
            await standardErrorTask);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class TempContractValidationWorkspace : IDisposable
    {
        public TempContractValidationWorkspace()
        {
            Root = Directory.CreateTempSubdirectory("syncfactors-contract-validation").FullName;
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root)) {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
