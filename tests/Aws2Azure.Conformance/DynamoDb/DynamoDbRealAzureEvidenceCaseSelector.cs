using Aws2Azure.Conformance.Cases;
using Aws2Azure.Core.Configuration;

namespace Aws2Azure.Conformance.DynamoDb;

public static class DynamoDbRealAzureEvidenceCaseSelector
{
    private const string DisabledSkipReasonPrefix =
        "requires stored procedures, disabled for this profile";

    public static IReadOnlyList<ConformanceCaseSelection> SelectCases(string? storedProcedureMode)
    {
        var mode = ResolveStoredProcedureMode(storedProcedureMode);
        var selectedCases = new List<ConformanceCaseSelection>(
            DynamoDbErrorMatrix.Cases.Count + DynamoDbHappyPathMatrix.Cases.Count);

        foreach (var testCase in DynamoDbErrorMatrix.Cases)
        {
            selectedCases.Add(new ConformanceCaseSelection(testCase));
        }

        foreach (var testCase in DynamoDbHappyPathMatrix.Cases)
        {
            selectedCases.Add(testCase.RequiresStoredProcedures && mode == StoredProcedureMode.Disabled
                ? new ConformanceCaseSelection(testCase, DisabledSkipReason(mode))
                : new ConformanceCaseSelection(testCase));
        }

        return selectedCases;
    }

    public static StoredProcedureMode ResolveStoredProcedureMode(string? storedProcedureMode)
    {
        if (string.IsNullOrWhiteSpace(storedProcedureMode))
        {
            return StoredProcedureMode.Disabled;
        }

        if (Enum.TryParse<StoredProcedureMode>(storedProcedureMode.Trim(), ignoreCase: true, out var mode)
            && mode is StoredProcedureMode.Disabled or StoredProcedureMode.Preferred)
        {
            return mode;
        }

        throw new InvalidOperationException(
            "AWS2AZURE_DDB_STORED_PROCEDURE_MODE must be Disabled or Preferred.");
    }

    private static string DisabledSkipReason(StoredProcedureMode mode)
        => $"{DisabledSkipReasonPrefix} (AWS2AZURE_DDB_STORED_PROCEDURE_MODE={mode})";
}
