namespace Aws2Azure.ChangeAwareValidation;

internal sealed record CommandLineOptions(string BaseRef, bool Pretty, bool ShowHelp,
    bool ReviewInputs, string? EvidenceSource, string? Decision, string? DecisionSha256)
{
    public static CommandLineOptions Parse(string[] args)
    {
        var baseRef = "main";
        var pretty = false;
        var showHelp = false;
        var reviewInputs = false;
        string? evidenceSource = null;
        string? decision = null;
        string? decisionSha256 = null;

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
                case "--review-inputs":
                    reviewInputs = true;
                    break;
                case "--evidence-source":
                case "--decision":
                case "--decision-sha256":
                    var name = args[index];
                    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    {
                        throw new ArgumentException($"{name} requires a value.");
                    }
                    switch (name)
                    {
                        case "--evidence-source": evidenceSource = args[index]; break;
                        case "--decision": decision = args[index]; break;
                        case "--decision-sha256": decisionSha256 = args[index]; break;
                    }
                    break;
                case "--help":
                case "-h":
                    showHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[index]}'.");
            }
        }

        if ((decision is null) != (decisionSha256 is null) ||
            (reviewInputs && decision is not null) ||
            (evidenceSource is not null && !reviewInputs))
        {
            throw new ArgumentException("Use --decision with --decision-sha256, or --review-inputs with optional --evidence-source.");
        }
        return new CommandLineOptions(baseRef, pretty, showHelp, reviewInputs, evidenceSource, decision, decisionSha256);
    }
}
