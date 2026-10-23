using System.Collections.Concurrent;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.SlashCommands;
using ServerAdminIntegration.Shared;

namespace ServerAdminBot.Components;

public static class PlayersUi
{
    public const int PageSize = 15;
    private static readonly ConcurrentDictionary<string, ListSession> Lists = new();
    private static readonly ConcurrentDictionary<string, ActionSession> Actions = new();

    private sealed class ListSession
    {
        public required ulong Owner { get; init; }
        public required ulong Guild { get; init; }
        public required ulong Channel { get; init; }
        public required string Server { get; init; }
        public required PlayerInfo[] Players { get; init; }
        public DateTime Expires { get; } = DateTime.UtcNow.AddMinutes(15);
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private sealed class ActionSession
    {
        public required ulong Owner { get; init; }
        public required ulong Guild { get; init; }
        public required ulong Channel { get; init; }
        public required string Server { get; init; }
        public required PlayerInfo Player { get; init; }
        public DateTime Expires { get; } = DateTime.UtcNow.AddMinutes(15);
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string Action { get; set; } = "";
        public string Modal { get; set; } = "";
        public bool Submitted { get; set; }
    }

    public static async Task CreateAsync(InteractionContext context, string server, PlayerInfo[] players)
    {
        Cleanup();
        if (Lists.Count >= 200)
            throw new UserError("Открыто слишком много списков. Повторите позже.");
        var key = Guid.NewGuid().ToString("N");
        var session = new ListSession { Owner = context.User.Id, Guild = context.Guild.Id, Channel = context.Channel.Id, Server = server,
            Players = players.OrderBy(x => x.PlayerId).ToArray() };
        Lists[key] = session;
        try
        {
            await Panel.SendAsync(context.Interaction, BuildList(key, server, session.Players, 0), Program.StopToken);
            var pages = Math.Max(1, (session.Players.Length + PageSize - 1) / PageSize);
            CommandLog.Write("players", context.User.Id, Enumerable.Range(0, pages)
                .Select(page => BuildList(key, server, session.Players, page, readOnly: true)).ToArray());
        }
        catch { Lists.TryRemove(key, out _); throw; }
    }

    public static string PlayerLabel(PlayerInfo player)
    {
        var prefix = "[" + player.PlayerId + "] ";
        var suffix = " (" + Ui.Truncate(player.UserId, 48) + ")";
        var nickname = new string(player.Nickname.Select(x => char.IsControl(x) ? ' ' : x).ToArray());
        return prefix + Ui.Truncate(nickname, Math.Max(0, 80 - prefix.Length - suffix.Length)) + suffix;
    }

    public static object BuildList(string key, string server, PlayerInfo[] players, int page, bool readOnly = false)
    {
        var pages = Math.Max(1, (players.Length + PageSize - 1) / PageSize);
        page = Math.Clamp(page, 0, pages - 1);
        var components = new List<object> { Panel.Text("### Игроки · " + Ui.Escape(server) + "\nВсего: **" + players.Length + "**") };
        if (players.Length == 0)
            components.Add(Panel.Text("На сервере нет игроков."));
        for (var index = page * PageSize; index < Math.Min(players.Length, (page + 1) * PageSize); index++)
            components.Add(readOnly ? Panel.Text(PlayerLabel(players[index])) : Panel.Row(Panel.Button($"players:{key}:select:{index}", PlayerLabel(players[index]))));
        components.Add(Panel.Text($"-# Страница {page + 1}/{pages}" + (readOnly ? "" : " · список действует 15 минут")));
        if (!readOnly) components.Add(Panel.Row(
            Panel.Button($"players:{key}:page:{page - 1}", "◀", disabled: page == 0),
            Panel.Button($"players:{key}:page:{page + 1}", "▶", disabled: page == pages - 1)));
        return Panel.Build(components);
    }

    private static object BuildActions(string key, PlayerInfo player) => Panel.Build([
        Panel.Text("### Выберите действие\n" + Ui.Escape(PlayerLabel(player))),
        Panel.Row(Panel.Button($"playeraction:{key}:ban", "Заблокировать"),
            Panel.Button($"playeraction:{key}:kick", "Исключить"), Panel.Button($"playeraction:{key}:cancel", "Отмена")),
    ]);

