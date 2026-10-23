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
    private readonly RecentPlayersStore _history;

    public GameActions(RecentPlayersStore history) => _history = history;

    public void Stop() => Interlocked.Exchange(ref _enabled, 0);

    private sealed class Issuer(string name) : ICommandSender
    {
        public string LogName => name;

        public void Respond(string message, bool success = true) { }
    }

    public async Task<ApiResult> ExecuteAsync(string command, ApiRequest request, CancellationToken token)
    {
        if (command == "recentlyplayers" || command == "recentban")
            await _history.WaitReadyAsync(token).ConfigureAwait(false);

        token.ThrowIfCancellationRequested();

        if (command == "recentlyplayers")
            return new ApiResult { Success = true, Players = _history.Latest() };

        return await GameThread
            .RunAsync(action => MainThreadDispatcher.Dispatch(action), () => Execute(command, request), token)
            .ConfigureAwait(false);
    }

    private static PlayerInfo Describe(Player player) =>
        new PlayerInfo
        {
            PlayerId = player.PlayerId,
            Nickname = player.Nickname,
            UserId = player.UserId,
        };

    private ApiResult Execute(string command, ApiRequest request)
    {
        if (Volatile.Read(ref _enabled) == 0)
            return ApiResult.Fail("Плагин интеграции выключен.");

        var players = Player.ReadyList.Where(x => !x.IsHost && !x.IsNpc).ToArray();

        if (command == "players")
            return new ApiResult
            {
                Success = true,
                Players = players.OrderBy(x => x.PlayerId).Select(Describe).ToArray(),
            };

        var fromHistory = command == "recentban";
        var combined = fromHistory && request.Mode == "ip";
        var entry = fromHistory ? _history.Find(request.Target) : null;

        if (fromHistory)
        {
            if (entry == null)
                return ApiResult.Fail("Игрок отсутствует в истории входов.");

            command = players.Any(player => player.UserId == entry.UserId) ? "ban" : "oban";
        }

        ApiResult Fail(string message) => new ApiResult { Message = message, Command = command };

        var seconds = command == "kick" ? 0 : BanDuration.Parse(request.Duration);

        if (command != "kick" && Misc.RelativeTimeToSeconds(request.Duration, 60) != seconds)
            return Fail("Неверный срок блокировки.");

        var issuer = new Issuer(request.Issuer);
        var started = TimeBehaviour.CurrentTimestamp();
        PlayerInfo target;
        BanHandler.BanType type;
        bool success;

        if (command == "oban")
        {
            var ip = !fromHistory && IPAddress.TryParse(request.Target, out _);

            if (!ip
                && !System.Text.RegularExpressions.Regex.IsMatch(request.Target, @"\A[a-zA-Z0-9_-]+@[a-zA-Z0-9_-]+\z"))
                return Fail("Укажите AuthID вида 765…@steam или IP-адрес.");

            var id = ip ? IPAddress.Parse(request.Target).ToString() : request.Target;
            type = ip ? BanHandler.BanType.IP : BanHandler.BanType.UserId;
            target = entry ?? new PlayerInfo { Nickname = "Unknown - offline ban", UserId = id };

            if (combined && !IPAddress.TryParse(target.IpAddress, out _))
                return Fail("В истории нет корректного IP этого игрока. Используйте блокировку по ID.");

            success = BanHandler.IssueBan(
                new BanDetails
                {
                    Id = id,
                    OriginalName = target.Nickname,
                    Reason = request.Reason,
                    Issuer = issuer.LogName,
                    IssuanceTime = started,
                    Expires = TimeBehaviour.GetBanExpirationTime((uint)seconds),
                },
                type
            );
        }
        else
        {
            int index;

            try
            {
                index = PlayerSelection.Find(players.Select(Describe).ToArray(), request.Target);
            }
            catch (ArgumentException exception)
            {
                return Fail(exception.Message);
            }

            if (index < 0)
                return Fail("Не удалось найти данного игрока.");

            var player = players[index];
            target = Describe(player);
            target.IpAddress = player.IpAddress;

            if (combined && !IPAddress.TryParse(target.IpAddress, out _))
                return Fail("Не удалось определить IP игрока; блокировка не выполнена.");

            type = BanHandler.BanType.UserId;
            success =
                command == "kick"
                    ? BanPlayer.KickUser(player.ReferenceHub, issuer, request.Reason)
                    : BanPlayer.BanUser(player.ReferenceHub, issuer, request.Reason, seconds);
        }

        if (!success)
            return Fail("Сервер отклонил команду. Действие могло быть отменено другим плагином.");

        var reason = request.Reason;
        var bannedIp = false;

        if (command != "kick")
        {
            var stored = BanHandler.GetBan(target.UserId, type);

            if (stored == null || stored.IssuanceTime < started)
                return Fail("Не удалось подтвердить запись блокировки. Проверьте бан-лист сервера.");

            seconds = (stored.Expires - stored.IssuanceTime) / TimeSpan.TicksPerSecond;
            reason = stored.Reason;

            var ipBan = string.IsNullOrEmpty(target.IpAddress)
                ? null
                : BanHandler.GetBan(target.IpAddress, BanHandler.BanType.IP);
            bannedIp =
                ipBan != null
                && ipBan.IssuanceTime >= started
                && (ipBan.Expires - ipBan.IssuanceTime) / TimeSpan.TicksPerSecond == seconds;

            if (combined && !bannedIp)
            {
                success = BanHandler.IssueBan(
                    new BanDetails
                    {
                        Id = target.IpAddress,
                        OriginalName = target.Nickname,
                        Reason = reason,
                        Issuer = issuer.LogName,
                        IssuanceTime = stored.IssuanceTime,
                        Expires = stored.Expires,
                    },
                    BanHandler.BanType.IP
                );
                ipBan = BanHandler.GetBan(target.IpAddress, BanHandler.BanType.IP);
                bannedIp = success && ipBan != null && ipBan.IssuanceTime >= started && ipBan.Expires == stored.Expires;

                if (!bannedIp)
                    return Fail(
                        "SteamID заблокирован, но запись бана IP не подтверждена. Проверьте оба бан-листа перед повторением."
                    );
            }
        }

        ServerLogs.AddLog(
            ServerLogs.Modules.Administrative,
            issuer.LogName + " executed " + command + " on " + target.UserId
                + ". Duration: " + seconds + ". Reason: " + reason,
            ServerLogs.ServerLogType.RemoteAdminActivity_GameChanging
        );

        return new ApiResult
        {
            Success = true,
            Player = target,
            DurationSeconds = seconds,
            Reason = reason,
            Message = command == "kick" ? "Игрок исключен." : "Игрок заблокирован.",
            Command = command,
            BannedIp = bannedIp,
        };
    }
}
