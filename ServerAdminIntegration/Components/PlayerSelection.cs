using System;
using ServerAdminIntegration.Shared;

namespace ServerAdminIntegration.Components;

public static class PlayerSelection
{
    public static int Find(PlayerInfo[] players, string target)
    {
        var auth = target.Contains("@");
        var numeric = !auth && int.TryParse(target, out _);
        var found = -1;

        for (var index = 0; index < players.Length; index++)
        {
            var player = players[index];
            var matches =
                auth ? player.UserId.Equals(target, StringComparison.Ordinal)
                : numeric ? player.PlayerId.ToString() == target
                : player.Nickname.Equals(target, StringComparison.OrdinalIgnoreCase);

            if (!matches)
                continue;

            if (found != -1)
                throw new ArgumentException("Найдено несколько игроков. Укажите Player ID или AuthID.");

            found = index;
        }

        return found;
    }
}
