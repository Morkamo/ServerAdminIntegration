using System.Collections.Concurrent;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;

namespace ServerAdminBot.Components;

public static class PermissionsUi
{
    private static readonly string[] Commands = ["ping", "pingserver", "ban", "oban", "players", "players.manage"];
    private static string Title(string command) => command == "players.manage" ? "/players · Управление" : "/" + command;
    private static readonly ConcurrentDictionary<string, Session> Sessions = new();

    private sealed class Session
    {
        public required ulong Owner { get; init; }
        public required ulong Guild { get; init; }
        public required ulong[] Roles { get; init; }
        public string Command { get; set; } = "ping";
        public int Page { get; set; }
        public int Revision { get; set; }
        public DateTime Expires { get; } = DateTime.UtcNow.AddMinutes(15);
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    public static async Task CreateAsync(DiscordInteraction interaction, DiscordMember member)
    {
        if (!Ui.IsAdmin(member))
            throw new UserError("Требуется право Discord Administrator.");
        foreach (var item in Sessions.Where(x => x.Value.Expires <= DateTime.UtcNow))
            Sessions.TryRemove(item.Key, out _);
        if (Sessions.Count >= 100)
            throw new UserError("Открыто слишком много панелей прав. Повторите позже.");
        var key = Guid.NewGuid().ToString("N");
        var session = new Session
        {
            Owner = member.Id,
            Guild = member.Guild.Id,
            Roles = member.Guild.Roles.Values.OrderBy(x => x.Id != member.Guild.Id)
                .ThenByDescending(x => x.Position).ThenBy(x => x.Id).Select(x => x.Id).ToArray(),
        };
        Sessions[key] = session;
        try
        {
            var payload = await BuildAsync(key, session, member.Guild);
            CommandLog.Write("permissionscontrol", member.Id, payload);
            await Panel.SendAsync(interaction, payload, Program.StopToken);
        }
        catch
        {
            Sessions.TryRemove(key, out _);
            throw;
        }
    }

    private static async Task<object> BuildAsync(string key, Session session, DiscordGuild guild)
    {
        var settings = await Program.Permissions.GetAsync(session.Guild, session.Command, Program.StopToken);
        var prefix = $"perm:{key}:{++session.Revision}:";
        var page = session.Roles.Skip(session.Page * 25).Take(25).ToArray();
        var names = settings.Roles.Where(guild.Roles.ContainsKey).Select(x => Ui.Escape(guild.Roles[x].Name));
        var roleText = Ui.Truncate(string.Join(", ", names), 950);
        var components = new List<object>
        {
            Panel.Text("### Настройка прав · " + Title(session.Command)),
            Panel.Text("**Общий доступ:** " + (settings.Everyone ? "Включён · @everyone" : "Выключен") +
                "\n**Роли с доступом:** " + (roleText.Length == 0 ? "Нет" : roleText)),
            Panel.Row(Panel.Select(prefix + "command", "Выберите команду или блок", Commands.Select(x => (Title(x), x, x == session.Command)))),
        };
        if (page.Length > 0)
            components.Add(Panel.Row(Panel.Select(prefix + "roles", "Роли с доступом", page.Select(id => (
                id == guild.Id ? "@everyone · Общий доступ" : guild.Roles.TryGetValue(id, out var role)
                    ? Ui.Truncate(role.Name, 100) : "Удалённая роль " + id,
                id.ToString(), id == guild.Id ? settings.Everyone : settings.Roles.Contains(id))), true)));
        components.Add(Panel.Row(
            Panel.Button(prefix + "previous", "◀ Роли", disabled: session.Page == 0),
            Panel.Button(prefix + "next", "Роли ▶", disabled: (session.Page + 1) * 25 >= session.Roles.Length),
            Panel.Button(prefix + "everyone", "Общий доступ: " + (settings.Everyone ? "✓" : "✗"), settings.Everyone ? 3 : 2)));
        components.Add(Panel.Text($"-# Роли: страница {session.Page + 1}/{Math.Max(1, (session.Roles.Length + 24) / 25)} · панель действует 15 минут"));
        return Panel.Build(components);
    }

