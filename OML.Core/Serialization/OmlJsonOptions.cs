using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace OML.Core.Serialization;

/// <summary>
/// 全局 JSON 配置。所有反序列化都必须走这里，否则迟早踩大小写、枚举、数字字符串的坑。
/// <para>
/// 为什么放在 Core 而不是 Abstractions：模型是纯 DTO，不该绑定序列化策略；
/// 策略属于"基础设施"，由 Core 统一持有。
/// </para>
/// </summary>
public static class OmlJsonOptions
{
    /// <summary>
    /// 只读配置实例。已调用 <see cref="JsonSerializerOptions.MakeReadOnly(bool)"/>，
    /// 运行期被篡改会直接抛异常，避免"某处改了全局配置导致另一处解析失败"的幽灵 bug。
    /// </summary>
    public static JsonSerializerOptions Default { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            // Mojang 的 JSON 大小写并不总是稳定；Forge/Fabric 的变体更容易漂移。
            PropertyNameCaseInsensitive = true,

            // 手工编辑过的版本 JSON 常常带注释或尾逗号，容忍它们能省掉大量"打不开"的工单。
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,

            // 个别第三方 JSON 把数字写成字符串（"size": "1234"）。
            NumberHandling = JsonNumberHandling.AllowReadingFromString,

            // MakeReadOnly() 在 .NET 8 要求显式 resolver，否则抛 InvalidOperationException。
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };

        // 枚举一律按字符串处理，避免以后加枚举时出现"存 0/1 还是存名字"的分裂。
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true));

        // Fabric 的时间戳用 "+0000"（无冒号），严格解析会整份 JSON 失败。
        options.Converters.Add(new FlexibleDateTimeOffsetConverter());

        options.MakeReadOnly();
        return options;
    }
}
