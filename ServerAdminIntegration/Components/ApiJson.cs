#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ServerAdminIntegration.Shared;
using Utf8Json;

namespace ServerAdminIntegration.Components;

public static class ApiJson
{
    public static ApiRequest ReadRequest(byte[] data)
    {
        var reader = new JsonReader(data);
        var request = new ApiRequest();
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            reader.ReadIsBeginObjectWithVerify();
            var count = 0;
            while (!reader.ReadIsEndObjectWithSkipValueSeparator(ref count))
            {
                var name = reader.ReadPropertyName();
                if (name == null || !names.Add(name))
                    throw new JsonParsingException("Duplicate or invalid property.");
                var value = reader.ReadString() ?? "";
                switch (name)
                {
                    case nameof(ApiRequest.RequestId): request.RequestId = value; break;
                    case nameof(ApiRequest.Target): request.Target = value; break;
                    case nameof(ApiRequest.Duration): request.Duration = value; break;
                    case nameof(ApiRequest.Reason): request.Reason = value; break;
                    case nameof(ApiRequest.Issuer): request.Issuer = value; break;
                    default: throw new JsonParsingException("Unknown property.");
                }
            }
            reader.SkipWhiteSpace();
            if (reader.GetCurrentOffsetUnsafe() != data.Length)
                throw new JsonParsingException("Unexpected data after request.");
            return request;
        }
        catch (Exception exception) when (exception is IndexOutOfRangeException || exception is ArgumentException)
        {
            throw new JsonParsingException("Invalid request JSON.");
        }
    }

    public static string WriteResult(ApiResult result)
    {
        var writer = new JsonWriter(new byte[1024]);
        writer.WriteBeginObject();
        writer.WritePropertyName(nameof(ApiResult.Success)); writer.WriteBoolean(result.Success);
        writer.WriteValueSeparator();
        writer.WritePropertyName(nameof(ApiResult.Message)); writer.WriteString(result.Message);
        writer.WriteValueSeparator();
        writer.WritePropertyName(nameof(ApiResult.Player)); WritePlayer(ref writer, result.Player);
        writer.WriteValueSeparator();
        writer.WritePropertyName(nameof(ApiResult.Players)); writer.WriteBeginArray();
        var players = result.Players ?? Array.Empty<PlayerInfo>();
        for (var index = 0; index < players.Length; index++)
        {
            if (index != 0) writer.WriteValueSeparator();
            WritePlayer(ref writer, players[index]);
        }
        writer.WriteEndArray();
        writer.WriteValueSeparator();
        writer.WritePropertyName(nameof(ApiResult.DurationSeconds)); writer.WriteInt64(result.DurationSeconds);
        writer.WriteValueSeparator();
        writer.WritePropertyName(nameof(ApiResult.Reason)); writer.WriteString(result.Reason);
        writer.WriteEndObject();
        // The game's writer leaves some control characters unescaped. This compact JSON has no formatting whitespace.
        var encoded = new StringBuilder();
        foreach (var character in writer.ToString())
        {
            if (character < ' ') encoded.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            else encoded.Append(character);
        }
        return encoded.ToString();
    }

    private static void WritePlayer(ref JsonWriter writer, PlayerInfo? player)
    {
        if (player == null) { writer.WriteNull(); return; }
        writer.WriteBeginObject();
        writer.WritePropertyName(nameof(PlayerInfo.PlayerId)); writer.WriteInt32(player.PlayerId);
        writer.WriteValueSeparator();
        writer.WritePropertyName(nameof(PlayerInfo.Nickname)); writer.WriteString(player.Nickname);
        writer.WriteValueSeparator();
        writer.WritePropertyName(nameof(PlayerInfo.UserId)); writer.WriteString(player.UserId);
        writer.WriteEndObject();
    }
}
