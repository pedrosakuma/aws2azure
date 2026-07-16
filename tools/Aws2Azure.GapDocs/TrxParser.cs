using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Aws2Azure.GapDocs;

public enum ConformanceOutcome
{
    Passed,
    Failed,
    Skipped,
    NotRun
}

public sealed record TrxTestResult(
    string TestIdentity,
    ConformanceOutcome Outcome,
    TimeSpan Duration,
    string SourceFile);

public static class TrxParser
{
    private static readonly HashSet<string> KnownFailureOutcomes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Failed", "Error", "Timeout", "Aborted", "Blocked", "Inconclusive",
        "Warning", "NotRunnable", "Disconnected"
    };

    public static IReadOnlyList<TrxTestResult> ParseFiles(IEnumerable<string> paths)
    {
        var results = new List<TrxTestResult>();
        foreach (var path in paths.OrderBy(p => p, StringComparer.Ordinal))
        {
            using var reader = new StreamReader(path);
            results.AddRange(Parse(reader, path));
        }
        return results;
    }

    public static IReadOnlyList<TrxTestResult> Parse(TextReader reader, string sourceFile)
    {
        var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        if (document.Root?.Name.LocalName != "TestRun")
        {
            throw new InvalidDataException($"{sourceFile}: expected a TRX TestRun document");
        }

        var definitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var unitTest in document.Descendants().Where(e => e.Name.LocalName == "UnitTest"))
        {
            var id = Attribute(unitTest, "id");
            var method = unitTest.Descendants().FirstOrDefault(e => e.Name.LocalName == "TestMethod");
            var className = method is null ? null : Attribute(method, "className");
            var methodName = method is null ? null : Attribute(method, "name");
            if (!string.IsNullOrWhiteSpace(id)
                && !string.IsNullOrWhiteSpace(className)
                && !string.IsNullOrWhiteSpace(methodName))
            {
                definitions[id] = className + "." + methodName;
            }
        }

        var results = new List<TrxTestResult>();
        foreach (var result in document.Descendants().Where(e => e.Name.LocalName == "UnitTestResult"))
        {
            var testId = Attribute(result, "testId");
            var testName = Attribute(result, "testName");
            var identity = !string.IsNullOrWhiteSpace(testId) && definitions.TryGetValue(testId, out var definedIdentity)
                ? definedIdentity
                : testName;
            if (string.IsNullOrWhiteSpace(identity))
            {
                throw new InvalidDataException($"{sourceFile}: UnitTestResult has neither a resolvable testId nor testName");
            }

            var outcomeText = Attribute(result, "outcome")
                ?? throw new InvalidDataException($"{sourceFile}: result '{identity}' is missing outcome");
            var outcome = ParseOutcome(outcomeText, sourceFile, identity);
            var durationText = Attribute(result, "duration");
            var duration = TimeSpan.Zero;
            if (!string.IsNullOrWhiteSpace(durationText)
                && !TimeSpan.TryParse(durationText, CultureInfo.InvariantCulture, out duration))
            {
                throw new InvalidDataException($"{sourceFile}: result '{identity}' has invalid duration '{durationText}'");
            }

            results.Add(new TrxTestResult(identity, outcome, duration, sourceFile));
        }

        return results;
    }

    private static ConformanceOutcome ParseOutcome(string value, string sourceFile, string identity)
    {
        if (value.Equals("Passed", StringComparison.OrdinalIgnoreCase))
        {
            return ConformanceOutcome.Passed;
        }
        if (value.Equals("NotExecuted", StringComparison.OrdinalIgnoreCase))
        {
            return ConformanceOutcome.Skipped;
        }
        if (KnownFailureOutcomes.Contains(value))
        {
            return ConformanceOutcome.Failed;
        }
        throw new InvalidDataException($"{sourceFile}: result '{identity}' has unknown outcome '{value}'");
    }

    private static string? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;
}
