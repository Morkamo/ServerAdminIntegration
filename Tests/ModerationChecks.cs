using System.Net;
using System.Text;
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
    private static async Task CheckModerationAsync()
    {
        var special = "Кириллица 😀 \" \\\n\t\r\b\f\0\u001f / <@everyone>";
        var encoded = ApiJson.WriteResult(
            new ApiResult
            {
                Success = true,
                Message = special,
                Reason = special,
                DurationSeconds = long.MaxValue,
                Player = new PlayerInfo
                {
                    PlayerId = 7,
                    Nickname = special,
                    UserId = "123@steam",
                },
                Players =
                [
                    new PlayerInfo
                    {
                        PlayerId = 8,
                        Nickname = special,
                        UserId = "456@steam",
                    },
                ],
            }
        );
        var decoded = JsonSerializer.Deserialize<ApiResult>(encoded)!;
        Check(
            decoded.Success
                && decoded.Message == special
                && decoded.Reason == special
                && decoded.DurationSeconds == long.MaxValue
                && decoded.Player?.Nickname == special
                && decoded.Players[0].Nickname == special,
            "game JSON writer escapes Unicode, quotes and control characters without data loss"
        );

        var empty = JsonSerializer.Deserialize<ApiResult>(ApiJson.WriteResult(new ApiResult()))!;
        Check(
            empty.Player == null && empty.Players.Length == 0 && !empty.Success,
            "game JSON writer preserves null player and empty roster"
        );

        var source = new ApiRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Target = "123@steam",
            Duration = "1h",
            Reason = special,
            Issuer = "Discord",
        };
        var parsed = ApiJson.ReadRequest(JsonSerializer.SerializeToUtf8Bytes(source));
        Check(
            parsed.RequestId == source.RequestId
                && parsed.Target == source.Target
                && parsed.Duration == source.Duration
                && parsed.Reason == source.Reason
                && parsed.Issuer == source.Issuer,
            "game JSON reader accepts bot request with escaped Unicode and quotes"
        );

        foreach (var invalid in new[]
            {
                "",
                "null",
                "[]",
                "{",
                "{\"Target\":123}",
                "{\"Target\":\"a\",\"Target\":\"b\"}",
                "{\"Unknown\":\"value\"}",
                "{}garbage",
                "{}{}",
                "{\"Target\":\"unfinished",
                "{\"Target\":\"\\u",
            })
            await ExpectAsync<Utf8Json.JsonParsingException>(
                () => Task.Run(() => ApiJson.ReadRequest(Encoding.UTF8.GetBytes(invalid))),
                "game JSON rejects " + invalid
            );

        foreach (
            var (input, seconds) in new (string, long)[]
            {
                ("1s", 1),
                ("2S", 2),
                ("5", 300),
                ("3m", 180),
                ("2h", 7200),
                ("2H", 7200),
                ("1d", 86400),
                ("1D", 86400),
                ("1M", 2592000),
                ("1y", 31536000),
                ("1Y", 31536000),
                ("4294967295s", uint.MaxValue),
            }
        )
            Check(BanDuration.Parse(input) == seconds, "duration " + input);

        foreach (var invalid in new[]
            {
                "",
                "0",
                "0h",
                "-1d",
                "1h30m",
                "1w",
                "1.5h",
                "+1m",
                "1 d",
                "4294967296s",
                "137y",
                "9999999999999999y",
            })
            await ExpectAsync<ArgumentException>(
                () => Task.Run(() => BanDuration.Parse(invalid)),
                "invalid duration " + invalid
            );

        var roster = Enumerable
            .Range(1, 20)
            .Select(index => new PlayerInfo
            {
                PlayerId = index,
                Nickname = "Игрок " + index,
                UserId = "765611980000000" + index.ToString("00") + "@steam",
            })
            .ToArray();
        Check(PlayerSelection.Find(roster, roster[1].UserId) == 1, "select by stable AuthID");
        Check(PlayerSelection.Find(roster, "3") == 2, "select one player id");
        Check(PlayerSelection.Find(roster, "игрок 4") == 3, "select exact nickname");
        Check(PlayerSelection.Find(roster, "Игрок") == -1, "no partial nickname ban");
        Check(PlayerSelection.Find(roster, "2.3") == -1, "RA multi-player syntax not accepted");

        var leftId = roster[0].UserId;
        var replacement = new[]
        {
            new PlayerInfo
            {
                PlayerId = 1,
                UserId = "other@steam",
                Nickname = leftId,
            },
        };
        Check(
            PlayerSelection.Find(replacement, leftId) == -1,
            "departed AuthID cannot resolve through reused slot or spoofed nickname"
        );
        await ExpectAsync<ArgumentException>(
            () =>
                Task.Run(() =>
                    PlayerSelection.Find(
                        [new PlayerInfo { Nickname = "Same" }, new PlayerInfo { Nickname = "same" }],
                        "Same"
                    )
                ),
            "ambiguous nickname rejected"
        );

        var payload = JsonSerializer.SerializeToElement(PlayersUi.BuildList("key", "Classic", roster, 0));
        static IEnumerable<JsonElement> Components(JsonElement root)
        {
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("type", out _))
                    yield return root;

                if (root.TryGetProperty("components", out var children))
                    foreach (var child in children.EnumerateArray())
                    foreach (var component in Components(child))
                        yield return component;
            }
        }
        var components = Components(payload).ToArray();
        Check(components.Length <= 40, "15-player panel fits Discord component budget");

        var playerButtons = components
            .Where(x =>
                x.GetProperty("type").GetInt32() == 2 && x.GetProperty("custom_id").GetString()!.Contains(":select:")
            )
            .ToArray();
        Check(playerButtons.Length == 15, "15 players on first page");
        Check(playerButtons.All(x => x.GetProperty("style").GetInt32() == 2), "player buttons use neutral style");
        Check(
            components
                .Where(x => x.GetProperty("type").GetInt32() == 17)
                .All(x => x.GetProperty("accent_color").GetInt32() == 0x008CFF),
            "exact blue color"
        );

        var second = Components(JsonSerializer.SerializeToElement(PlayersUi.BuildList("key", "Classic", roster, 1)))
            .ToArray();
        Check(
            second.Count(x =>
                x.GetProperty("type").GetInt32() == 2 && x.GetProperty("custom_id").GetString()!.Contains(":select:")
            ) == 5,
            "remaining players on second page"
        );

        var longLabel = PlayersUi.PlayerLabel(
            new PlayerInfo
            {
                PlayerId = 32,
                Nickname = new string('а', 200) + "😀",
                UserId = "123456789@steam",
            }
        );
        Check(
            longLabel.Length <= 80 && longLabel.EndsWith("(123456789@steam)"),
            "button label preserves identity within limit"
        );

        var success = JsonSerializer.SerializeToElement(
            Moderation.Result(
                "ban",
                "Classic",
                new ApiResult
                {
                    Success = true,
                    Player = roster[0],
                    Reason = "Причина @everyone **текст**",
                    DurationSeconds = 86400,
                }
            )
        );
        Check(
            success.GetProperty("components").GetArrayLength() == 1
                && !success.ToString().Contains("Команда успешно обработана"),
            "moderation response immediately shows actual result without generic acknowledgement"
        );
        Check(
            success.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength() == 0,
            "public result cannot ping users"
        );

        var logSource = Panel.Build([
            Panel.Text("Результат @everyone <@123>"),
            Panel.Row(Panel.Button("private-control", "Управление")),
        ]);
        var log = JsonSerializer.SerializeToElement(CommandLog.Build(123, logSource));
        Check(
            log.GetProperty("components")[0].GetProperty("content").GetString() == "От: <@123>",
            "command log renders a real Discord identity above the result"
        );
        Check(
            log.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength() == 0
                && (log.GetProperty("flags").GetInt32() & 4096) != 0,
            "command log suppresses actor, embedded mentions and push notifications"
        );
        Check(
            !Components(log).Any(component => component.GetProperty("type").GetInt32() is 1 or 2 or 3),
            "logged permission panels contain no live controls"
        );
        Check(
            Components(JsonSerializer.SerializeToElement(logSource))
                .Any(component => component.GetProperty("type").GetInt32() == 2),
            "logging does not mutate original interactive response"
        );

        var loggedPages = Enumerable
            .Range(0, 2)
            .Select(page =>
                JsonSerializer.SerializeToElement(
                    CommandLog.Build(123, PlayersUi.BuildList("key", "Classic", roster, page, readOnly: true))
                )
            )
            .ToArray();
        Check(
            loggedPages.All(page =>
                Components(page).Count() <= 40
                && !Components(page).Any(component => component.GetProperty("type").GetInt32() == 2)
            ),
            "player log pages fit component limits and cannot moderate players"
        );

        var logText = string.Join(
            "\n",
            loggedPages
                .SelectMany(Components)
                .Where(component => component.GetProperty("type").GetInt32() == 10)
                .Select(component => component.GetProperty("content").GetString())
        );
        Check(
            roster.All(player => logText.Contains(Ui.Escape(player.UserId))),
            "all players are present across log pages"
        );

        Action? queued = null;
        var executed = 0;

        using (var cancelled = new CancellationTokenSource())
        {
            var task = GameThread.RunAsync(action => queued = action, () => ++executed, cancelled.Token);
            Check(executed == 0, "game operation waits for dispatcher");
            await cancelled.CancelAsync();
            await ExpectAsync<OperationCanceledException>(() => task, "queued operation cancels");
            queued!();
            Check(executed == 0, "expired operation never executes later");
        }

        using (var running = new CancellationTokenSource())
        {
            var task = GameThread.RunAsync(
                action => action(),
                () =>
                {
                    running.Cancel();

                    return ++executed;
                },
                running.Token
            );
            Check(await task == 1, "cancellation cannot misreport an already executing operation");
        }

        using (var alreadyCancelled = new CancellationTokenSource())
        {
            alreadyCancelled.Cancel();
            await ExpectAsync<OperationCanceledException>(
                () =>
                    GameThread.RunAsync<int>(
                        _ => throw new Exception("must not dispatch"),
                        () => 0,
                        alreadyCancelled.Token
                    ),
                "stopped plugin never dispatches"
            );
        }

        await ExpectAsync<InvalidOperationException>(
            () => GameThread.RunAsync<int>(action => action(), () => throw new InvalidOperationException(), default),
            "native errors propagate asynchronously"
        );
        Console.WriteLine("Moderation: durations, target identity, panel layout and game-thread cancellation passed.");
    }

    private static async Task CheckModerationApiAsync()
    {
        var port = FreePort();
        var calls = 0;
        var live = new PlayerInfo
        {
            PlayerId = 2,
            Nickname = "Моркамо 😀",
            UserId = "76561198000000001@steam",
        };
        var online = true;
        var failRoster = false;
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var handler = new CommandHandler(
            (command, request, token) =>
            {
                if (command == "players")
                {
                    if (failRoster)
                        throw new InvalidOperationException("Roster failure", new Exception("Underlying failure"));

                    return Task.FromResult(new ApiResult { Success = true, Players = online ? [live] : [] });
                }

                Interlocked.Increment(ref calls);

                if (command != "oban" && (!online || request.Target != live.UserId))
                    return Task.FromResult(ApiResult.Fail("Не удалось найти данного игрока."));

                return Task.FromResult(
                    new ApiResult
                    {
                        Success = true,
                        Player = live,
                        Reason = request.Reason,
                        DurationSeconds = command == "kick" ? 0 : BanDuration.Parse(request.Duration),
                        Message = "Успех",
                    }
                );
            }
        );
        using var stop = new CancellationTokenSource();
        var server = Task.Run(() =>
            new ApiServer(new ApiConfig { Port = port, ApiKey = Key }, handler, logs.Enqueue).RunAsync(stop.Token)
        );
        await ReadyAsync(port, server);

        using var api = new ServerApi();
        var connection = new ServerConfig { Port = port, ApiKey = Key };

        try
        {
            var roster = await api.QueryAsync(connection, "players");
            Check(
                roster.Players.Length == 1 && roster.Players[0].Nickname == live.Nickname,
                "plugin JSON roster round trip with Unicode"
            );

            var request = new ApiRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Target = live.UserId,
                Duration = "1d",
                Reason = "Причина с \"кавычками\" и пробелами",
                Issuer = "Administrator (Discord 123)",
            };
            var responses = await Task.WhenAll(
                Enumerable.Range(0, 8).Select(_ => api.QueryAsync(connection, "ban", request))
            );
            Check(
                calls == 1 && responses.All(x => x.Success && x.Reason == request.Reason && x.DurationSeconds == 86400),
                "duplicate HTTP operations execute once"
            );
            request.Reason = "different";
            await ExpectAsync<UserError>(
                () => api.QueryAsync(connection, "ban", request),
                "request id cannot be reused for different data"
            );
            Check(calls == 1, "conflicting replay has no game side effects");
            request.RequestId = Guid.NewGuid().ToString("N");
            request.Duration = "0";
            await ExpectAsync<UserError>(
                () => api.QueryAsync(connection, "ban", request),
                "zero ban rejected at plugin boundary"
            );
            Check(calls == 1, "invalid duration never reaches game handler");
            request.Duration = "1h";
            request.Reason = "malicious\nline";
            await ExpectAsync<UserError>(
                () => api.QueryAsync(connection, "oban", request),
                "ban-file newline injection rejected"
            );
            Check(calls == 1, "invalid reason never reaches game handler");
            request.Reason = "Нормальная причина";
            request.RequestId = Guid.NewGuid().ToString("N");
            online = false;

            try
            {
                await api.QueryAsync(connection, "kick", request);

                throw new Exception("Expected missing player");
            }
            catch (UserError exception)
            {
                Check(
                    exception.Message == "Не удалось найти данного игрока.",
                    "departed player error preserved exactly"
                );
            }

            var offline = await api.QueryAsync(
                connection,
                "oban",
                new ApiRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    Target = "76561198000000002@steam",
                    Duration = "1y",
                    Reason = "Офлайн бан",
                    Issuer = "Discord 123",
                }
            );
            Check(offline.Success && offline.DurationSeconds == 31536000, "offline moderation route");

            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            using var malformed = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(connection.Address, "api/commands/ban")
            );
            malformed.Headers.Add(ApiProtocol.KeyHeader, Key);
            malformed.Content = new StringContent("{not-json", Encoding.UTF8, "application/json");

            using var invalid = await http.SendAsync(malformed);
            Check(invalid.StatusCode == HttpStatusCode.BadRequest, "malformed mutation payload rejected");

            using var wrongType = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(connection.Address, "api/commands/ban")
            );
            wrongType.Headers.Add(ApiProtocol.KeyHeader, Key);
            wrongType.Content = new StringContent("{\"Target\":123}", Encoding.UTF8, "application/json");

            using var invalidType = await http.SendAsync(wrongType);
            Check(invalidType.StatusCode == HttpStatusCode.BadRequest, "invalid JSON property type returns 400");
            failRoster = true;
            await ExpectAsync<UserError>(
                () => api.QueryAsync(connection, "players"),
                "server operation failure reported to bot"
            );
            Check(
                logs.Any(log =>
                    log.Contains("InvalidOperationException: Roster failure")
                    && log.Contains("Underlying failure")
                    && log.Contains(" at ")
                ),
                "plugin logs message, inner exception and stack trace"
            );
        }
        finally
        {
            await stop.CancelAsync();
            await server.WaitAsync(TimeSpan.FromSeconds(3));
        }

        Console.WriteLine("Moderation API: JSON, duplicate protection, validation and offline targets passed.");
    }
}
