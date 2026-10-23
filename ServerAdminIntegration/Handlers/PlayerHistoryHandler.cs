using System;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Features.Wrappers;
using ServerAdminIntegration.Components;

namespace ServerAdminIntegration.Handlers;

public sealed class PlayerHistoryHandler
{
    private readonly RecentPlayersStore _history;

    public PlayerHistoryHandler(RecentPlayersStore history) => _history = history;

    public void OnJoined(PlayerJoinedEventArgs ev) => Record(ev.Player);

    public void Record(Player player, bool onlyIfMissing = false)
    {
        if (player == null
            || player.IsHost
            || player.Connection == null
            || player.IsNpc
            || !player.IsReady
            || !player.IsPlayer)
            return;

        _history.Record(player.UserId, player.Nickname, player.IpAddress, DateTime.UtcNow.Ticks, onlyIfMissing);
    }
}
