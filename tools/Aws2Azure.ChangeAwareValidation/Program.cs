using System.Text.Json;
using Aws2Azure.ChangeAwareValidation;

try
{
    var options = CommandLineOptions.Parse(args);
    if (options.ShowHelp)
    {
        Console.WriteLine(
            """
            Usage: dotnet run --project tools/Aws2Azure.ChangeAwareValidation -- [--base <ref>] [--pretty]

            Classifies committed, staged, unstaged, and untracked changes from the
            merge-base with <ref> (default: main) and writes JSON.
            Fetch main first when the local clone does not contain an up-to-date main or origin/main ref.

            --review-inputs [--evidence-source <full commit SHA>]
                Emit exact clean-input binding for human review; never approves.
            --decision <json> --decision-sha256 <approved SHA256>
                Apply an explicitly selected, externally pinned real-azure decision.
                Read docs/testing/validation-evidence-decisions.md before use.
            """);
        return 0;
    }

    var diff = GitDiffReader.Read(options.BaseRef);
    var plan = ValidationPlanBuilder.Build(diff.ChangedPaths, diff.Comparison);
    if (options.ReviewInputs)
    {
        plan = plan with
        {
            SchemaVersion = 2,
            ReviewBinding = EvidenceDecisionValidator.Review(
                Directory.GetCurrentDirectory(), plan, options.EvidenceSource)
        };
    }
    if (options.Decision is not null)
    {
        plan = EvidenceDecisionValidator.Apply(
            Directory.GetCurrentDirectory(), plan, options.Decision, options.DecisionSha256!);
    }
    var serializerContext = new ValidationJsonContext(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = options.Pretty
    });
    Console.WriteLine(JsonSerializer.Serialize(plan, serializerContext.ValidationPlan));
    return 0;
}
catch (Exception exception) when (
    exception is ArgumentException or InvalidOperationException or IOException or
        UnauthorizedAccessException or JsonException)
{
    Console.Error.WriteLine($"change-aware-validation: {exception.Message}");
    return 2;
}
