using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OML.Core.Serialization;

/// <summary>
/// 宽容的 <see cref="DateTimeOffset"/> 转换器。
/// <para>
/// 严格 ISO 8601 要求 <c>+00:00</c>，但 Fabric 的 profile JSON 写的是
/// <c>2026-10-23T19:06:59+0000</c>（时区偏移没有冒号）。
/// 默认序列化器会因此抛 <see cref="JsonException"/>，
/// 整个版本 JSON 解析失败——扫描器直接跳过这个版本，表现为"装了但列表里没有"。
/// </para>
/// </summary>
public sealed class FlexibleDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"时间字段应为字符串，实际是 {reader.TokenType}。");
        }

        var text = reader.GetString();

        if (DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
        {
            return parsed;
        }

        throw new JsonException($"无法解析时间：{text}");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("O", CultureInfo.InvariantCulture));
}
