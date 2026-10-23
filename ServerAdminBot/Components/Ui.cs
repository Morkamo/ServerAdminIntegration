using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;

namespace ServerAdminBot.Components;

public sealed class UserError(string message, string? command = null) : Exception(message)
{
    public string? Command { get; } = command;
}

public static class Ui
{
    public static bool IsAdmin(DiscordMember member) => member.Permissions.HasPermission(Permissions.Administrator);

    public static async Task<bool> CanAsync(DiscordMember member, string permission)
    {
        if (IsAdmin(member))
            return true;

        return (await Program.Permissions.GetAsync(member.Guild.Id, permission, Program.StopToken)).Allows(
            false,
            member.Roles.Select(x => x.Id)
        );
    }

    public static async Task EnsureAsync(DiscordMember member, string permission)
    {
        if (!await CanAsync(member, permission))
            throw new UserError("Недостаточно прав для этого действия.");
    }

    public static (string Name, ServerConfig Connection) Server(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            if (Program.Config.Servers.Count != 1)
                throw new UserError("Выберите игровой сервер в параметре server.");

            name = Program.Config.Servers.Keys.Single();
        }

        if (!Program.Config.Servers.TryGetValue(name, out var connection))
            throw new UserError("Неизвестный игровой сервер. Выберите сервер из списка.");

        return (Program.Config.Servers.Keys.First(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)), connection);
    }

    public static async Task RunAsync(InteractionContext context, Func<Task> action, bool administratorOnly = false)
    {
        var acknowledged = false;
        Console.WriteLine($"/{context.CommandName}: received.");

        try
        {
            await Panel.DeferAsync(context.Interaction, ephemeral: true);
            acknowledged = true;
            Console.WriteLine($"/{context.CommandName}: acknowledged.");

            if (context.Guild?.Id != Program.Config.GuildId || context.Member == null)
                throw new UserError("Команда доступна только на настроенном Discord сервере.");

            var admin = IsAdmin(context.Member);

            if (administratorOnly && !admin)
                throw new UserError("Панель доступна только с правом Discord Administrator.");

            await EnsureAsync(context.Member, context.CommandName);
            await action();
            Console.WriteLine($"/{context.CommandName}: completed.");
        }
        catch (OperationCanceledException) when (Program.StopToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            var text = exception is UserError ? exception.Message : "Не удалось выполнить команду. Попробуйте позже.";
            Console.Error.WriteLine(
                $"/{context.CommandName}: {exception.GetType().Name}"
                    + (exception is UserError ? ": " + exception.Message : "")
            );
            CommandLog.Write(context.CommandName, context.User.Id, Panel.Build([Panel.Text(text)], 0xED4245));

            if (!acknowledged)
                return;

            try
            {
                await Panel.SendAsync(
                    context.Interaction,
                    Panel.Build([Panel.Text(text)], 0xED4245),
                    Program.StopToken
                );
            }
            catch (Exception responseError)
            {
                Console.Error.WriteLine(
                    $"/{context.CommandName}: failed to send error ({responseError.GetType().Name})."
                );
            }
        }
    }

    public static async Task ReplyTextAsync(InteractionContext context, string text)
    {
        CommandLog.Write(context.CommandName, context.User.Id, Panel.Build(SplitText(text).Select(Panel.Text)));

        var first = true;

        foreach (var part in SplitText(text))
        {
            if (first)
                await context.EditResponseAsync(
                    new DiscordWebhookBuilder().WithContent(part).AddMentions(Mentions.None)
                );
            else
                await context.FollowUpAsync(
                    new DiscordFollowupMessageBuilder().WithContent(part).AsEphemeral().AddMentions(Mentions.None)
                );

            first = false;
        }
    }

    public static IEnumerable<string> SplitText(string text)
    {
        for (var offset = 0; offset < text.Length; )
        {
            var length = Math.Min(2000, text.Length - offset);

            if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1]))
                length--;

            yield return text.Substring(offset, length);
            offset += length;
        }
    }

    public static string Truncate(string text, int length)
    {
        if (text.Length <= length)
            return text;

        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
            length--;

        return text[..length];
    }

    public static string Escape(string text) =>
        text.Replace("\\", "\\\\")
            .Replace("*", "\\*")
            .Replace("_", "\\_")
            .Replace("`", "\\`")
            .Replace("~", "\\~")
            .Replace("|", "\\|")
            .Replace("[", "\\[")
            .Replace("]", "\\]")
            .Replace("<", "\\<")
            .Replace(">", "\\>")
            .Replace("@", "@\u200b");
}
