using Aws2Azure.Conformance.Canonicalization;
using Aws2Azure.Conformance.Diff;

namespace Aws2Azure.Conformance.Canonicalization;

public sealed class OfflineConformanceDiffRunnerTests
{
    [Fact]
    public void NormalizeForComparison_treats_any_bucket_shaped_s3_arn_as_equivalent_for_list_objects_case()
    {
        var expected = new CanonicalResponse(
            200,
            [new CanonicalField("x-amz-bucket-arn", "arn:aws:s3:::aws2azure-it-1786146839-459618-s3bucket")],
            CanonicalResponse.BodyKindEmpty,
            [],
            string.Empty);
        var actual = new CanonicalResponse(
            200,
            [new CanonicalField("x-amz-bucket-arn", "arn:aws:s3:::conf-happy-bucket-abc123")],
            CanonicalResponse.BodyKindEmpty,
            [],
            string.Empty);

        var diffs = CanonicalDiff.Compare(
            OfflineConformanceDiffRunner.NormalizeForComparisonForTests("list-objects-v2-pagination", expected),
            OfflineConformanceDiffRunner.NormalizeForComparisonForTests("list-objects-v2-pagination", actual));

        Assert.DoesNotContain(diffs, d => d.Tag == "header-value:x-amz-bucket-arn");
    }
}
