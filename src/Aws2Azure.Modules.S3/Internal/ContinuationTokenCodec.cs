using System.Text;

namespace Aws2Azure.Modules.S3.Internal;

/// <summary>
/// Round-trips Azure's opaque blob-listing <c>NextMarker</c> as an
/// S3-shaped continuation token. We base64url-encode the marker so that:
/// (a) the surface looks opaque to SDKs (S3 docs only promise opacity);
/// (b) the encoded form survives SigV4 canonical-query escaping without
/// extra '%' double-encoding traps; (c) the proxy stays stateless — no
/// server-side token registry.
/// </summary>
internal static class ContinuationTokenCodec
{
    /// <summary>Encodes an Azure NextMarker as an S3 continuation token.</summary>
    public static string Encode(string azureMarker)
    {
        ArgumentNullException.ThrowIfNull(azureMarker);
        var bytes = Encoding.UTF8.GetBytes(azureMarker);
        return ToBase64Url(bytes);
    }

    /// <summary>
    /// Decodes a previously-issued S3 continuation token back into the
    /// Azure NextMarker. Returns null when the token is malformed so the
    /// caller can surface InvalidArgument without crashing the request.
    /// </summary>
    public static string? TryDecode(string s3Token)
    {
        if (string.IsNullOrEmpty(s3Token))
        {
            return null;
        }
        try
        {
            var bytes = FromBase64Url(s3Token);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string ToBase64Url(byte[] bytes)
    {
        var b64 = Convert.ToBase64String(bytes);
        // base64url: '+' → '-', '/' → '_', trim '=' padding.
        return b64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static byte[] FromBase64Url(string s)
    {
        var b64 = s.Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4)
        {
            case 2: b64 += "=="; break;
            case 3: b64 += "="; break;
            case 1: throw new FormatException("Invalid base64url length.");
        }
        return Convert.FromBase64String(b64);
    }
}
