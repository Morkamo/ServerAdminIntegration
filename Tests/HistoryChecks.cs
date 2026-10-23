using System.Collections.Concurrent;
using System.Text.Json;
using ServerAdminBot;
using ServerAdminBot.Components;
using ServerAdminIntegration.Components;
using ServerAdminIntegration.Configs;
using ServerAdminIntegration.Handlers;
using ServerAdminIntegration.Shared;

namespace IntegrationTests;

public static partial class Program
{
    private static async Task UntilAsync(Func<bool> ready)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        while (!ready())
            await Task.Delay(25, limit.Token);
    }

    private static async Task CheckHistoryAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "server-admin-history-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, "recent-players.json");
        var logs = new ConcurrentQueue<string>();
        var history = new RecentPlayersStore(path, logs.Enqueue);
        using var stop = new CancellationTokenSource();
        var worker = Task.Run(() => history.RunAsync(stop.Token));
        var time = DateTime.UtcNow.Ticks;
        static string Id(int index) => (76561198000000000UL + (ulong)index).ToString() + "@steam";

        try
        {
            await history.WaitReadyAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
            await Task.WhenAll(
                Enumerable
                    .Range(0, 40)
                    .Select(index =>
                        Task.Run(() => history.Record(Id(index), "Игрок " + index, "203.0.113.40", time + index))
                    )
            );
            await UntilAsync(() => history.Latest().Length == 40);
            Check(history.Latest()[0].UserId == Id(39), "40 concurrent joins are ordered by newest unique player");
            Check(
                history.Latest().Select(player => player.UserId).Distinct().Count() == 40,
                "join burst produces no duplicate Steam IDs"
            );

            foreach (var invalid in new[]
                {
                    "ID_Dummy",
                    "123@steam",
                    "12345678901234567@discord",
                    "00000000000000000@steam",
                    "",
                })
                history.Record(invalid, "Bot", "127.0.0.1", time);

            history.Record(Id(0), "**Новый**\n@everyone\u202e", "2001:db8::1", time + 1000);
            history.Record(Id(0), "Old name", "192.0.2.1", time - 1);
            await UntilAsync(() => history.Latest()[0].UserId == Id(0));
            Check(
                history.Find(Id(0))?.Nickname == "**Новый**@everyone"
                    && history.Find(Id(0))?.IpAddress == "2001:db8::1",
                "latest join updates sanitized nickname and last IP without stale overwrite"
            );
            Check(
                history.Latest().Length == 40 && history.Latest().All(player => player.IpAddress == ""),
                "bots and invalid IDs excluded, IP hidden from history response"
            );

            var copy = history.Find(Id(0))!;
            copy.IpAddress = "127.0.0.1";
            Check(history.Find(Id(0))!.IpAddress == "2001:db8::1", "ban lookup does not expose mutable stored entry");

            for (var index = 40; index < 100; index++)
                history.Record(Id(index), "Игрок " + index, "203.0.113.40", time + index);

            await UntilAsync(() => history.Find(Id(99)) != null);
            Check(
                history.Latest().Length == 75 && history.Latest()[0].UserId == Id(0),
                "API returns newest 75 while retaining older players"
            );
            await CheckHistoryApiAsync(history, Id(0));
        }
        finally
        {
            history.Stop();
            await stop.CancelAsync();
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
        }

        try
        {
            var saved = ApiJson.ReadHistory(await File.ReadAllBytesAsync(path));
            Check(
                saved.Length == 100 && saved.Select(player => player.UserId).Distinct().Count() == 100,
                "persistent history retains every unique player beyond display limit"
            );
            Check(!File.Exists(path + ".tmp") && logs.IsEmpty, "successful history saves remove temporary files");

            using var reloadStop = new CancellationTokenSource();
            var reloaded = new RecentPlayersStore(path, logs.Enqueue);
            reloaded.Record(Id(0), "After restart", "192.0.2.3", time + 2000);
            reloaded.Record(Id(99), "Reload seed", "127.0.0.1", time + 3000, onlyIfMissing: true);

            var reload = Task.Run(() => reloaded.RunAsync(reloadStop.Token));

            try
            {
                await reloaded.WaitReadyAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
                Check(
                    reloaded.Find(Id(99)) != null && reloaded.Find(Id(0))?.Nickname == "After restart",
                    "startup merges queued joins with loaded history"
                );
                Check(
                    reloaded.Find(Id(99))?.LastSeenUtcTicks == time + 99,
                    "plugin reload does not invent a new join time for known online players"
                );
                Check(
                    reloaded
                        .Latest()
                        .Zip(reloaded.Latest().Skip(1))
                        .All(pair => pair.First.LastSeenUtcTicks >= pair.Second.LastSeenUtcTicks),
                    "history remains sorted after restart"
                );

                var roster = reloaded.Latest();
                static IEnumerable<JsonElement> All(JsonElement root)
                {
                    if (root.TryGetProperty("type", out _))
                        yield return root;

                    if (root.TryGetProperty("components", out var children))
                        foreach (var child in children.EnumerateArray())
                        foreach (var nested in All(child))
                            yield return nested;
                }
                var lastPage = All(
                        JsonSerializer.SerializeToElement(
                            PlayersUi.BuildList("key", "Classic", roster, 100, history: true)
                        )
                    )
                    .ToArray();
                var buttons = lastPage
                    .Where(node =>
                        node.GetProperty("type").GetInt32() == 2
                        && node.GetProperty("custom_id").GetString()!.Contains(":select:")
                    )
                    .ToArray();
                Check(
                    lastPage.Length <= 40
                        && buttons.Length == 15
                        && buttons[0].GetProperty("label").GetString()!.StartsWith("61) ")
                        && buttons[^1].GetProperty("label").GetString()!.StartsWith("75) "),
                    "fifth page holds 15 players with continuous numbering and no Player ID"
                );

                var actions = All(JsonSerializer.SerializeToElement(PlayersUi.BuildActions("key", roster[0], true)))
                    .Where(node => node.GetProperty("type").GetInt32() == 2)
                    .Select(node => node.GetProperty("label").GetString())
                    .ToArray();
                Check(
                    actions.SequenceEqual(new[] { "Заблокировать по ID", "Заблокировать по IP", "Отмена" }),
                    "history panel only offers ID and IP bans"
                );
                Check(
                    PlayersUi
                        .PlayerLabel(
                            new PlayerInfo
                            {
                                PlayerId = 999,
                                Nickname = "Name",
                                UserId = Id(0),
                            },
                            1
                        )
                        .StartsWith("1) Name"),
                    "history numbering ignores live player slot"
                );
                Check(
                    ServerAdminBot.Database.CommandPermissions.IsOpen("recentlyplayers")
                        && !ServerAdminBot.Database.CommandPermissions.IsOpen("players.manage"),
                    "history viewing is public by permission, management stays restricted"
                );
            }
            finally
            {
                reloaded.Stop();
                await reloadStop.CancelAsync();
                await reload.WaitAsync(TimeSpan.FromSeconds(5));
            }

            var original = await File.ReadAllBytesAsync(path);
            await File.WriteAllBytesAsync(path + ".tmp", original);
            File.Delete(path);

            using (var recoverStop = new CancellationTokenSource())
            {
                var recovered = new RecentPlayersStore(path, logs.Enqueue);
                var run = Task.Run(() => recovered.RunAsync(recoverStop.Token));

                try
                {
                    await recovered.WaitReadyAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
                    Check(
                        recovered.Find(Id(99)) != null && File.Exists(path) && !File.Exists(path + ".tmp"),
                        "first-save temporary snapshot can be recovered after interruption"
                    );
                }
                finally
                {
                    recovered.Stop();
                    await recoverStop.CancelAsync();
                    await run.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }

            await File.WriteAllTextAsync(path, "not-json");

            var broken = new RecentPlayersStore(path, logs.Enqueue);
            var failure = Task.Run(() => broken.RunAsync(default));
            await ExpectAsync<Exception>(
                () => broken.WaitReadyAsync(default),
                "corrupt history is unavailable rather than silently reset"
            );
            await ExpectAsync<Exception>(() => failure, "history loading failure is observed by background runner");
            Check(await File.ReadAllTextAsync(path) == "not-json", "corrupt original file is preserved for recovery");

            var retryPath = Path.Combine(directory, "retry", "recent-players.json");
            Directory.CreateDirectory(retryPath + ".tmp");

            using var retryStop = new CancellationTokenSource();
            var retrying = new RecentPlayersStore(retryPath, logs.Enqueue);
            var retry = Task.Run(() => retrying.RunAsync(retryStop.Token));

            try
            {
                await retrying.WaitReadyAsync(default);
                retrying.Record(Id(0), "Retry", "203.0.113.1", time);
                await UntilAsync(() => !logs.IsEmpty);
                Check(retrying.Latest().Length == 1, "disk write failure does not block accepting or querying joins");
                Directory.Delete(retryPath + ".tmp");
                await UntilAsync(() => File.Exists(retryPath));
                Check(
                    ApiJson.ReadHistory(await File.ReadAllBytesAsync(retryPath)).Length == 1,
                    "background writer retries after a transient disk failure"
                );
            }
            finally
            {
                retrying.Stop();
                await retryStop.CancelAsync();
                await retry.WaitAsync(TimeSpan.FromSeconds(5));
            }

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            var neverStarted = new RecentPlayersStore(Path.Combine(directory, "cancelled.json"), _ => { });
            await ExpectAsync<OperationCanceledException>(
                () => neverStarted.WaitReadyAsync(cancelled.Token),
                "history readiness obeys command cancellation"
            );
        }
        finally
        {
            Directory.Delete(directory, true);
        }

        Console.WriteLine(
            "History: burst joins, unique records, atomic persistence, restart, recovery, privacy and UI passed."
        );
    }

    private static async Task CheckHistoryApiAsync(RecentPlayersStore history, string id)
    {
        var port = FreePort();
        var calls = 0;
        var online = false;
        var partial = false;
        var handler = new CommandHandler(
            async (command, request, token) =>
            {
                await history.WaitReadyAsync(token);

                if (command == "recentlyplayers")
                    return new ApiResult { Success = true, Players = history.Latest() };

                Interlocked.Increment(ref calls);

                if (partial)
                    return new ApiResult
                    {
                        Command = "oban",
                        Message = "SteamID заблокирован, но запись бана IP не подтверждена.",
                    };

                return new ApiResult
                {
                    Success = true,
                    Player = history.Find(request.Target),
                    Command = online ? "ban" : "oban",
                    BannedIp = request.Mode == "ip",
                };
            }
        );
        using var stop = new CancellationTokenSource();
        var server = Task.Run(() =>
            new ApiServer(new ApiConfig { Port = port, ApiKey = Key }, handler, Console.Error.WriteLine).RunAsync(
                stop.Token
            )
        );
        await ReadyAsync(port, server);

        using var api = new ServerApi();
        var connection = new ServerConfig { Port = port, ApiKey = Key };

        try
        {
            var recent = await api.QueryAsync(connection, "recentlyplayers");
            Check(
                recent.Players.Length == 75 && recent.Players.All(player => player.IpAddress == ""),
                "history HTTP endpoint returns bounded JSON without IP addresses"
            );

            var request = new ApiRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Target = id,
                Mode = "ip",
                Duration = "1d",
                Reason = "Проверка",
                Issuer = "Discord test",
            };
            var offline = await api.QueryAsync(connection, "recentban", request);
            Check(
                offline.Command == "oban" && offline.BannedIp && offline.Player!.UserId == id,
                "combined offline action metadata survives HTTP serialization"
            );
            request.Mode = "id";
            await ExpectAsync<UserError>(
                () => api.QueryAsync(connection, "recentban", request),
                "same request ID cannot change ID/IP mode"
            );
            Check(calls == 1, "changed ban mode never repeats native operation");
            request.RequestId = Guid.NewGuid().ToString("N");
            online = true;

            var live = await api.QueryAsync(connection, "recentban", request);
            Check(live.Command == "ban", "actual online procedure is returned for existing ban log routing");
            request.Mode = "kick";
            request.RequestId = Guid.NewGuid().ToString("N");
            await ExpectAsync<UserError>(
                () => api.QueryAsync(connection, "recentban", request),
                "history API rejects unexpected action mode"
            );
            Check(calls == 2, "invalid history action never reaches executor");
            request.Target = "123@steam";
            request.Mode = "ip";
            await ExpectAsync<UserError>(
                () => api.QueryAsync(connection, "recentban", request),
                "history action requires confirmed SteamID format"
            );
            request.Target = id;
            request.RequestId = Guid.NewGuid().ToString("N");
            partial = true;

            try
            {
                await api.QueryAsync(connection, "recentban", request);

                throw new Exception("Expected partial ban error");
            }
            catch (UserError error)
            {
                Check(
                    error.Command == "oban" && error.Message.Contains("IP не подтверждена"),
                    "partial ban errors preserve actual procedure and cannot appear as full success"
                );
            }
        }
        finally
        {
            await stop.CancelAsync();
            await server.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }
}
