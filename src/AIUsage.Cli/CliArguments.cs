namespace AIUsage.Cli;

public sealed record CliArguments(string? ProviderId = null, bool Force = false, bool ShowVersion = false, bool ShowHelp = false)
{
    /// <summary>Direct port of the Swift CLIArguments.parse: at most one bare provider token,
    /// plus --force/-v/--version/-h/--help. Anything else is a usage error.</summary>
    public static CliArguments Parse(string[] arguments)
    {
        var parsed = new CliArguments();
        foreach (var argument in arguments)
        {
            switch (argument)
            {
                case "--force":
                    parsed = parsed with { Force = true };
                    break;
                case "-v":
                case "--version":
                    parsed = parsed with { ShowVersion = true };
                    break;
                case "-h":
                case "--help":
                    parsed = parsed with { ShowHelp = true };
                    break;
                default:
                    if (argument.StartsWith('-'))
                    {
                        throw new CliUsageException($"Unknown option: {argument}");
                    }
                    if (parsed.ProviderId is not null)
                    {
                        throw new CliUsageException("Only one provider may be specified.");
                    }
                    parsed = parsed with { ProviderId = argument.ToLowerInvariant() };
                    break;
            }
        }
        return parsed;
    }
}

public sealed class CliUsageException : Exception
{
    public CliUsageException(string message) : base(message) { }
}
