using System.Text;

namespace Aws2Azure.Modules.S3.Internal;

internal static class S3VersionIdCodec
{
    private const string Prefix = "azv-";

    public static string Encode(string azureVersionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(azureVersionId);
        return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(azureVersionId))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryDecode(string s3VersionId, out string azureVersionId)
    {
        azureVersionId = string.Empty;
        if (string.IsNullOrEmpty(s3VersionId))
        {
            return false;
        }

        if (!s3VersionId.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var normalized = s3VersionId[Prefix.Length..].Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 2: normalized += "=="; break;
            case 3: normalized += "="; break;
            case 0: break;
            default: return false;
        }

        try
        {
            azureVersionId = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            return azureVersionId.Length > 0;
        }
        catch (FormatException)
        {
            azureVersionId = string.Empty;
            return false;
        }
    }
}
