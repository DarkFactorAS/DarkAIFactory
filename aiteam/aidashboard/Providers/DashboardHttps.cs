using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace AiDashboard.Auth;

/// <summary>
/// Serves the dashboard over HTTPS outside Development, because login is refused on plain HTTP
/// (see <see cref="AuthEndpoints"/>) and the session cookies are Secure-only. Kestrel listens with
/// TLS on the container port (8080 by default, <c>Https:Port</c> to change it). The certificate is
/// the one configured in <c>Kestrel:Certificates:Default</c> (Path/Password, or the matching
/// <c>Kestrel__Certificates__Default__*</c> environment variables); without one, a self-signed
/// certificate is generated at startup, so the connection is encrypted but browsers warn once.
/// Development keeps the normal URLs (plain HTTP behind the Vite dev proxy).
/// </summary>
public static class DashboardHttps
{
    public const int DefaultPort = 8080;
    public const string CertificatePathKey = "Kestrel:Certificates:Default:Path";
    public const string PortKey = "Https:Port";

    public static WebApplicationBuilder UseDashboardHttps(this WebApplicationBuilder builder)
    {
        if (builder.Environment.IsDevelopment())
        {
            return builder;
        }

        var port = builder.Configuration.GetValue(PortKey, DefaultPort);
        var hasConfiguredCertificate = !string.IsNullOrWhiteSpace(builder.Configuration[CertificatePathKey]);
        X509Certificate2? selfSigned = hasConfiguredCertificate ? null : CreateSelfSignedCertificate();

        builder.WebHost.ConfigureKestrel(options =>
            options.ListenAnyIP(port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                if (selfSigned is null)
                {
                    listen.UseHttps();
                }
                else
                {
                    listen.UseHttps(selfSigned);
                }
            }));
        return builder;
    }

    /// <summary>Self-signed server certificate for localhost and this machine, valid for one year.</summary>
    public static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=aidashboard", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddDnsName(Environment.MachineName);
        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false)); // Server authentication

        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(1));
        // Round-trip through PKCS#12 so the private key is usable by SslStream on every platform.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null);
    }
}
