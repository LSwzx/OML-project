using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OML.Abstractions.Auth;
using OML.Abstractions.Downloads;
using OML.Core.Downloads;

namespace OML.Core.Auth;

/// <summary>
/// 微软正版登录（OAuth 2.0 设备代码流）。
/// <para>
/// 链路：设备码 → 用户浏览器授权 → MSA access token → Xbox Live → XSTS → Minecraft → 玩家档案。
/// 设备码流是桌面启动器的正确选择：不需要内嵌浏览器，也不需要回调地址。
/// </para>
/// <para>
/// <b>Client ID 必须是你自己注册的 Azure 应用</b>（Entra ID → 应用注册 → 允许公共客户端流）。
/// 不要复用其它启动器的 ID：那会把用户引导到别人的应用授权页，既违反微软条款，也无法保证长期可用。
/// 用环境变量 <c>OML_MICROSOFT_CLIENT_ID</c> 或构造参数注入。
/// </para>
/// </summary>
public sealed class MicrosoftAuthService : IAuthService, IDisposable
{
    /// <summary>消费者租户的 OAuth 端点前缀。</summary>
    public const string Authority = "https://login.microsoftonline.com/consumers/oauth2/v2.0";

    /// <summary>请求的作用域：Xbox Live 登录 + 可刷新。</summary>
    public const string Scope = "XboxLive.signin offline_access";

    /// <summary>环境变量名：注入 Azure 应用 Client ID。</summary>
    public const string ClientIdEnvironmentVariable = "OML_MICROSOFT_CLIENT_ID";

    /// <summary>
    /// 默认 Client ID（空）。启动器项目必须注册自己的应用；
    /// 这里不预置任何第三方 ID，避免把用户导向他人的授权页。
    /// </summary>
    public const string DefaultClientId = "";

    private const string XboxLiveUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string MinecraftLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string MinecraftProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

