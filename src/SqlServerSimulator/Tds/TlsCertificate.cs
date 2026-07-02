using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SqlServerSimulator.Tds;

/// <summary>Creates the self-signed server certificate used for TLS-encrypted connections.</summary>
public static class TlsCertificate
{
    public static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") /* server auth */ }, critical: false));
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        foreach (var address in GetLocalIpAddresses())
            sanBuilder.AddIpAddress(address);
        request.CertificateExtensions.Add(sanBuilder.Build());

        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        // Round-trip through PKCS#12 so the private key is usable by SslStream on Windows
        // (ephemeral keys from CreateSelfSigned are rejected by SChannel).
        return X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pfx), password: null,
            X509KeyStorageFlags.Exportable);
    }

    private static IEnumerable<IPAddress> GetLocalIpAddresses()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;

            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                    continue;
                if (address.Address.IsIPv6LinkLocal)
                    continue;
                if (seen.Add(address.Address.ToString()))
                    yield return address.Address;
            }
        }

        if (seen.Add(IPAddress.Loopback.ToString()))
            yield return IPAddress.Loopback;
    }
}
