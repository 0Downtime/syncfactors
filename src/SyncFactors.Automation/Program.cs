using SyncFactors.Automation;

if (args.Length > 0 && string.Equals(args[0], "bootstrap-local-user", StringComparison.OrdinalIgnoreCase))
{
    return await LocalAutomationUserBootstrapCommand.RunAsync(args[1..], Console.Out, CancellationToken.None);
}

return await AutomationCli.RunAsync(args, Console.Out, CancellationToken.None);
