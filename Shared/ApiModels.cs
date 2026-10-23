#nullable enable
using System;
using System.Globalization;

namespace ServerAdminIntegration.Shared;

public sealed class ApiRequest
{
    public string RequestId { get; set; } = "";
    public string Target { get; set; } = "";
    public string Duration { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Issuer { get; set; } = "";
}

public sealed class PlayerInfo
{
    public int PlayerId { get; set; }
    public string Nickname { get; set; } = "";
    public string UserId { get; set; } = "";
}

public sealed class ApiResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public PlayerInfo? Player { get; set; }
    public PlayerInfo[] Players { get; set; } = Array.Empty<PlayerInfo>();
    public long DurationSeconds { get; set; }
    public string Reason { get; set; } = "";
    public static ApiResult Fail(string message) => new ApiResult { Message = message };
}

public static class BanDuration
{
    public static long Parse(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 16)
            throw new ArgumentException("Укажите положительный срок: 30m, 1h, 1d, 1M или 1y.");
        var suffix = value[value.Length - 1];
        var number = char.IsDigit(suffix) ? value : value.Substring(0, value.Length - 1);
        long factor;
        switch (suffix)
        {
            case 's': case 'S': factor = 1; break;
            case 'h': case 'H': factor = 3600; break;
            case 'd': case 'D': factor = 86400; break;
            case 'M': factor = 2592000; break;
            case 'y': case 'Y': factor = 31536000; break;
            default:
                if (suffix != 'm' && !char.IsDigit(suffix))
                    throw new ArgumentException("Неверный срок. Примеры: 30m, 1h, 1d, 1M, 1y. m — минуты, M — месяц.");
                factor = 60;
                break;
        }
        if (!uint.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var amount) || amount == 0)
            throw new ArgumentException("Срок блокировки должен быть больше нуля.");
        var seconds = amount * factor;
        if (seconds > uint.MaxValue)
            throw new ArgumentException("Срок блокировки превышает допустимый игровой API диапазон.");
        return seconds;
    }
}