    private static readonly Regex AadCodePattern = new(@"AADSTS(?<code>\d+)", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    /// <param name="clientId">Azure 应用 Client ID；为 <c>null</c> 时读环境变量。</param>
    /// <param name="httpClient">可注入共享客户端。</param>
    public MicrosoftAuthService(string? clientId = null, HttpClient? httpClient = null)
    {
        ClientId = ResolveClientId(clientId);
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? OmlHttpClientFactory.Create();
    }

    /// <summary>实际使用的 Client ID。</summary>
    public string ClientId { get; }

    /// <summary>解析 Client ID：显式参数 → 环境变量 → 抛异常并给出注册指引。</summary>
    public static string ResolveClientId(string? explicitClientId = null)
    {
        var candidate = !string.IsNullOrWhiteSpace(explicitClientId)
            ? explicitClientId.Trim()
            : Environment.GetEnvironmentVariable(ClientIdEnvironmentVariable)?.Trim();

        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        throw new InvalidOperationException(
            "未配置微软应用的 Client ID。请先注册 Azure 应用（Entra ID → 应用注册 → 身份验证 → 允许公共客户端流），" +
            $"然后设置环境变量 {ClientIdEnvironmentVariable} 或通过构造参数传入。");
    }

    /// <summary>
    /// 发起设备码请求。返回后应立即把 <see cref="MicrosoftDeviceCode.UserCode"/> 与
    /// <see cref="MicrosoftDeviceCode.VerificationUri"/> 展示给用户，然后调用
    /// <see cref="WaitForAuthorizationAsync"/> 轮询。
    /// </summary>
    public async Task<MicrosoftDeviceCode> StartDeviceCodeAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http
            .PostAsync(
                $"{Authority}/devicecode",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["scope"] = Scope,
                }),
                cancellationToken)
            .ConfigureAwait(false);

        var json = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        ThrowIfError(json, response);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var expiresIn = root.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 900;
        var interval = root.TryGetProperty("interval", out var intervalNode) ? intervalNode.GetInt32() : 5;

        return new MicrosoftDeviceCode
        {
            DeviceCode = GetString(root, "device_code") ?? throw new InvalidDataException("设备码响应缺少 device_code。"),
            UserCode = GetString(root, "user_code") ?? throw new InvalidDataException("设备码响应缺少 user_code。"),
            VerificationUri = GetString(root, "verification_uri") ?? "https://microsoft.com/link",
            VerificationUriComplete = GetString(root, "verification_uri_complete"),
            Message = GetString(root, "message"),
            ExpiresIn = expiresIn,
            Interval = interval,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
        };
    }

    /// <summary>
    /// 轮询授权结果并完成 Xbox / Minecraft 链路。
    /// <para>会把 <c>authorization_pending</c> 当作"继续等"，<c>slow_down</c> 自动延长间隔。</para>
    /// </summary>
    public async Task<MicrosoftSession> WaitForAuthorizationAsync(
        MicrosoftDeviceCode code,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);

        var interval = TimeSpan.FromSeconds(Math.Max(1, code.Interval));
        status?.Report("等待在浏览器中完成授权…");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (DateTimeOffset.UtcNow >= code.ExpiresAt)
            {
                throw new MicrosoftAuthException("设备代码已过期，请重新发起登录。", "expired_token");
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

            var tokenJson = await RequestTokenAsync(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = ClientId,
                    ["device_code"] = code.DeviceCode,
                },
                cancellationToken).ConfigureAwait(false);

            if (tokenJson.ErrorCode is null)
            {
                status?.Report("授权成功，正在登录 Xbox / Minecraft…");
                return await CompleteLoginAsync(tokenJson.AccessToken!, tokenJson.RefreshToken!, cancellationToken)
                    .ConfigureAwait(false);
            }

            switch (tokenJson.ErrorCode)
            {
                case "authorization_pending":
                    status?.Report("尚未完成授权，继续等待…");
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    status?.Report("请求过快，已自动放慢轮询…");
                    continue;
                case "authorization_declined":
                    throw new MicrosoftAuthException("用户拒绝了授权。", tokenJson.ErrorCode);
                case "expired_token":
                    throw new MicrosoftAuthException("设备代码已过期，请重新发起登录。", tokenJson.ErrorCode);
                case "bad_verification_code":
                    throw new MicrosoftAuthException("设备代码无效。", tokenJson.ErrorCode);
                default:
                    throw new MicrosoftAuthException(
                        $"获取令牌失败：{tokenJson.ErrorDescription}", tokenJson.ErrorCode, tokenJson.AadErrorCode);
            }
        }
    }

    /// <summary>用刷新令牌静默续期（启动器下次启动时免登录）。</summary>
    public async Task<MicrosoftSession> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        var tokenJson = await RequestTokenAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ClientId,
                ["refresh_token"] = refreshToken,
                ["scope"] = Scope,
            },
            cancellationToken).ConfigureAwait(false);

        if (tokenJson.ErrorCode is not null)
        {
            throw new MicrosoftAuthException(
                $"刷新令牌失败：{tokenJson.ErrorDescription}", tokenJson.ErrorCode, tokenJson.AadErrorCode);
        }

        // 微软可能轮换 refresh token，优先用新的。
        var newRefresh = tokenJson.RefreshToken ?? refreshToken;
        return await CompleteLoginAsync(tokenJson.AccessToken!, newRefresh, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="IAuthService"/> 入口：微软类型下 <c>credential</c> 视为 refresh token。
    /// </summary>
    public async Task<UserProfile> AuthenticateAsync(
        string type,
        string credential,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(type, AuthTypes.Microsoft, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentOutOfRangeException(
                nameof(type), type, $"MicrosoftAuthService 只支持 {AuthTypes.Microsoft}。");
        }

        var session = await RefreshAsync(credential, cancellationToken).ConfigureAwait(false);
        return session.Profile;
    }

    // ---------------------------------------------------------------- Xbox 链路

    private async Task<MicrosoftSession> CompleteLoginAsync(
        string msaAccessToken,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var (xblToken, userHash) = await AuthenticateXboxLiveAsync(msaAccessToken, cancellationToken).ConfigureAwait(false);
        var (xstsToken, xstsUserHash) = await AuthorizeXstsAsync(xblToken, cancellationToken).ConfigureAwait(false);

        var minecraftToken = await LoginMinecraftAsync(xstsUserHash ?? userHash, xstsToken, cancellationToken)
            .ConfigureAwait(false);

        var (uuid, name) = await GetProfileAsync(minecraftToken.AccessToken, cancellationToken).ConfigureAwait(false);

        return new MicrosoftSession(
            new UserProfile
            {
                Username = name,
                Uuid = uuid,
                AccessToken = minecraftToken.AccessToken,
                SkinUrl = null,
                Type = AuthTypes.Microsoft,
            },
            refreshToken,
            minecraftToken.ExpiresAt);
    }

    private async Task<(string Token, string UserHash)> AuthenticateXboxLiveAsync(
        string msaAccessToken,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                // MSA v2 令牌需要 "d=" 前缀，这是 Xbox Live 的约定。
                RpsTicket = "d=" + msaAccessToken,
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
        };

        var json = await PostJsonAsync(XboxLiveUrl, payload, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);

        var token = document.RootElement.GetProperty("Token").GetString()
            ?? throw new MicrosoftAuthException("Xbox Live 认证未返回 Token。");

        var userHash = document.RootElement
            .GetProperty("DisplayClaims")
            .GetProperty("xui")[0]
            .GetProperty("uhs")
            .GetString() ?? string.Empty;

        return (token, userHash);
    }

    private async Task<(string Token, string? UserHash)> AuthorizeXstsAsync(
        string xblToken,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xblToken },
            },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT",
        };

        var json = await PostJsonAsync(XstsUrl, payload, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);

        var token = document.RootElement.GetProperty("Token").GetString()
            ?? throw new MicrosoftAuthException("XSTS 授权未返回 Token。");

        var userHash = document.RootElement.TryGetProperty("DisplayClaims", out var claims) &&
                       claims.TryGetProperty("xui", out var xui) &&
                       xui.GetArrayLength() > 0 &&
                       xui[0].TryGetProperty("uhs", out var uhs)
            ? uhs.GetString()
            : null;

        return (token, userHash);
    }

    private async Task<(string AccessToken, DateTimeOffset ExpiresAt)> LoginMinecraftAsync(
        string userHash,
        string xstsToken,
        CancellationToken cancellationToken)
    {
        var payload = new { identityToken = $"XBL3.0 x={userHash};{xstsToken}" };
        var json = await PostJsonAsync(MinecraftLoginUrl, payload, cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        var accessToken = document.RootElement.GetProperty("access_token").GetString()
            ?? throw new MicrosoftAuthException("Minecraft 登录未返回 access_token。");

        var expiresIn = document.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 86_400;
        return (accessToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }

    private async Task<(string Uuid, string Name)> GetProfileAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, MinecraftProfileUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new MicrosoftAuthException(
                "该账号没有 Minecraft: Java Edition 的所有权（或档案尚未创建）。", "no_minecraft_profile");
        }

        var json = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        ThrowIfError(json, response);

        using var document = JsonDocument.Parse(json);
        var id = document.RootElement.GetProperty("id").GetString()
            ?? throw new MicrosoftAuthException("玩家档案缺少 id。");
        var name = document.RootElement.GetProperty("name").GetString()
            ?? throw new MicrosoftAuthException("玩家档案缺少 name。");

        return (FormatUuid(id), name);
    }

    // ---------------------------------------------------------------- HTTP 工具

    private async Task<TokenResult> RequestTokenAsync(
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var response = await _http
            .PostAsync($"{Authority}/token", new FormUrlEncodedContent(form), cancellationToken)
            .ConfigureAwait(false);

        var json = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var error = GetString(root, "error");
        if (error is not null)
        {
            var description = GetString(root, "error_description") ?? error;
            return new TokenResult(null, null, error, description, ParseAadCode(description));
        }

        return new TokenResult(
            GetString(root, "access_token"),
            GetString(root, "refresh_token"),
            null,
            null,
            null);
    }

    private async Task<string> PostJsonAsync(string url, object payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateHttpError(json, response);
        }

        return json;
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

    private static void ThrowIfError(string json, HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw CreateHttpError(json, response);
    }

    private static MicrosoftAuthException CreateHttpError(string json, HttpResponseMessage response)
    {
        string? error = null;
        string? description = null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            error = GetString(root, "error");
            description = GetString(root, "error_description");
        }
        catch (JsonException)
        {
            // 非 JSON 错误（网关 502 之类）。
        }

        // Xbox / XSTS 的错误是另一个形状。
        if (error is null)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("XErr", out var xerr))
                {
                    var code = xerr.GetInt64();
                    throw new MicrosoftAuthException(DescribeXboxError(code), "xbox_error");
                }
            }
            catch (JsonException)
            {
                // 忽略。
            }
        }

        var message = description ?? (string.IsNullOrWhiteSpace(json)
            ? $"HTTP {(int)response.StatusCode}"
            : json[..Math.Min(300, json.Length)]);

        // Minecraft API 对第三方应用有白名单：未提交审核的应用会在这里被 403 拦下。
        var allowlistHint = DescribeMinecraftApiError(json);
        if (allowlistHint is not null)
        {
            message = allowlistHint;
        }

        return new MicrosoftAuthException(
            $"微软认证请求失败（HTTP {(int)response.StatusCode}）：{message}",
            error,
            ParseAadCode(description));
    }

    /// <summary>
    /// 识别 Mojang 的应用白名单错误。
    /// <para>
    /// 自建 Azure 应用能拿到微软令牌、也能通过 Xbox/XSTS，但调用
    /// <c>login_with_xbox</c> 时仍会 403 <c>Invalid app registration</c>——
    /// Mojang 要求第三方启动器提交审核（<c>https://aka.ms/mce-reviewappid</c>），
    /// 通过后同一个 Client ID 才能使用 Minecraft API。
    /// </para>
    /// </summary>
    public static string? DescribeMinecraftApiError(string? responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return null;
        }

        if (responseJson.Contains("Invalid app registration", StringComparison.OrdinalIgnoreCase) ||
            responseJson.Contains("mce-reviewappid", StringComparison.OrdinalIgnoreCase))
        {
            return "该 Azure 应用尚未通过 Mojang 的第三方应用审核，无法调用 Minecraft API。" +
                   "请到 https://aka.ms/mce-reviewappid 提交申请（审核通过后同一个 Client ID 即可使用）。";
        }

        return null;
    }

    private static string DescribeXboxError(long code) => code switch
    {
        2148916233 => "该微软账号还没有 Xbox 档案，请先到 xbox.com 创建。",
        2148916235 => "该账号所在地区不支持 Xbox Live。",
        2148916238 => "未成年账号需要先加入 Microsoft 家庭组。",
        _ => $"Xbox 认证失败（XErr={code}）。",
    };

    private static int? ParseAadCode(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var match = AadCodePattern.Match(text);
        return match.Success && int.TryParse(match.Groups["code"].Value, out var code) ? code : null;
    }

    private static string? GetString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;

    /// <summary>把 Minecraft 的 32 位无连字符 ID 格式化成标准 UUID。</summary>
    public static string FormatUuid(string id) => UuidText.Format(id);

    private sealed record TokenResult(
        string? AccessToken,
        string? RefreshToken,
        string? ErrorCode,
        string? ErrorDescription,
        int? AadErrorCode);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
