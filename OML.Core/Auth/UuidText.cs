namespace OML.Core.Auth;

/// <summary>
/// UUID 文本处理的共享实现。
/// <para>Minecraft 生态里 UUID 有两种写法：带连字符的标准 UUID，以及 32 位无连字符形态
/// （Yggdrasil / Xbox 都返回后者）。展示与持久化统一用带连字符的标准形态。</para>
/// </summary>
internal static class UuidText
{
    public static string Format(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return id;
        }

        var hex = id.Replace("-", string.Empty).Trim();
        if (hex.Length != 32)
        {
            return id;
        }

        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
}
