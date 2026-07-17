using Xunit;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

public sealed class SecretsManagerCredentialRotationQualificationTests
{
    [Fact]
    public void Sentinel_name_uses_the_full_guid_without_invalid_truncation()
    {
        var name = SecretsManagerCredentialRotationQualification.CreateSentinelName(
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));

        Assert.Equal(
            "a2a-rotation-00112233445566778899aabbccddeeff",
            name);
        Assert.Equal(45, name.Length);
    }
}
