using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LabApi.Features;
using LabApi.Loader;
using LabApi.Loader.Features.Plugins;
using ServerAdminIntegration.Components;
using ServerAdminIntegration.Configs;
using ServerAdminIntegration.Handlers;
using Logger = LabApi.Features.Console.Logger;
using PlayerEvents = LabApi.Events.Handlers.PlayerEvents;

namespace ServerAdminIntegration;

public sealed class Core : Plugin
{
    public static Core Instance { get; private set; }
    public override string Name { get; } = nameof(ServerAdminIntegration);
    public override string Description { get; } = "Asynchronous API for the server administration Discord bot.";
    public override string Author { get; } = "Morkamo";
    public override Version Version { get; } = new Version(1, 2, 0);
    public override Version RequiredApiVersion { get; } = new Version(LabApiProperties.CompiledVersion);

    public ApiConfig ApiConfig { get; private set; }
    public ApiServer ApiServer { get; private set; }
    public CommandHandler CommandHandler { get; private set; }
    private GameActions _actions;
    private CancellationTokenSource _stop;
    public RecentPlayersStore RecentPlayers { get; private set; }
    public PlayerHistoryHandler PlayerHistoryHandler { get; private set; }

    public override void Enable()
    {
        Instance = this;
        ApiConfig = this.LoadConfig<ApiConfig>("Config");

        if (ApiConfig == null || !ApiConfig.IsEnabled)
            return;

        var history = RecentPlayers = new RecentPlayersStore(
            Path.Combine(this.GetConfigDirectory().FullName, "recent-players.json"),
            message => Logger.Error(message)
        );
        PlayerHistoryHandler = new PlayerHistoryHandler(history);
        PlayerEvents.Joined += PlayerHistoryHandler.OnJoined;

        foreach (var player in LabApi.Features.Wrappers.Player.ReadyList)
            PlayerHistoryHandler.Record(player, onlyIfMissing: true);

        var actions = _actions = new GameActions(history);
        CommandHandler = new CommandHandler(actions.ExecuteAsync);
        ApiServer = new ApiServer(ApiConfig, CommandHandler, message => Logger.Info(message));

        var server = ApiServer;
        var stop = _stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                var historyTask = Task.Run(async () =>
                {
                    try
                    {
                        await history.RunAsync(stop.Token).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        Logger.Error("Player history: " + exception.GetType().Name + ": " + exception.Message);
                    }
                });

                try
                {
                    await server.RunAsync(stop.Token).ConfigureAwait(false);
                }
                finally
                {
                    stop.Cancel();
                    await historyTask.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception exception)
            {
                Logger.Error("ServerAdminIntegration API: " + exception.GetType().Name + ": " + exception.Message);
            }
            finally
            {
                stop.Dispose();
            }
        });
    }

    public override void Disable()
    {
        _actions?.Stop();

        if (PlayerHistoryHandler != null)
            PlayerEvents.Joined -= PlayerHistoryHandler.OnJoined;

        RecentPlayers?.Stop();
        PlayerHistoryHandler = null;
        RecentPlayers = null;
        _actions = null;

        var stop = Interlocked.Exchange(ref _stop, null);

        if (stop != null)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    stop.Cancel();
                }
                catch (ObjectDisposedException) { }
            });
        }

        ApiServer = null;
        CommandHandler = null;
        ApiConfig = null;
        Instance = null;
    }
}
