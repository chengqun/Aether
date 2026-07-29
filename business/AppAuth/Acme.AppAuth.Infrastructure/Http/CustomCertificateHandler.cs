using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;

namespace Acme.AppAuth.Infrastructure.Http;

/// <summary>
/// Loads the embedded rootCA.crt self-signed CA certificate and creates an HttpMessageHandler
/// that trusts it. Solves SSL connection issues on Windows when connecting to servers using
/// self-signed certificates (e.g. internal/test environments).
/// </summary>
public static class CustomCertificateHandler
{
    private static X509Certificate2Collection? _trustedCerts;
    private static bool _initialized;

    /// <summary>
    /// Initializes the handler by loading the embedded rootCA.crt from assembly resources.
    /// Call this once at application startup before creating any HttpClient instances.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            _trustedCerts = new X509Certificate2Collection();

            var assembly = typeof(CustomCertificateHandler).Assembly;
            // List all embedded resources for debugging
            var allResources = assembly.GetManifestResourceNames();
            Debug.WriteLine($"[CustomCertificateHandler] Assembly: {assembly.FullName}");
            Debug.WriteLine($"[CustomCertificateHandler] Embedded resources: {string.Join(", ", allResources)}");

            // Try both with and without namespace prefix
            using var stream = assembly.GetManifestResourceStream("rootCA.crt")
                ?? assembly.GetManifestResourceStream($"{assembly.GetName().Name}.rootCA.crt");

            if (stream == null)
            {
                Debug.WriteLine("[CustomCertificateHandler] rootCA.crt not found as embedded resource.");
                Console.WriteLine("[CustomCertificateHandler] WARNING: rootCA.crt not found as embedded resource.");
                return;
            }

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var certData = ms.ToArray();
            Debug.WriteLine($"[CustomCertificateHandler] Read {certData.Length} bytes from embedded cert stream.");

            var cert = X509CertificateLoader.LoadCertificate(certData);
            _trustedCerts.Add(cert);

            Debug.WriteLine($"[CustomCertificateHandler] Loaded embedded CA certificate: Subject={cert.Subject}, Thumbprint={cert.Thumbprint}");
            Console.WriteLine($"[CustomCertificateHandler] Loaded embedded CA certificate: {cert.Subject}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CustomCertificateHandler] Failed to initialize: {ex}");
            Console.WriteLine($"[CustomCertificateHandler] Failed to initialize: {ex.Message}");
            _trustedCerts = null;
        }
    }

    /// <summary>
    /// Creates an HttpMessageHandler that trusts the embedded CA certificate.
    /// If no custom cert was loaded, returns a default SocketsHttpHandler.
    /// </summary>
    public static HttpMessageHandler CreateHandler()
    {
        var handler = new SocketsHttpHandler();

        if (_trustedCerts != null && _trustedCerts.Count > 0)
        {
            Debug.WriteLine("[CustomCertificateHandler] Creating handler with custom CA trust (certificate will be validated against embedded rootCA.crt)");
            handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
                {
                    if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.None)
                        return true;

                    Debug.WriteLine($"[CustomCertificateHandler] SSL validation error: {sslPolicyErrors}");

                    if (certificate != null && chain != null)
                    {
                        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                        chain.ChainPolicy.CustomTrustStore.AddRange(_trustedCerts);
                        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

                        using var cert2 = new X509Certificate2(certificate);
                        var result = chain.Build(cert2);
                        Debug.WriteLine($"[CustomCertificateHandler] Custom chain build result: {result}");
                        return result;
                    }

                    return false;
                }
            };
        }
        else
        {
            Debug.WriteLine("[CustomCertificateHandler] No custom certificates loaded, using default handler.");
        }

        return handler;
    }
}