    public static async Task HandleAsync(DiscordClient client, ComponentInteractionCreateEventArgs e)
    {
        var isList = e.Id.StartsWith("players:", StringComparison.Ordinal);
        if (!isList && !e.Id.StartsWith("playeraction:", StringComparison.Ordinal))
            return;
        var responded = false;
        var responseAttempted = false;
        var pageUpdate = false;
        try
        {
            var parts = e.Id.Split(':');
            if (isList)
            {
                if (parts.Length != 4 || !Lists.TryGetValue(parts[1], out var list) || list.Expires <= DateTime.UtcNow ||
                    e.User.Id != list.Owner || e.Guild?.Id != list.Guild || e.Channel.Id != list.Channel)
                    throw new UserError("Список устарел. Вызовите /players заново.");
                pageUpdate = parts[2] == "page";
                responseAttempted = true;
                await Panel.DeferAsync(e.Interaction, ephemeral: true, update: pageUpdate);
                responded = true;
                var member = await e.Guild.GetMemberAsync(e.User.Id, updateCache: true);
                await Ui.EnsureAsync(member, pageUpdate ? "players" : "players.manage");
                if (!int.TryParse(parts[3], out var index))
                    throw new UserError("Некорректный выбор игрока или страницы.");
                if (pageUpdate)
                {
                    await list.Gate.WaitAsync(Program.StopToken);
                    try { await Panel.SendAsync(e.Interaction, BuildList(parts[1], list.Server, list.Players, index), Program.StopToken); }
                    finally { list.Gate.Release(); }
                }
                else
                {
                    if (parts[2] != "select" || index < 0 || index >= list.Players.Length)
                        throw new UserError("Некорректный выбор игрока.");
                    Cleanup();
                    if (Actions.Count >= 500)
                        throw new UserError("Открыто слишком много панелей управления. Повторите позже.");
                    var key = Guid.NewGuid().ToString("N");
                    Actions[key] = new ActionSession { Owner = e.User.Id, Guild = list.Guild, Channel = list.Channel,
                        Server = list.Server, Player = list.Players[index] };
                    try { await Panel.SendAsync(e.Interaction, BuildActions(key, list.Players[index]), Program.StopToken); }
                    catch { Actions.TryRemove(key, out _); throw; }
                }
                return;
            }

            if (parts.Length != 3 || !Actions.TryGetValue(parts[1], out var session) || session.Expires <= DateTime.UtcNow ||
                e.User.Id != session.Owner || e.Guild?.Id != session.Guild || e.Channel.Id != session.Channel)
                throw new UserError("Панель устарела или принадлежит другому пользователю.");
            if (!await session.Gate.WaitAsync(0, Program.StopToken))
                throw new UserError("Панель уже обрабатывает действие. Дождитесь результата.");
            try
            {
                if (session.Submitted)
                    throw new UserError("Действие уже отправлено. Откройте новую панель для следующей команды.");
                if (parts[2] == "cancel")
                {
                    responseAttempted = true;
                    await Panel.DeferAsync(e.Interaction, update: true);
                    responded = true;
                    Actions.TryRemove(parts[1], out _);
                    await Panel.SendAsync(e.Interaction, Panel.Build([Panel.Text("Действие отменено.")]), Program.StopToken);
                    return;
                }
                if (parts[2] != "ban" && parts[2] != "kick")
                    throw new UserError("Неизвестное действие.");
                // Modal должен быть первым ответом Discord; актуальные права проверяются при отправке формы.
                session.Action = parts[2];
                session.Modal = Guid.NewGuid().ToString("N");
                responseAttempted = true;
                await Panel.RespondAsync(e.Interaction, InteractionResponseType.Modal,
                    BuildModal(parts[1], session.Modal, session.Action == "ban"));
                responded = true;
            }
            finally { session.Gate.Release(); }
        }
        catch (OperationCanceledException) when (Program.StopToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            var text = Error(exception);
            if (!responded && responseAttempted)
            {
                Console.Error.WriteLine("Players callback: " + text);
                return;
            }
            if (!responded)
                await RejectAsync(e.Interaction, text);
            else if (pageUpdate)
                await e.Interaction.CreateFollowupMessageAsync(new DiscordFollowupMessageBuilder().WithContent(text).AsEphemeral().AddMentions(Mentions.None));
            else
                await Panel.SendAsync(e.Interaction, Panel.Build([Panel.Text(text)], 0xED4245), Program.StopToken);
        }
    }

