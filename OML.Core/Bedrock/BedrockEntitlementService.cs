using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using OML.Core.Downloads;

namespace OML.Core.Bedrock;

/// <summary>
/// 基岩版正版验证：查询 <c>entitlements/mcstore</c> 是否包含
/// <c>product_minecraft_bedrock</c>。
/// </summary>
public sealed class BedrockEntitlementService : IDisposable
{
    private const string McStoreUrl = "https://api.minecraftservices.com/entitlements/mcstore";

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public BedrockEntitlementService(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? OmlHttpClientFactory.Create();
    }

    /// <summary>检查令牌对应的账号是否拥有基岩版。</summary>
    /// <param name="minecraftAccessToken">微软登录链路换到的 Minecraft access token。</param>
    public async Task<BedrockEntitlementResult> CheckAsync(
        string? minecraftAccessToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(minecraftAccessToken))
        {
            return new BedrockEntitlementResult(
                BedrockEntitlementStatus.NotSignedIn,
                "启动基岩版需要微软正版账号。请先在「账号」页登录微软账号。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, McStoreUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", minecraftAccessToken);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new BedrockEntitlementResult(
                    BedrockEntitlementStatus.NotSignedIn,
                    "微软登录已过期，请重新登录后再启动基岩版。");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new BedrockEntitlementResult(
                    BedrockEntitlementStatus.Error,
                    $"授权检查失败（HTTP {(int)response.StatusCode}），请稍后重试。");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return new BedrockEntitlementResult(
                    BedrockEntitlementStatus.NotOwned,
                    "未在账号授权中找到基岩版，请确认已购买 Minecraft for Windows。");
            }

            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("name", out var name) &&
                    name.GetString()?.Contains("product_minecraft_bedrock", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return new BedrockEntitlementResult(
                        BedrockEntitlementStatus.Owned,
                        "已拥有基岩版授权。");
                }
            }

            return new BedrockEntitlementResult(
                BedrockEntitlementStatus.NotOwned,
                "该微软账号未购买基岩版（Minecraft for Windows）。请先在 Microsoft Store 购买后再启动。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new BedrockEntitlementResult(
                BedrockEntitlementStatus.Error,
                $"授权检查失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
