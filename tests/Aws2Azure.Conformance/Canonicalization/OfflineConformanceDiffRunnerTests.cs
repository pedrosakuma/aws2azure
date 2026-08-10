using Aws2Azure.Conformance.Diff;

namespace Aws2Azure.Conformance.Canonicalization;

public sealed class OfflineConformanceDiffRunnerTests
{
    [Fact]
    public void NormalizeForComparison_treats_any_bucket_shaped_s3_arn_as_equivalent_across_case_names()
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
            OfflineConformanceDiffRunner.NormalizeForComparison("list-objects-v2-pagination", expected),
            OfflineConformanceDiffRunner.NormalizeForComparison("put-get-delete-object-roundtrip", actual));

        Assert.DoesNotContain(diffs, d => d.Tag == "header-value:x-amz-bucket-arn");
    }

    [Fact]
    public void NormalizeForComparison_masks_the_account_id_segment_of_any_aws_arn_body_field()
    {
        var expected = new CanonicalResponse(
            200,
            [],
            CanonicalResponse.BodyKindOpaque,
            [new CanonicalField("TopicArn", "arn:aws:sns:us-east-1:123456789012:conf-happy-topic-abc123")],
            string.Empty);
        var actual = new CanonicalResponse(
            200,
            [],
            CanonicalResponse.BodyKindOpaque,
            [new CanonicalField("TopicArn", "arn:aws:sns:us-east-1:000000000000:conf-happy-topic-abc123")],
            string.Empty);

        var diffs = CanonicalDiff.Compare(
            OfflineConformanceDiffRunner.NormalizeForComparison("topic-attributes-roundtrip", expected),
            OfflineConformanceDiffRunner.NormalizeForComparison("topic-attributes-roundtrip", actual));

        Assert.DoesNotContain(diffs, d => d.Tag == "field-value:TopicArn");
    }

    [Fact]
    public void NormalizeForComparison_masks_the_account_id_segment_of_a_subscription_endpoint_arn()
    {
        var expected = new CanonicalResponse(
            200,
            [],
            CanonicalResponse.BodyKindOpaque,
            [new CanonicalField(
                "Endpoint",
                "arn:aws:sqs:us-east-1:123456789012:conf-happy-queue")],
            string.Empty);
        var actual = new CanonicalResponse(
            200,
            [],
            CanonicalResponse.BodyKindOpaque,
            [new CanonicalField(
                "Endpoint",
                "arn:aws:sqs:us-east-1:000000000000:conf-happy-queue")],
            string.Empty);

        var diffs = CanonicalDiff.Compare(
            OfflineConformanceDiffRunner.NormalizeForComparison("list-subscriptions-roundtrip", expected),
            OfflineConformanceDiffRunner.NormalizeForComparison("list-subscriptions-roundtrip", actual));

        Assert.DoesNotContain(diffs, d => d.Tag == "field-value:Endpoint");
    }
}
