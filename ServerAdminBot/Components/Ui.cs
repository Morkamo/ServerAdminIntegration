using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;

namespace ServerAdminBot.Components;

public sealed class UserError(string message) : Exception(message);

public static class Ui
{
    public static bool IsAdmin(DiscordMember member) => member.Permissions.HasPermission(Permissions.Administrator);

    public static async Task RunAsync(InteractionContext context, Func<Task> action, bool administratorOnly = false)
    {
        await context.DeferAsync(administratorOnly);
        try
        {
            if (context.Guild?.Id != Program.Config.GuildId || context.Member == null)
                throw new UserError("Команда доступна только на настроенном Discord сервере.");
            var admin = IsAdmin(context.Member);
            if (administratorOnly && !admin)
                throw new UserError("Панель доступна только с правом Discord Administrator.");
            if (!admin && !(await Program.Permissions.GetAsync(context.Guild.Id, context.CommandName, Program.StopToken))
                .Allows(false, context.Member.Roles.Select(x => x.Id)))
                throw new UserError("Недостаточно прав для этой команды.");
            await action();
        }
        catch (OperationCanceledException) when (Program.StopToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            var text = exception is UserError ? exception.Message : "Не удалось выполнить команду. Попробуйте позже.";
            if (exception is not UserError)
                Console.Error.WriteLine($"{context.CommandName}: {exception.GetType().Name}");
            await Panel.SendAsync(context.Interaction, Panel.Build([Panel.Text(text)], 0xED4245), Program.StopToken);
        }
    }

    public static async Task ReplyTextAsync(InteractionContext context, string text)
    {
        var first = true;
        foreach (var part in SplitText(text))
        {
            if (first)
                await context.EditResponseAsync(new DiscordWebhookBuilder().WithContent(part).AddMentions(Mentions.None));
            else
                await context.FollowUpAsync(new DiscordFollowupMessageBuilder().WithContent(part).AddMentions(Mentions.None));
            first = false;
        }
    }

    public static IEnumerable<string> SplitText(string text)
    {
        for (var offset = 0; offset < text.Length;)
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

    public static string Escape(string text) => text.Replace("\\", "\\\\").Replace("*", "\\*")
        .Replace("_", "\\_").Replace("`", "\\`").Replace("~", "\\~").Replace("|", "\\|")
        .Replace("[", "\\[").Replace("]", "\\]").Replace("<", "\\<").Replace(">", "\\>").Replace("@", "@\u200b");
}
