using DSharpPlus.SlashCommands;
using ServerAdminBot.Components;

namespace ServerAdminBot.Commands;

public sealed class PlayersCommand : ApplicationCommandModule
{
    [SlashCommand("recentlyplayers", "Вывести список последних 75 игроков.")]
    public Task RecentlyPlayers(
        InteractionContext context,
        [Option("server", "Игровой сервер")] [Autocomplete(typeof(ServerAutocomplete))] string? server = null
    ) =>
        Ui.RunAsync(
            context,
            async () =>
            {
                var selected = Ui.Server(server);
                var result = await Program.ServerApi.QueryAsync(
                    selected.Connection,
                    "recentlyplayers",
                    stop: Program.StopToken
                );
                await PlayersUi.CreateAsync(context, selected.Name, result.Players, history: true);
            }
        );

    [SlashCommand("players", "Показать текущий список игроков сервера")]
    public Task Players(
        InteractionContext context,
        [Option("server", "Игровой сервер")] [Autocomplete(typeof(ServerAutocomplete))] string? server = null
    ) =>
        Ui.RunAsync(
            context,
            async () =>
            {
                var selected = Ui.Server(server);
                var result = await Program.ServerApi.QueryAsync(
                    selected.Connection,
                    "players",
                    stop: Program.StopToken
                );
                Console.WriteLine($"/players: game API returned {result.Players.Length} players.");
                await PlayersUi.CreateAsync(context, selected.Name, result.Players);
                Console.WriteLine("/players: private list displayed.");
            }
        );
}
