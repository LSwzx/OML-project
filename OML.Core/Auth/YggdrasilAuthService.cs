using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OML.Abstractions.Auth;
using OML.Core.Downloads;

namespace OML.Core.Auth;

/// <summary>
/// 外置登录（Yggdrasil）认证，默认对接 LittleSkin。
/// <para>
/// 协议：<c>/authserver/authenticate</c>、<c>/authserver/refresh</c>、<c>/authserver/validate</c>。
/// 响应里的 UUID 是 32 位无连字符形态，转 <see cref="UserProfile"/> 时统一格式化。
/// </para>
/// <para>
/// <b>错误码坑：</b>用户名密码错误与令牌失效在 Yggdrasil 里通常都返回 <b>403</b>
/// （<c>ForbiddenOperationException</c>），不是 401。判断凭据错误要看
/// <see cref="YggdrasilException.IsCredentialError"/>。
/// </para>
/// </summary>
public sealed class YggdrasilAuthService : IAuthService, IDisposable
{
    /// <summary>LittleSkin 的 Yggdrasil 地址。</summary>
    public const string DefaultBaseUrl = "https://littleskin.cn/api/yggdrasil";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    /// <param name="baseUrl">Yggdrasil 根地址（不含 <c>/authserver</c>）。</param>
    /// <param name="httpClient">可注入共享客户端。</param>
    /// <param name="clientToken">客户端令牌；不传则随机生成一个并在此实例内复用。</param>
    public YggdrasilAuthService(string? baseUrl = null, HttpClient? httpClient = null, string? clientToken = null)
    {
        BaseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? OmlHttpClientFactory.Create();
        ClientToken = string.IsNullOrWhiteSpace(clientToken) ? Guid.NewGuid().ToString("N") : clientToken;
    }

    /// <summary>服务端根地址。</summary>
    public string BaseUrl { get; }

    /// <summary>本实例使用的 clientToken。</summary>
    public string ClientToken { get; }

