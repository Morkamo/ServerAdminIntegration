using DSharpPlus.Entities;
using ServerAdminIntegration.Shared;

namespace ServerAdminBot.Components;

public static class Moderation
{
    public static ApiRequest Request(DiscordMember actor, string target, string duration, string reason)
    {
        target = target.Trim();
        reason = reason.Trim();
        duration = duration.Trim();
        if (string.IsNullOrEmpty(target) || target.Length > 128)
            throw new UserError("Укажите одного игрока, AuthID или IP-адрес.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000 || reason.Any(char.IsControl))
            throw new UserError("Причина должна содержать от 1 до 1000 символов без переносов строк.");
        if (duration.Length != 0)
        {
            try { BanDuration.Parse(duration); }
            catch (ArgumentException exception) { throw new UserError(exception.Message); }
        }
        var name = new string(actor.DisplayName.Where(x => !char.IsControl(x)).ToArray());
        return new ApiRequest
        {
            RequestId = Guid.NewGuid().ToString("N"), Target = target, Duration = duration, Reason = reason,
            Issuer = Ui.Truncate(name, 60) + " (Discord " + actor.Id + ")",
        };
    }

    public static object Result(string command, string server, ApiResult result)
    {
        var player = result.Player ?? throw new UserError("API не вернул сведения об обработанном игроке.");
        var title = command == "kick" ? "Игрок исключен" : "Игрок заблокирован";
        var details = "### " + title + "\n**Игрок:** " + Ui.Escape(Ui.Truncate(player.Nickname, 100)) +
            "\n**AuthID / IP:** " + Ui.Escape(player.UserId) + "\n**Сервер:** " + Ui.Escape(server);
        if (command != "kick")
            details += "\n**Срок:** " + Duration(result.DurationSeconds);
        details += "\n**Причина:** " + Ui.Escape(result.Reason);
        return Panel.Blocks(Panel.Container([Panel.Text(details)]));
    }

    private static string Duration(long seconds)
    {
        if (seconds > 0 && seconds % 31536000 == 0) return seconds / 31536000 + " г.";
        if (seconds > 0 && seconds % 2592000 == 0) return seconds / 2592000 + " мес.";
        if (seconds > 0 && seconds % 86400 == 0) return seconds / 86400 + " дн.";
        if (seconds > 0 && seconds % 3600 == 0) return seconds / 3600 + " ч.";
        if (seconds > 0 && seconds % 60 == 0) return seconds / 60 + " мин.";
        return seconds + " с";
    }
}
