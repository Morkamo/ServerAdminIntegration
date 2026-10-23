using DSharpPlus.SlashCommands;
using ServerAdminBot.Components;

namespace ServerAdminBot.Commands;

public sealed class ModerationCommands : ApplicationCommandModule
{
    [SlashCommand("ban", "Заблокировать одного подключённого игрока")]
    public Task Ban(InteractionContext context,
        [Option("player", "Player ID, AuthID или точный ник игрока")] string player,
        [Option("duration", "Срок > 0: 30m, 1h, 1d, 1M, 1y; число — минуты")] string duration,
        [Option("reason", "Причина блокировки")] string reason,
        [Option("server", "Игровой сервер")] [Autocomplete(typeof(ServerAutocomplete))] string? server = null) =>
        ExecuteAsync(context, "ban", player, duration, reason, server);

    [SlashCommand("oban", "Заблокировать AuthID или IP без присутствия игрока")]
    public Task OfflineBan(InteractionContext context,
        [Option("target", "AuthID вида 765…@steam либо IP-адрес")] string target,
        [Option("duration", "Срок > 0: 30m, 1h, 1d, 1M, 1y; число — минуты")] string duration,
        [Option("reason", "Причина блокировки")] string reason,
        [Option("server", "Игровой сервер")] [Autocomplete(typeof(ServerAutocomplete))] string? server = null) =>
        ExecuteAsync(context, "oban", target, duration, reason, server);

    private static Task ExecuteAsync(InteractionContext context, string command, string target, string duration, string reason, string? server) =>
        Ui.RunAsync(context, async () =>
        {
            var selected = Ui.Server(server);
            var request = Moderation.Request(context.Member, target, duration, reason);
            var result = await Program.ServerApi.QueryAsync(selected.Connection, command, request, Program.StopToken);
            CommandLog.Write(command, context.User.Id, Moderation.Result(command, selected.Name, result));
            try { await Panel.SendAsync(context.Interaction, Moderation.Result(command, selected.Name, result), Program.StopToken); }
            catch (UserError) { throw new UserError("Игрок заблокирован. Не удалось отправить подтверждение в Discord; повторять блокировку не нужно."); }
        });
}