    /// <summary>读取服务端元信息（连通性检测；也用于展示"登录到哪个皮肤站"）。</summary>
    public async Task<YggdrasilServerInfo> GetServerInfoAsync(CancellationToken cancellationToken = default)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, BaseUrl, null, cancellationToken).ConfigureAwait(false);
        if (status != HttpStatusCode.OK)
        {
            throw CreateException(status, body, "读取 Yggdrasil 服务端信息失败");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var serverName = "Unknown";
        string? implementation = null;
        string? homepage = null;

        if (root.TryGetProperty("meta", out var meta))
        {
            serverName = GetString(meta, "serverName") ?? serverName;
            implementation = GetString(meta, "implementationName");

            if (meta.TryGetProperty("links", out var links))
            {
                homepage = GetString(links, "homepage");
            }
        }

        return new YggdrasilServerInfo(serverName, implementation, homepage);
    }

    /// <summary>
    /// 用户名密码登录。
    /// </summary>
    /// <exception cref="YggdrasilException">凭据错误或服务端异常。</exception>
    public async Task<YggdrasilSession> LoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var payload = new
        {
            agent = new { name = "Minecraft", version = 1 },
            username,
            password,
            clientToken = ClientToken,
            requestUser = true,
        };

        var (status, body) = await SendAsync(
            HttpMethod.Post,
            $"{BaseUrl}/authserver/authenticate",
            payload,
            cancellationToken).ConfigureAwait(false);

        if (status != HttpStatusCode.OK)
        {
            throw CreateException(status, body, "登录失败");
        }

        return await ParseSessionAsync(body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>用 accessToken 静默续期。</summary>
    public async Task<YggdrasilSession> RefreshAsync(
        string accessToken,
        string? clientToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        var payload = new
        {
            accessToken,
            clientToken = clientToken ?? ClientToken,
            requestUser = true,
        };

        var (status, body) = await SendAsync(
            HttpMethod.Post,
            $"{BaseUrl}/authserver/refresh",
            payload,
            cancellationToken).ConfigureAwait(false);

        if (status != HttpStatusCode.OK)
        {
            throw CreateException(status, body, "刷新令牌失败");
        }

        return await ParseSessionAsync(body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>校验令牌是否仍然有效（服务端返回 204 表示有效）。</summary>
    public async Task<bool> ValidateAsync(
        string accessToken,
        string? clientToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        var payload = new
        {
            accessToken,
            clientToken = clientToken ?? ClientToken,
        };

        var (status, _) = await SendAsync(
            HttpMethod.Post,
            $"{BaseUrl}/authserver/validate",
            payload,
            cancellationToken).ConfigureAwait(false);

        return status is HttpStatusCode.NoContent or HttpStatusCode.OK;
    }

    /// <summary>
    /// <see cref="IAuthService"/> 入口。<paramref name="credential"/> 形如 <c>邮箱:密码</c>。
    /// </summary>
    public async Task<UserProfile> AuthenticateAsync(
        string type,
        string credential,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(type, AuthTypes.LittleSkin, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(type, "Yggdrasil", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentOutOfRangeException(
                nameof(type), type, $"YggdrasilAuthService 只支持 {AuthTypes.LittleSkin} 或 Yggdrasil。");
        }

        var separator = credential?.IndexOf(':') ?? -1;
        if (separator <= 0 || separator >= credential!.Length - 1)
        {
            throw new ArgumentException("凭证格式应为 \"邮箱:密码\"。", nameof(credential));
        }

        var username = credential[..separator];
        var password = credential[(separator + 1)..];

        var session = await LoginAsync(username, password, cancellationToken).ConfigureAwait(false);
        return session.Profile;
    }

    // ---------------------------------------------------------------- 内部

    private async Task<YggdrasilSession> ParseSessionAsync(string body, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var accessToken = GetString(root, "accessToken")
            ?? throw new YggdrasilException("响应缺少 accessToken。", HttpStatusCode.OK);
        var clientToken = GetString(root, "clientToken") ?? ClientToken;

        var available = new List<UserProfile>();
        if (root.TryGetProperty("availableProfiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
        {
            foreach (var profileNode in profiles.EnumerateArray())
            {
                available.Add(ToProfile(profileNode));
            }
        }

        UserProfile selected;
        if (root.TryGetProperty("selectedProfile", out var selectedNode) && selectedNode.ValueKind == JsonValueKind.Object)
        {
            selected = ToProfile(selectedNode);
        }
        else if (available.Count > 0)
        {
            // 有些服务端不带 selectedProfile，退化用第一个角色。
            selected = available[0];
        }
        else
        {
            throw new YggdrasilException(
                "该账号没有可用的 Minecraft 角色，请先在皮肤站创建角色。", HttpStatusCode.OK);
        }

        await EnrichSkinAsync(selected, cancellationToken).ConfigureAwait(false);

        return new YggdrasilSession(selected, accessToken, clientToken, available);
    }

    private static UserProfile ToProfile(JsonElement node)
    {
        var id = GetString(node, "id") ?? string.Empty;
        var name = GetString(node, "name") ?? string.Empty;

        return new UserProfile
        {
            Username = name,
            Uuid = UuidText.Format(id),
            AccessToken = null,
            SkinUrl = null,
            SkinModel = null,
            Type = AuthTypes.LittleSkin,
        };
    }

    /// <summary>
    /// 拉取皮肤信息（头像 URL + 模型）。失败不影响登录——皮肤只是展示层。
    /// </summary>
    private async Task EnrichSkinAsync(UserProfile profile, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.Uuid))
        {
            return;
        }

        try
        {
            var rawId = profile.Uuid.Replace("-", string.Empty);
            var (status, body) = await SendAsync(
                HttpMethod.Get,
                $"{BaseUrl}/sessionserver/session/minecraft/profile/{rawId}",
                null,
                cancellationToken).ConfigureAwait(false);

            if (status != HttpStatusCode.OK)
            {
                return;
            }

            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("properties", out var properties) ||
                properties.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var property in properties.EnumerateArray())
            {
                if (!string.Equals(GetString(property, "name"), "textures", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = GetString(property, "value");
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                ApplyTextures(profile, value!);
                return;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException)
        {
            // 皮肤拿不到就留空，不阻断登录。
        }
    }

    private static void ApplyTextures(UserProfile profile, string base64Value)
    {
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64Value));

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("textures", out var textures) ||
            !textures.TryGetProperty("SKIN", out var skin))
        {
            return;
        }

        profile.SkinUrl = GetString(skin, "url");

        if (skin.TryGetProperty("metadata", out var metadata))
        {
            profile.SkinModel = GetString(metadata, "model") ?? "classic";
        }
        else
        {
            profile.SkinModel = "classic";
        }
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpMethod method,
        string url,
        object? payload,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        using var request = new HttpRequestMessage(method, url);

        if (payload is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        }

        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return (response.StatusCode, body);
    }

    private static YggdrasilException CreateException(HttpStatusCode status, string body, string fallback)
    {
        string? error = null;
        string? message = null;

        try
        {
            using var document = JsonDocument.Parse(body);
            error = GetString(document.RootElement, "error");
            message = GetString(document.RootElement, "errorMessage")
                      ?? GetString(document.RootElement, "message");
        }
        catch (JsonException)
        {
            // 非 JSON 错误体。
        }

        var friendly = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "用户名或密码错误，或登录令牌已失效。",
            HttpStatusCode.BadRequest => "请求参数不合法。",
            HttpStatusCode.TooManyRequests => "请求过于频繁，请稍后再试。",
            _ => fallback,
        };

        var detail = message ?? body[..Math.Min(200, body.Length)];
        return new YggdrasilException($"{friendly}（服务端：{detail}）", status, error, message);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
