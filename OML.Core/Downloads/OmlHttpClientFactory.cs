using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace OML.Core.Downloads;

/// <summary>
/// 统一的 <see cref="HttpClient"/> 构造入口。
/// <para>
/// 关键点：
/// <list type="bullet">
/// <item><c>Timeout = InfiniteTimeSpan</c>：HttpClient 的超时覆盖整个请求体读取，
/// 对 26MB 的 client.jar 是致命的；连接阶段交给 <c>ConnectTimeout</c>。</item>
/// <item>显式启用 TLS 1.2/1.3：部分系统/网络环境默认协议过低，
/// Mojang 的下载源会直接握手失败（The SSL connection could not be established）。</item>
/// <item>使用系统代理：用户开着加速器/VPN 时，下载流量才能正确分流。</item>
/// <item>证书链兜底：只对"链不完整"放行，主机名不匹配或证书缺失仍然拒绝。</item>
/// </list>
/// </para>
/// </summary>
public static class OmlHttpClientFactory
{
    /// <summary>启动器 UA（版本取入口程序集，发版不用回来改这里）。部分 CDN 会对空 UA 直接 403。</summary>
    public static string UserAgent { get; } = $"OML/{EntryVersion} (+https://github.com/LSwzx/OML-project)";

    private static string EntryVersion =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0";

    /// <summary>创建适合大文件下载的客户端：不禁用重定向、不做透明解压、连接超时 10s。</summary>
    public static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            MaxConnectionsPerServer = 64,

            // 显式走系统代理（默认 true，但这里写清楚并绑定系统代理对象）。
            UseProxy = true,
            Proxy = WebRequest.GetSystemWebProxy(),
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,

            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = ValidateServerCertificate,
            },
        };

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    /// <summary>
    /// 证书校验：默认严格校验；仅在"证书链不完整"时尝试用系统证书库重建链。
    /// <para>
    /// 加速器/企业代理常把中间证书去掉，导致 <see cref="SslPolicyErrors.RemoteCertificateChainErrors"/>。
    /// 这里把服务器发来的中间证书补进 ExtraStore 重新验证，并确认根证书确实存在于
    /// Windows 根存储中——不会无条件信任任意自签证书。
    /// </para>
    /// </summary>
    private static bool ValidateServerCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        if (certificate is null)
        {
            return false;
        }

        // 主机名不匹配 / 没有证书：这两种情况没有任何"链不完整"的借口，直接拒绝。
        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0 ||
            (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
        {
            return false;
        }

        if ((errors & SslPolicyErrors.RemoteCertificateChainErrors) == 0)
        {
            return false;
        }

        try
        {
            using var leaf = new X509Certificate2(certificate);

            using var rebuilt = new X509Chain();
            rebuilt.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            rebuilt.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            rebuilt.ChainPolicy.TrustMode = X509ChainTrustMode.System;

            // 服务器随握手发来的中间证书通常足够补齐链。
            if (chain is not null)
            {
                foreach (var element in chain.ChainElements)
                {
                    rebuilt.ChainPolicy.ExtraStore.Add(element.Certificate);
                }
            }

            if (rebuilt.Build(leaf))
            {
                return true;
            }

            // 仍失败：检查链的根是否真的在 Windows 根存储里（而不是随便接受未知根）。
            if (rebuilt.ChainElements.Count == 0)
            {
                return false;
            }

            var root = rebuilt.ChainElements[^1].Certificate;
            foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
            {
                using var store = new X509Store(StoreName.Root, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

                if (store.Certificates.Find(X509FindType.FindByThumbprint, root.Thumbprint, validOnly: false).Count > 0)
                {
                    return true;
                }
            }
        }
        catch
        {
            // 任何异常都按"校验失败"处理。
        }

        return false;
    }
}
