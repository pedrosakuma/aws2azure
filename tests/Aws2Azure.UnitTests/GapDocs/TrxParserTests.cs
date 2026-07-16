using Aws2Azure.GapDocs;

namespace Aws2Azure.UnitTests.GapDocs;

public sealed class TrxParserTests
{
    [Fact]
    public void Parse_resolves_test_definitions_outcomes_and_durations()
    {
        const string trx = """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="pass-id" testName="display pass" outcome="Passed" duration="00:00:01.2500000" />
                <UnitTestResult testId="skip-id" testName="display skip" outcome="NotExecuted" duration="00:00:00" />
                <UnitTestResult testId="fail-id" testName="display fail" outcome="Failed" duration="00:00:00.5000000" />
              </Results>
              <TestDefinitions>
                <UnitTest id="pass-id"><TestMethod className="Tests.Sample" name="Passes" /></UnitTest>
                <UnitTest id="skip-id"><TestMethod className="Tests.Sample" name="Skips" /></UnitTest>
                <UnitTest id="fail-id"><TestMethod className="Tests.Sample" name="Fails" /></UnitTest>
              </TestDefinitions>
            </TestRun>
            """;

        var results = TrxParser.Parse(new StringReader(trx), "results.trx");

        Assert.Collection(
            results,
            result =>
            {
                Assert.Equal("Tests.Sample.Passes", result.TestIdentity);
                Assert.Equal(ConformanceOutcome.Passed, result.Outcome);
                Assert.Equal(TimeSpan.FromMilliseconds(1250), result.Duration);
            },
            result => Assert.Equal(ConformanceOutcome.Skipped, result.Outcome),
            result => Assert.Equal(ConformanceOutcome.Failed, result.Outcome));
    }

    [Fact]
    public void Parse_rejects_unknown_outcome()
    {
        const string trx = """
            <TestRun>
              <Results>
                <UnitTestResult testName="Tests.Sample.Unknown" outcome="Surprising" />
              </Results>
            </TestRun>
            """;

        var error = Assert.Throws<InvalidDataException>(
            () => TrxParser.Parse(new StringReader(trx), "results.trx"));

        Assert.Contains("unknown outcome 'Surprising'", error.Message, StringComparison.Ordinal);
    }
}
