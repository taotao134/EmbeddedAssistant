using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeviceDebugStudio.Infrastructure.Persistence;

/// <summary>
/// @brief 设备配置 JSON 序列化与反序列化辅助类
/// </summary>
internal static class DeviceProfileJsonSerializer
{
    /// <summary>
    /// @brief 创建设备配置专用的 JSON 序列化选项
    /// @return 已注册枚举与容错可空类型转换器的序列化选项
    /// </summary>
    internal static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter(),
            new LenientNullableGuidConverter(),
            new LenientNullableDateTimeOffsetConverter()
        }
    };

    /// <summary>
    /// 可空 GUID 容错转换器，将空字符串按 null 处理，兼容外部或 AI 生成配置
    /// </summary>
    private sealed class LenientNullableGuidConverter : JsonConverter<Guid?>
    {
        /// <inheritdoc />
        public override Guid? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                string? text = reader.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                if (Guid.TryParse(text, out Guid parsed))
                {
                    return parsed;
                }

                throw new JsonException($"无法将“{text}”解析为有效的 GUID。");
            }

            throw new JsonException($"无法从令牌 {reader.TokenType} 反序列化可空 GUID。");
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, Guid? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
            {
                writer.WriteStringValue(value.Value);
                return;
            }

            writer.WriteNullValue();
        }
    }

    /// <summary>
    /// 可空时间戳容错转换器，将空字符串按 null 处理
    /// </summary>
    private sealed class LenientNullableDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
    {
        /// <inheritdoc />
        public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                string? text = reader.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
                {
                    return parsed;
                }

                throw new JsonException($"无法将“{text}”解析为有效的时间戳。");
            }

            throw new JsonException($"无法从令牌 {reader.TokenType} 反序列化可空时间戳。");
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
            {
                writer.WriteStringValue(value.Value);
                return;
            }

            writer.WriteNullValue();
        }
    }
}
