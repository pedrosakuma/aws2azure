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
            """);
        return 0;
    }

    var diff = GitDiffReader.Read(options.BaseRef);
    var plan = ValidationPlanBuilder.Build(diff.ChangedPaths, diff.Comparison);
    var serializerContext = new ValidationJsonContext(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = options.Pretty
    });
    Console.WriteLine(JsonSerializer.Serialize(plan, serializerContext.ValidationPlan));
    return 0;
}
catch (Exception exception) when (
    exception is ArgumentException or InvalidOperationException)
{
    Console.Error.WriteLine($"change-aware-validation: {exception.Message}");
    return 2;
}
