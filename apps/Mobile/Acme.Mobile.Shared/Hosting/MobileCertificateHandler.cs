using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;

namespace Acme.Mobile.Shared.Hosting;

/// <summary>
/// 从 app 包加载 rootCA.crt 自签名 CA 证书并创建信任它的 <see cref="HttpMessageHandler"/>。
/// 镜像桌面 <c>CustomCertificateHandler</c>（在 AppAuth.Infrastructure），但桌面版从程序集内嵌资源读、
/// 手机端从 MAUI 资产包读（<c>FileSystem.OpenAppPackageFileAsync</c>）。
/// </summary>
public static class MobileCertificateHandler
{
    private static X509Certificate2Collection? _trustedCerts;
    private static bool _initialized;

    /// <summary>启动期调用一次：从 app 包加载 rootCA.crt。找不到则降级为默认 handler。</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            // MauiAsset LogicalName="rootCA.crt"（Resources/Raw/rootCA.crt）
            using var stream = FileSystem.OpenAppPackageFileAsync("rootCA.crt").GetAwaiter().GetResult();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var cert = X509CertificateLoader.LoadCertificate(ms.ToArray());

            _trustedCerts = new X509Certificate2Collection { cert };
            Debug.WriteLine($"[MobileCertificateHandler] Loaded CA: {cert.Subject}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MobileCertificateHandler] rootCA.crt not loaded (using default handler): {ex.Message}");
            _trustedCerts = null;
        }
    }

    /// <summary>创建信任内嵌 CA 的 handler；未加载则返回默认 SocketsHttpHandler。</summary>
    public static HttpMessageHandler CreateHandler()
    {
        var handler = new SocketsHttpHandler();

        if (_trustedCerts is { Count: > 0 })
        {
            handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
                {
                    if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.None)
                        return true;
                    if (certificate != null && chain != null)
                    {
                        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                        chain.ChainPolicy.CustomTrustStore.AddRange(_trustedCerts);
                        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        using var cert2 = new X509Certificate2(certificate);
                        return chain.Build(cert2);
                    }
                    return false;
                }
            };
        }
        return handler;
    }
}
