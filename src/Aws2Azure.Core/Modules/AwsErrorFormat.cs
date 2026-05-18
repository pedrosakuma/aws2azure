namespace Aws2Azure.Core.Modules;

/// <summary>
/// On-the-wire format the module uses for AWS-shaped error responses.
/// S3 returns XML; almost every other AWS service returns JSON.
/// </summary>
public enum AwsErrorFormat
{
    Xml,
    Json,
}
