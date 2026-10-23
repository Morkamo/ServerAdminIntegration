using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CommandSystem;
using LabApi.Features.Wrappers;
using ServerAdminIntegration.Components;
using ServerAdminIntegration.Shared;

namespace ServerAdminIntegration.Handlers;

public sealed class GameActions
{
    private int _enabled = 1;
    public void Stop() => Interlocked.Exchange(ref _enabled, 0);
    private sealed class Issuer(string name) : ICommandSender
    {
        public string LogName => name;
        public void Respond(string message, bool success = true) { }
    }

    public Task<ApiResult> ExecuteAsync(string command, ApiRequest request, CancellationToken token) =>
        GameThread.RunAsync(action => MainThreadDispatcher.Dispatch(action), () => Execute(command, request), token);

    private static PlayerInfo Describe(Player player) => new PlayerInfo
        { PlayerId = player.PlayerId, Nickname = player.Nickname, UserId = player.UserId };

    private ApiResult Execute(string command, ApiRequest request)
    {
        if (Volatile.Read(ref _enabled) == 0)
            return ApiResult.Fail("Плагин интеграции выключен.");
        var players = Player.ReadyList.Where(x => !x.IsHost && !x.IsNpc).ToArray();
        if (command == "players")
            return new ApiResult { Success = true, Players = players.OrderBy(x => x.PlayerId).Select(Describe).ToArray() };

        var seconds = command == "kick" ? 0 : BanDuration.Parse(request.Duration);
        if (command != "kick" && Misc.RelativeTimeToSeconds(request.Duration, 60) != seconds)
            return ApiResult.Fail("Неверный срок блокировки.");
        var issuer = new Issuer(request.Issuer);
        var started = TimeBehaviour.CurrentTimestamp();
        PlayerInfo target;
        BanHandler.BanType type;
        bool success;
        if (command == "oban")
        {
            var ip = IPAddress.TryParse(request.Target, out var address);
            if (!ip && !System.Text.RegularExpressions.Regex.IsMatch(request.Target, @"\A[a-zA-Z0-9_-]+@[a-zA-Z0-9_-]+\z"))
                return ApiResult.Fail("Укажите AuthID вида 765…@steam или IP-адрес.");
            var id = ip ? address.ToString() : request.Target;
            type = ip ? BanHandler.BanType.IP : BanHandler.BanType.UserId;
            target = new PlayerInfo { Nickname = "Unknown - offline ban", UserId = id };
            success = BanHandler.IssueBan(new BanDetails
            {
                Id = id, OriginalName = target.Nickname, Reason = request.Reason, Issuer = issuer.LogName,
                IssuanceTime = started, Expires = TimeBehaviour.GetBanExpirationTime((uint)seconds),
            }, type);
        }
        else
        {
            int index;
            try { index = PlayerSelection.Find(players.Select(Describe).ToArray(), request.Target); }
            catch (ArgumentException exception) { return ApiResult.Fail(exception.Message); }
            if (index < 0)
                return ApiResult.Fail("Не удалось найти данного игрока.");
            var player = players[index];
            target = Describe(player);
            type = BanHandler.BanType.UserId;
            success = command == "kick"
                ? BanPlayer.KickUser(player.ReferenceHub, issuer, request.Reason)
                : BanPlayer.BanUser(player.ReferenceHub, issuer, request.Reason, seconds);
        }
        if (!success)
            return ApiResult.Fail("Сервер отклонил команду. Действие могло быть отменено другим плагином.");
        var reason = request.Reason;
        if (command != "kick")
        {
            var stored = BanHandler.GetBan(target.UserId, type);
            if (stored == null || stored.IssuanceTime < started)
                return ApiResult.Fail("Не удалось подтвердить запись блокировки. Проверьте бан-лист сервера.");
            seconds = (stored.Expires - stored.IssuanceTime) / TimeSpan.TicksPerSecond;
            reason = stored.Reason;
        }
        ServerLogs.AddLog(ServerLogs.Modules.Administrative,
            issuer.LogName + " executed " + command + " on " + target.UserId + ". Duration: " + seconds + ". Reason: " + reason,
            ServerLogs.ServerLogType.RemoteAdminActivity_GameChanging);
        return new ApiResult { Success = true, Player = target, DurationSeconds = seconds, Reason = reason,
            Message = command == "kick" ? "Игрок исключен." : "Игрок заблокирован." };
    }
}
