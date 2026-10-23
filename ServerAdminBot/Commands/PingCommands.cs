using DSharpPlus.SlashCommands;
using ServerAdminBot.Components;

namespace ServerAdminBot.Commands;

public sealed class PingCommands : ApplicationCommandModule
{
    [SlashCommand("ping", "Проверить доступность Discord API")]
    public Task Ping(InteractionContext context) => Ui.RunAsync(context, async () =>
    {
        await context.Client.GetCurrentApplicationAsync();
        await Ui.ReplyTextAsync(context, "pong");
    });

    [SlashCommand("pingserver", "Проверить ответ API игрового сервера")]
    public Task PingServer(InteractionContext context,
        [Option("server", "Игровой сервер")] [Autocomplete(typeof(ServerAutocomplete))] string? server = null) => Ui.RunAsync(context, async () =>
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            if (Program.Config.Servers.Count != 1)
                throw new UserError("Выберите игровой сервер в параметре server.");
            server = Program.Config.Servers.Keys.Single();
        }
        if (!Program.Config.Servers.TryGetValue(server, out var connection))
            throw new UserError("Неизвестный игровой сервер. Выберите сервер из списка.");
        var result = await Program.ServerApi.ExecuteAsync(connection, "ping", Program.StopToken);
        await Ui.ReplyTextAsync(context, result);
    });
}
