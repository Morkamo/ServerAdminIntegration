using System.Runtime.InteropServices;
using DSharpPlus;
using DSharpPlus.SlashCommands;
using ServerAdminBot.Commands;
using ServerAdminBot.Components;
using ServerAdminBot.Database;

namespace ServerAdminBot;

public static class Program
{
    public static Config Config { get; private set; } = null!;
    public static CommandPermissions Permissions { get; private set; } = null!;
    public static ServerApi ServerApi { get; private set; } = null!;
    public static CancellationToken StopToken { get; private set; }

    public static async Task<int> Main(string[] args)
    {
        using var stop = new CancellationTokenSource();
        StopToken = stop.Token;
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(
            PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
        try
        {
            var initializeOnly = args.Contains("--init-db");
            var paths = args.Where(x => x != "--init-db").ToArray();
            if (paths.Length > 1)
                throw new InvalidOperationException("Использование: ServerAdminBot [config.json] [--init-db]");
            var path = Path.GetFullPath(paths.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "config.json"));
            Config = await Config.LoadAsync(path, !initializeOnly, stop.Token);
            await using var source = Config.Database.CreateDataSource();
            Permissions = new CommandPermissions(source);
            await Permissions.InitializeAsync(stop.Token);
            if (initializeOnly)
            {
                Console.WriteLine("Таблицы ServerAdminBot готовы.");
                return 0;
            }
            using var api = new ServerApi();
            ServerApi = api;
            using var client = new DiscordClient(new DiscordConfiguration
            {
                Token = Config.Token,
                TokenType = TokenType.Bot,
                Intents = DiscordIntents.Guilds,
                MinimumLogLevel = Microsoft.Extensions.Logging.LogLevel.Information,
            });
            var slash = client.UseSlashCommands();
            slash.RegisterCommands<PingCommands>(Config.GuildId);
            slash.RegisterCommands<PermissionsControlCommand>(Config.GuildId);
            client.ComponentInteractionCreated += PermissionsUi.HandleAsync;
            slash.SlashCommandErrored += async (_, e) =>
            {
                Console.Error.WriteLine("Slash command failed: " + e.Exception.GetType().Name);
                try
                {
                    await e.Context.FollowUpAsync(new DSharpPlus.Entities.DiscordFollowupMessageBuilder()
                        .WithContent("Не удалось обработать команду. Попробуйте позже.").AsEphemeral()
                        .AddMentions(DSharpPlus.Entities.Mentions.None));
                }
                catch (Exception exception) { Console.Error.WriteLine("Discord response failed: " + exception.GetType().Name); }
            };
            await client.ConnectAsync();
            try
            {
                Console.WriteLine("ServerAdminBot запущен.");
                await Task.Delay(Timeout.Infinite, stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally
            {
                await client.DisconnectAsync();
            }
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"ServerAdminBot: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }
}
