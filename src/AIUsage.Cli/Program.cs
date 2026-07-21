using AIUsage.Cli;
using AIUsage.Core.Services;

const string Help = """
Usage: aiusage [provider] [--force]

Read limits through AIUsage's shared five-minute cache and exit. Output is always JSON.

Options:
  --force      Refresh even when the shared cache is still fresh
  -v, --version
  -h, --help
""";

try
{
    var arguments = CliArguments.Parse(args);
    if (arguments.ShowHelp)
    {
        Console.WriteLine(Help);
        return 0;
    }

    if (arguments.ShowVersion)
    {
        Console.WriteLine($"aiusage {CliVersion.Display()}");
        return 0;
    }

    var reader = new UsageReader();
    var result = await reader.ReadAsync(arguments.ProviderId, arguments.Force).ConfigureAwait(false);
    Console.WriteLine(result.Json);
    if (result.Warnings.Count > 0)
    {
        foreach (var warning in result.Warnings) Console.Error.WriteLine($"warning: {warning}");
        return 4;
    }
    return 0;
}
catch (CliUsageException usageError)
{
    Console.Error.WriteLine($"aiusage: {usageError.Message}\nRun 'aiusage --help' for usage.");
    return 2;
}
catch (UnknownProviderException unknownProvider)
{
    Console.Error.WriteLine($"aiusage: {unknownProvider.Message}");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"aiusage: {ex.Message}");
    return 4;
}
