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
        var result = await Program.ServerApi.ExecuteAsync(Ui.Server(server).Connection, "ping", Program.StopToken);
        await Ui.ReplyTextAsync(context, result);
    });
}