    public static async Task HandleAsync(DiscordClient client, ComponentInteractionCreateEventArgs e)
    {
        if (!e.Id.StartsWith("perm:", StringComparison.Ordinal))
            return;
        var parts = e.Id.Split(':');
        if (parts.Length != 4 || !Sessions.TryGetValue(parts[1], out var session) || session.Expires <= DateTime.UtcNow)
        {
            await RejectAsync(e, "Панель истекла. Вызовите /permissionscontrol заново.");
            return;
        }
        if (e.User.Id != session.Owner || e.Guild?.Id != session.Guild)
        {
            await RejectAsync(e, "Это панель другого пользователя.");
            return;
        }
        await Panel.DeferAsync(e.Interaction, update: true);
        await session.Gate.WaitAsync(Program.StopToken);
        try
        {
            if (!int.TryParse(parts[2], out var revision) || revision != session.Revision)
            {
                await e.Interaction.CreateFollowupMessageAsync(new DiscordFollowupMessageBuilder()
                    .WithContent("Панель обновилась. Используйте её текущие элементы управления.").AsEphemeral().AddMentions(Mentions.None));
                return;
            }
            var member = await e.Guild.GetMemberAsync(e.User.Id, updateCache: true);
            if (!Ui.IsAdmin(member))
                throw new UserError("Требуется право Discord Administrator.");
            switch (parts[3])
            {
                case "command":
                    if (e.Values.Length != 1 || !Commands.Contains(e.Values[0]))
                        throw new UserError("Неизвестная команда.");
                    session.Command = e.Values[0];
                    session.Page = 0;
                    break;
                case "roles":
                    var page = session.Roles.Skip(session.Page * 25).Take(25).ToArray();
                    var selected = new List<ulong>();
                    foreach (var value in e.Values)
                    {
                        if (!ulong.TryParse(value, out var id) || !page.Contains(id) || !e.Guild.Roles.ContainsKey(id))
                            throw new UserError("Список ролей изменился. Откройте панель заново.");
                        selected.Add(id);
                    }
                    await Program.Permissions.UpdateRolesAsync(session.Guild, session.Command,
                        page.Where(x => x != session.Guild), selected.Where(x => x != session.Guild),
                        page.Contains(session.Guild) ? selected.Contains(session.Guild) : null, Program.StopToken);
                    break;
                case "everyone":
                    await Program.Permissions.ToggleEveryoneAsync(session.Guild, session.Command, Program.StopToken);
                    break;
                case "previous":
                    session.Page = Math.Max(0, session.Page - 1);
                    break;
                case "next":
                    session.Page = Math.Min(Math.Max(0, (session.Roles.Length - 1) / 25), session.Page + 1);
                    break;
                default:
                    throw new UserError("Неизвестное действие.");
            }
            var payload = await BuildAsync(parts[1], session, e.Guild);
            if (parts[3] is "roles" or "everyone") CommandLog.Write("permissionscontrol", e.User.Id, payload);
            await Panel.SendAsync(e.Interaction, payload, Program.StopToken);
        }
        catch (OperationCanceledException) when (Program.StopToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Permissions: " + exception.GetType().Name);
            Sessions.TryRemove(parts[1], out _);
            var payload = Panel.Build([Panel.Text(exception is UserError
                ? exception.Message : "Не удалось обновить права. Откройте панель заново.")], 0xED4245);
            CommandLog.Write("permissionscontrol", e.User.Id, payload);
            await Panel.SendAsync(e.Interaction, payload, Program.StopToken);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private static Task RejectAsync(ComponentInteractionCreateEventArgs e, string text) => Panel.RejectAsync(e.Interaction, text);
}
