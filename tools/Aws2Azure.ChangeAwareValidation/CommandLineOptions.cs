namespace Aws2Azure.ChangeAwareValidation;

internal sealed record CommandLineOptions(string BaseRef, bool Pretty, bool ShowHelp)
{
    public static CommandLineOptions Parse(string[] args)
    {
        var baseRef = "main";
        var pretty = false;
        var showHelp = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--base":
                    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    {
                        throw new ArgumentException("--base requires a git ref.");
                    }

                    baseRef = args[index];
                    break;
                case "--pretty":
                    pretty = true;
                    break;
                case "--help":
                case "-h":
                    showHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[index]}'.");
            }
        }

        return new CommandLineOptions(baseRef, pretty, showHelp);
    }
}
