using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ScriptDesk.Catalogue;

// Same provider format and compiled trust as the desktop. No service secret or
// request-supplied root, endpoint, device ID or expiry is accepted.
internal static class CatalogueAuthorization
{
#if HEX_TEST_BUILD
    internal const string Composition="Isolated regression build";
#endif
    static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property,out var value) && value.ValueKind==JsonValueKind.String ? value.GetString()! : "";
    static string Relation(JsonElement value,string name) =>
        value.GetProperty("relationships").GetProperty(name).GetProperty("data").GetProperty("id").GetString()!;
    static DateTimeOffset Time(JsonElement element,string property) =>
        DateTimeOffset.Parse(Text(element,property),System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeUniversal).ToUniversalTime();
    internal static JsonDocument Trust()
    {
        using var stream=typeof(CatalogueAuthorization).Assembly.GetManifestResourceStream("HEX.LicenseTrust")!;
        return JsonDocument.Parse(stream);
    }
    internal static string Device(string product)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Windows device identity required.");
        var id=Convert.ToString(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography","MachineGuid",null));
        if(string.IsNullOrWhiteSpace(id))throw new InvalidDataException("Device identity unavailable.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hex-device-v1|"+id+"|"+product))).ToLowerInvariant();
    }
    internal static void Verify(string certificate,JsonElement trust,string fingerprint,DateTimeOffset now)
    {
        string account=Text(trust,"account"),product=Text(trust,"product"),environment=Text(trust,"environment");
        var roots=trust.GetProperty("keys").EnumerateArray().ToArray();
        if(account==""||product==""||environment==""||roots.Length==0||certificate.Length>262144)throw new InvalidDataException("Paid licensing setup pending.");
        const string begin="-----BEGIN MACHINE FILE-----",end="-----END MACHINE FILE-----";
        certificate=certificate.Trim();
        if(!certificate.StartsWith(begin,StringComparison.Ordinal)||!certificate.EndsWith(end,StringComparison.Ordinal))throw new InvalidDataException("Signed authorization required.");
        using var envelope=JsonDocument.Parse(Convert.FromBase64String(certificate.Substring(begin.Length,certificate.Length-begin.Length-end.Length)));
        var e=envelope.RootElement;
        if(Text(e,"alg")!="base64+rsa-sha256")throw new InvalidDataException("Unexpected authorization algorithm.");
        string encoded=Text(e,"enc");byte[] signature=Convert.FromBase64String(Text(e,"sig"));
        bool valid=false;
        foreach(var root in roots)
        {
            using var rsa=RSA.Create();rsa.FromXmlString(root.GetString()!);
            if(rsa.KeySize>=2048&&rsa.VerifyData(Encoding.UTF8.GetBytes("machine/"+encoded),signature,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1)){valid=true;break;}
        }
        if(!valid)throw new InvalidDataException("Authorization signature is not trusted.");
        using var payload=JsonDocument.Parse(Convert.FromBase64String(encoded));
        var p=payload.RootElement;var machine=p.GetProperty("data");var attributes=machine.GetProperty("attributes");
        if(Text(machine,"type")!="machines"||Text(attributes,"fingerprint")!=fingerprint||Relation(machine,"account")!=account||Relation(machine,"product")!=product||Relation(machine,"environment")!=environment)throw new InvalidDataException("Authorization scope or device mismatch.");
        var license=p.GetProperty("included").EnumerateArray().Single(x=>Text(x,"type")=="licenses"&&Text(x,"id")==Relation(machine,"license"));
        var la=license.GetProperty("attributes");
        if(Relation(license,"product")!=product||Relation(license,"environment")!=environment||
           la.TryGetProperty("suspended",out var suspended)&&suspended.ValueKind==JsonValueKind.True||
           new[]{"SUSPENDED","REVOKED","EXPIRED","BANNED"}.Contains(Text(la,"status").ToUpperInvariant()))throw new InvalidDataException("License unavailable.");
        var meta=p.GetProperty("meta");var issued=Time(meta,"issued");var expiry=Time(meta,"expiry");var paid=Time(la,"expiry");
        if(issued>now||expiry<=now||paid<=now||expiry<=issued||expiry-issued>TimeSpan.FromHours(4))throw new InvalidDataException("Authorization expired or future dated.");
        var metadata=la.GetProperty("metadata");var capabilities=metadata.GetProperty("hexCapabilities").EnumerateArray().Select(x=>x.GetString()).ToHashSet(StringComparer.Ordinal);
        if(Text(metadata,"hexProduct")!=product||Text(metadata,"hexEnvironment")!=environment||!capabilities.Contains("app.access")||!capabilities.Contains("catalogue.read"))throw new InvalidDataException("Paid catalogue access required.");
    }
    internal static void Require(JsonElement request)
    {
#if HEX_TEST_BUILD
        return; // Isolated regression build; never packaged as a commercial helper.
#else
        using var trust=Trust();
        Verify(Text(request,"licenseProof"),trust.RootElement,Device(Text(trust.RootElement,"product")),DateTimeOffset.UtcNow);
#endif
    }
}
