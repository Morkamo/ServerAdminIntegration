using System;
using System.Threading;
using System.Threading.Tasks;
using LabApi.Features;
using LabApi.Loader;
using LabApi.Loader.Features.Plugins;
using ServerAdminIntegration.Components;
using ServerAdminIntegration.Configs;
using ServerAdminIntegration.Handlers;
using Logger = LabApi.Features.Console.Logger;

namespace ServerAdminIntegration;

public sealed class Core : Plugin
{
    public static Core Instance { get; private set; }
    public override string Name { get; } = nameof(ServerAdminIntegration);
    public override string Description { get; } = "Asynchronous API for the server administration Discord bot.";
    public override string Author { get; } = "Morkamo";
    public override Version Version { get; } = new Version(1, 1, 2);
    public override Version RequiredApiVersion { get; } = new Version(LabApiProperties.CompiledVersion);

    public ApiConfig ApiConfig { get; private set; }
    public ApiServer ApiServer { get; private set; }
    public CommandHandler CommandHandler { get; private set; }
    private GameActions _actions;
    private CancellationTokenSource _stop;

    public override void Enable()
    {
        Instance = this;
        ApiConfig = this.LoadConfig<ApiConfig>("Config");
        if (ApiConfig == null || !ApiConfig.IsEnabled)
            return;

        var actions = _actions = new GameActions();
        CommandHandler = new CommandHandler(actions.ExecuteAsync);
        ApiServer = new ApiServer(ApiConfig, CommandHandler, message => Logger.Info(message));
        var server = ApiServer;
        var stop = _stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(stop.Token).ConfigureAwait(false);
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
        _actions = null;
        var stop = Interlocked.Exchange(ref _stop, null);
        if (stop != null)
        {
            _ = Task.Run(() =>
            {
                try { stop.Cancel(); }
                catch (ObjectDisposedException) { }
            });
        }
        ApiServer = null;
        CommandHandler = null;
        ApiConfig = null;
        Instance = null;
    }
}
