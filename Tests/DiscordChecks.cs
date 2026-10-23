using System.Diagnostics;
using System.Net;
using System.Text.Json;
using DSharpPlus;
using DSharpPlus.Entities;
using ServerAdminBot.Components;

namespace IntegrationTests;

public static partial class Program
{
    private sealed class DiscordHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            send(request, token);
    }

    private sealed class UnreadContent : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("Callback response body must not be read.");
    }

    private static async Task CheckDiscordAsync()
    {
        var interaction = Newtonsoft.Json.JsonConvert.DeserializeObject<DiscordInteraction>(
            "{\"id\":\"123456789012345678\",\"application_id\":\"123\",\"token\":\"test-interaction-token\"}"
        )!;
        var seen = new List<JsonElement>();
        using var http = new HttpClient(
            new DiscordHandler(
                async (request, token) =>
                {
                    Check(
                        request.RequestUri!.AbsolutePath.EndsWith("/" + interaction.Token + "/callback")
                            && request.RequestUri.Query == "",
                        "callback uses official route without wait query"
                    );
                    Check(
                        request.Content?.Headers.ContentType?.MediaType == "application/json",
                        "initial response uses JSON rather than multipart"
                    );
                    Check(
                        request.Content?.Headers.ContentLength > 0,
                        "callback body has a fixed content length for HTTP fallback"
                    );

                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                    seen.Add(body.RootElement.Clone());

                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
            )
        );
        await Panel.RespondAsync(
            interaction,
            InteractionResponseType.DeferredChannelMessageWithSource,
            new { flags = 64 },
            http
        );
        await Panel.RespondAsync(interaction, InteractionResponseType.DeferredMessageUpdate, client: http);
        await Panel.RespondAsync(
            interaction,
            InteractionResponseType.Modal,
            PlayersUi.BuildModal("key", "nonce", true),
            http
        );
        Check(
            seen[0].GetProperty("type").GetInt32() == 5
                && seen[0].GetProperty("data").GetProperty("flags").GetInt32() == 64,
            "private slash defer has correct callback type and flags"
        );
        Check(
            seen[1].GetProperty("type").GetInt32() == 6 && !seen[1].TryGetProperty("data", out _),
            "component update acknowledges without message payload"
        );

        var modal = seen[2].GetProperty("data");
        Check(
            seen[2].GetProperty("type").GetInt32() == 9
                && modal.GetProperty("custom_id").GetString() == "playermodal:key:nonce",
            "ban modal response has matching session identity"
        );

        var fields = modal
            .GetProperty("components")
            .EnumerateArray()
            .Select(row => row.GetProperty("components")[0])
            .ToArray();
        Check(
            fields.Length == 2
                && fields.All(field =>
                    field.GetProperty("required").GetBoolean() && field.GetProperty("label").GetString()!.Length <= 45
                ),
            "ban modal has two required fields within Discord limits"
        );

        var kick = JsonSerializer.SerializeToElement(PlayersUi.BuildModal("key", "nonce", false));
        Check(kick.GetProperty("components").GetArrayLength() == 1, "kick modal only asks for reason");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var isolated = new HttpClient(
            new DiscordHandler(
                async (request, token) =>
                {
                    if (request.RequestUri!.AbsolutePath == "/blocked")
                    {
                        entered.TrySetResult();
                        await Task.Delay(Timeout.Infinite, token);
                    }

                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnreadContent() };
                }
            )
        );
        var timer = Stopwatch.StartNew();
        var blocked = Panel.RequestAsync(
            HttpMethod.Post,
            "https://discord.test/blocked",
            new { type = 5 },
            default,
            initial: true,
            client: isolated
        );
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Panel
            .RequestAsync(
                HttpMethod.Post,
                "https://discord.test/independent",
                new { type = 5 },
                default,
                initial: true,
                client: isolated
            )
            .WaitAsync(TimeSpan.FromSeconds(1));
        Check(!blocked.IsCompleted, "one stalled callback does not queue another callback");
        await ExpectAsync<UserError>(() => blocked, "stalled callback fails before Discord three-second deadline");
        Check(
            timer.Elapsed.TotalSeconds >= 2 && timer.Elapsed.TotalSeconds < 3.5,
            "callback deadline replaces SDK hundred-second wait"
        );
        await Panel.RequestAsync(
            HttpMethod.Post,
            "https://discord.test/recovered",
            new { type = 5 },
            default,
            initial: true,
            client: isolated
        );
        Check(true, "callback transport survives timeout without stale locks");

        using (var pendingStop = new CancellationTokenSource())
        {
            var pendingPublish = Panel.RequestAsync(
                HttpMethod.Post,
                "https://discord.test/blocked",
                new { components = Array.Empty<object>() },
                pendingStop.Token,
                client: isolated
            );
            await Panel
                .RequestAsync(
                    HttpMethod.Post,
                    "https://discord.test/other-callback",
                    new { type = 5 },
                    default,
                    initial: true,
                    client: isolated
                )
                .WaitAsync(TimeSpan.FromSeconds(1));
            Check(!pendingPublish.IsCompleted, "stalled panel publication cannot block initial acknowledgement");
            await pendingStop.CancelAsync();
            await ExpectAsync<OperationCanceledException>(
                () => pendingPublish,
                "shutdown cancels an in-flight Discord request"
            );
        }

        var attempts = 0;
        using var limited = new HttpClient(
            new DiscordHandler(
                (_, _) =>
                {
                    var response =
                        Interlocked.Increment(ref attempts) == 1
                            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                            {
                                Content = new StringContent("{\"retry_after\":0.1}"),
                            }
                            : new HttpResponseMessage(HttpStatusCode.NoContent);

                    return Task.FromResult(response);
                }
            )
        );
        await Panel.RequestAsync(
            HttpMethod.Patch,
            "https://discord.test/message",
            new { content = "test" },
            default,
            client: limited
        );
        Check(attempts == 2, "message update obeys Discord retry_after");
        attempts = 0;
        await ExpectAsync<UserError>(
            () =>
                Panel.RequestAsync(
                    HttpMethod.Post,
                    "https://discord.test/callback",
                    new { type = 5 },
                    default,
                    initial: true,
                    client: limited
                ),
            "initial response is not queued for retry after rate limit"
        );
        Check(attempts == 1, "initial response never automatically repeats after uncertain acknowledgement");

        using var unavailable = new HttpClient(
            new DiscordHandler(
                (_, _) =>
                    Task.FromResult(
                        new HttpResponseMessage(HttpStatusCode.BadGateway)
                        {
                            Content = new StringContent("<html>upstream unavailable</html>"),
                        }
                    )
            )
        );
        await ExpectAsync<UserError>(
            () =>
                Panel.RequestAsync(
                    HttpMethod.Patch,
                    "https://discord.test/message",
                    new { },
                    default,
                    client: unavailable
                ),
            "non-JSON Discord error still produces a clear error"
        );

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ExpectAsync<OperationCanceledException>(
            () =>
                Panel.RequestAsync(
                    HttpMethod.Post,
                    "https://discord.test/blocked",
                    new { },
                    cancelled.Token,
                    initial: true,
                    client: isolated
                ),
            "shutdown cancellation is preserved"
        );

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatch = ServerAdminBot.Program.DispatchAsync(async () =>
        {
            started.SetResult();
            await release.Task;
            finished.SetResult();
        });
        Check(dispatch.IsCompletedSuccessfully, "component dispatch immediately releases gateway receive loop");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await ServerAdminBot.Program.DispatchAsync(() =>
        {
            second.SetResult();

            return Task.CompletedTask;
        });
        await second.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Check(!finished.Task.IsCompleted, "another interaction runs while first component action is pending");
        release.SetResult();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Console.WriteLine(
            "Discord: callback JSON, isolated timeouts, rate limits, modals and gateway dispatch passed."
        );
    }
}
