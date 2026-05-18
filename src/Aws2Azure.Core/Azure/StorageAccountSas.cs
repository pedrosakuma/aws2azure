using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace Aws2Azure.Core.Azure;

/// <summary>
/// Generates an Azure Storage <em>account-level</em> SAS query string per the
/// REST API spec (version 2020-12-06+). The StringToSign layout is fixed and
/// must match exactly; any missing field still contributes its newline.
/// </summary>
public static class StorageAccountSas
{
    public static string Generate(
        string accountName,
        string base64Key,
        StorageSasPermissions permissions,
        StorageSasServices services,
        StorageSasResourceTypes resourceTypes,
        DateTimeOffset expiry,
        DateTimeOffset? start = null,
        string? ipRange = null,
        StorageSasProtocol protocol = StorageSasProtocol.HttpsOnly,
        string version = "2020-12-06")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Key);
        var key = Convert.FromBase64String(base64Key);

        var permissionsStr = PermissionsToString(permissions);
        var servicesStr = ServicesToString(services);
        var resourceTypesStr = ResourceTypesToString(resourceTypes);
        var startStr = start is null ? string.Empty : start.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var expiryStr = expiry.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var protocolStr = protocol switch
        {
            StorageSasProtocol.HttpsOnly => "https",
            StorageSasProtocol.HttpsAndHttp => "https,http",
            _ => string.Empty
        };

        var stringToSign =
            accountName + "\n" +
            permissionsStr + "\n" +
            servicesStr + "\n" +
            resourceTypesStr + "\n" +
            startStr + "\n" +
            expiryStr + "\n" +
            (ipRange ?? string.Empty) + "\n" +
            protocolStr + "\n" +
            version + "\n" +
            "\n"; // signedEncryptionScope (empty) + trailing newline

        using var hmac = new HMACSHA256(key);
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

        var sb = new StringBuilder();
        Append(sb, "sv", version);
        Append(sb, "ss", servicesStr);
        Append(sb, "srt", resourceTypesStr);
        Append(sb, "sp", permissionsStr);
        if (start is not null) Append(sb, "st", startStr);
        Append(sb, "se", expiryStr);
        if (!string.IsNullOrEmpty(ipRange)) Append(sb, "sip", ipRange);
        if (!string.IsNullOrEmpty(protocolStr)) Append(sb, "spr", protocolStr);
        Append(sb, "sig", signature);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string key, string value)
    {
        if (sb.Length > 0) sb.Append('&');
        sb.Append(key).Append('=').Append(HttpUtility.UrlEncode(value));
    }

    private static string PermissionsToString(StorageSasPermissions p)
    {
        // Order is significant per spec.
        Span<char> chars = stackalloc char[16];
        var i = 0;
        if (p.HasFlag(StorageSasPermissions.Read)) chars[i++] = 'r';
        if (p.HasFlag(StorageSasPermissions.Write)) chars[i++] = 'w';
        if (p.HasFlag(StorageSasPermissions.Delete)) chars[i++] = 'd';
        if (p.HasFlag(StorageSasPermissions.List)) chars[i++] = 'l';
        if (p.HasFlag(StorageSasPermissions.Add)) chars[i++] = 'a';
        if (p.HasFlag(StorageSasPermissions.Create)) chars[i++] = 'c';
        if (p.HasFlag(StorageSasPermissions.Update)) chars[i++] = 'u';
        if (p.HasFlag(StorageSasPermissions.Process)) chars[i++] = 'p';
        return new string(chars[..i]);
    }

    private static string ServicesToString(StorageSasServices s)
    {
        Span<char> chars = stackalloc char[4];
        var i = 0;
        if (s.HasFlag(StorageSasServices.Blob)) chars[i++] = 'b';
        if (s.HasFlag(StorageSasServices.Queue)) chars[i++] = 'q';
        if (s.HasFlag(StorageSasServices.Table)) chars[i++] = 't';
        if (s.HasFlag(StorageSasServices.File)) chars[i++] = 'f';
        return new string(chars[..i]);
    }

    private static string ResourceTypesToString(StorageSasResourceTypes r)
    {
        Span<char> chars = stackalloc char[3];
        var i = 0;
        if (r.HasFlag(StorageSasResourceTypes.Service)) chars[i++] = 's';
        if (r.HasFlag(StorageSasResourceTypes.Container)) chars[i++] = 'c';
        if (r.HasFlag(StorageSasResourceTypes.Object)) chars[i++] = 'o';
        return new string(chars[..i]);
    }
}

[Flags]
public enum StorageSasPermissions
{
    None = 0,
    Read = 1,
    Write = 1 << 1,
    Delete = 1 << 2,
    List = 1 << 3,
    Add = 1 << 4,
    Create = 1 << 5,
    Update = 1 << 6,
    Process = 1 << 7
}

[Flags]
public enum StorageSasServices
{
    None = 0,
    Blob = 1,
    Queue = 1 << 1,
    Table = 1 << 2,
    File = 1 << 3
}

[Flags]
public enum StorageSasResourceTypes
{
    None = 0,
    Service = 1,
    Container = 1 << 1,
    Object = 1 << 2
}

public enum StorageSasProtocol
{
    HttpsOnly,
    HttpsAndHttp
}