    public static async Task ModalAsync(DiscordClient client, ModalSubmitEventArgs e)
    {
        var id = e.Interaction.Data.CustomId;
        if (!id.StartsWith("playermodal:", StringComparison.Ordinal))
            return;
        var parts = id.Split(':');
        if (parts.Length != 3 || !Actions.TryGetValue(parts[1], out var session) || session.Expires <= DateTime.UtcNow ||
            e.Interaction.User.Id != session.Owner || e.Interaction.GuildId != session.Guild || e.Interaction.ChannelId != session.Channel)
        {
            await RejectAsync(e.Interaction, "Окно устарело или принадлежит другому пользователю.");
            return;
        }
        await Panel.DeferAsync(e.Interaction, update: true);
        await session.Gate.WaitAsync(Program.StopToken);
        ApiResult? completed = null;
        try
        {
            if (session.Submitted || session.Modal != parts[2] || string.IsNullOrEmpty(session.Modal))
                throw new UserError("Окно уже обработано или устарело.");
            session.Modal = "";
            var member = await e.Interaction.Guild.GetMemberAsync(session.Owner, updateCache: true);
            await Ui.EnsureAsync(member, "players.manage");
            if (!e.Values.TryGetValue("reason", out var reason))
                throw new UserError("Укажите причину.");
            var duration = "";
            if (session.Action == "ban" && !e.Values.TryGetValue("duration", out duration))
                throw new UserError("Укажите срок блокировки.");
            var request = Moderation.Request(member, session.Player.UserId, duration, reason);
            session.Submitted = true;
            var selected = Ui.Server(session.Server);
            var result = completed = await Program.ServerApi.QueryAsync(selected.Connection, session.Action, request, Program.StopToken);
            var payload = Moderation.Result(session.Action, selected.Name, result);
            CommandLog.Write(session.Action, session.Owner, payload);
            await Panel.SendAsync(e.Interaction, payload, Program.StopToken);
        }
        catch (OperationCanceledException) when (Program.StopToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            var payload = completed == null ? Panel.Build([Panel.Text(Error(exception))], 0xED4245) :
                Moderation.Result(session.Action, session.Server, completed);
            if (completed == null) CommandLog.Write(session.Action, session.Owner, payload);
            await Panel.SendAsync(e.Interaction, payload, Program.StopToken);
        }
        finally { session.Gate.Release(); }
    }

    private static string Error(Exception exception)
    {
        if (exception is UserError) return exception.Message;
        Console.Error.WriteLine("Players: " + exception.GetType().Name);
        return "Не удалось обработать действие. Попробуйте позже.";
    }

    private static Task RejectAsync(DiscordInteraction interaction, string text) => Panel.RejectAsync(interaction, text);

    public static object BuildModal(string key, string nonce, bool ban)
    {
        var fields = new List<object>();
        if (ban)
            fields.Add(Panel.Row(new { type = 4, custom_id = "duration", style = 1, label = "Срок: 30m, 1h, 1d, 1M, 1y (больше нуля)", required = true, max_length = 16 }));
        fields.Add(Panel.Row(new { type = 4, custom_id = "reason", style = 1, label = "Причина", required = true, max_length = 1000 }));
        return new { custom_id = $"playermodal:{key}:{nonce}", title = ban ? "Заблокировать игрока" : "Исключить игрока", components = fields };
    }

    private static void Cleanup()
    {
        foreach (var item in Lists.Where(x => x.Value.Expires <= DateTime.UtcNow)) Lists.TryRemove(item.Key, out _);
        foreach (var item in Actions.Where(x => x.Value.Expires <= DateTime.UtcNow)) Actions.TryRemove(item.Key, out _);
    }
}
