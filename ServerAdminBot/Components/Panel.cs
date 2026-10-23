using System.Net;
using System.Text.Json;
using DSharpPlus;
using DSharpPlus.Entities;

namespace ServerAdminBot.Components;

public static class Panel
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(2),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
    };

    public static object Text(string text) => new { type = 10, content = text };
    public static object Row(params object[] components) => new { type = 1, components };
    public static object Button(string id, string label, int style = 2, bool disabled = false) =>
        new { type = 2, custom_id = id, label, style, disabled };

    public static object Select(string id, string placeholder, IEnumerable<(string Label, string Value, bool Selected)> choices, bool multiple = false)
    {
        var options = choices.Select(x => new { label = x.Label, value = x.Value, @default = x.Selected }).ToArray();
        return new { type = 3, custom_id = id, placeholder, min_values = multiple ? 0 : 1, max_values = multiple ? options.Length : 1, options };
    }

    public static object Container(IEnumerable<object> components, int color = 0x008CFF) =>
        new { type = 17, accent_color = color, components = components.ToArray() };

    public static object Build(IEnumerable<object> components, int color = 0x008CFF) => new
    {
        flags = 32768,
        content = (string?)null,
        embeds = Array.Empty<object>(),
        allowed_mentions = new { parse = Array.Empty<string>() },
        components = components.Chunk(10).Select(chunk => new { type = 17, accent_color = color, components = chunk }).ToArray(),
    };

    public static object Blocks(params object[] blocks) => new
    {
        flags = 32768,
        allowed_mentions = new { parse = Array.Empty<string>() },
        components = blocks,
    };

    internal static Task RespondAsync(DiscordInteraction interaction, InteractionResponseType type, object? data = null, HttpClient? client = null) =>
        RequestAsync(HttpMethod.Post, $"https://discord.com/api/v10/interactions/{interaction.Id}/{interaction.Token}/callback",
            data == null ? new { type = (int)type } : (object)new { type = (int)type, data }, Program.StopToken, initial: true, client: client);

    public static Task DeferAsync(DiscordInteraction interaction, bool ephemeral = false, bool update = false) =>
        RespondAsync(interaction, update ? InteractionResponseType.DeferredMessageUpdate : InteractionResponseType.DeferredChannelMessageWithSource,
            update ? null : new { flags = ephemeral ? 64 : 0 });

    public static Task RejectAsync(DiscordInteraction interaction, string text) => RespondAsync(interaction,
        InteractionResponseType.ChannelMessageWithSource, new { content = text, flags = 64, allowed_mentions = new { parse = Array.Empty<string>() } });

    public static async Task<ulong> PublishAsync(ulong channel, object payload, CancellationToken token = default)
    {
        using var body = JsonDocument.Parse(await RequestAsync(HttpMethod.Post,
            $"https://discord.com/api/v10/channels/{channel}/messages", payload, token, authenticate: true));
        return ulong.Parse(body.RootElement.GetProperty("id").GetString()!);
    }

    public static async Task SendAsync(DiscordInteraction interaction, object payload, CancellationToken token = default)
        => await RequestAsync(HttpMethod.Patch,
            $"https://discord.com/api/v10/webhooks/{interaction.ApplicationId}/{interaction.Token}/messages/@original?with_components=true", payload, token);

    internal static async Task<string> RequestAsync(HttpMethod method, string url, object? payload, CancellationToken token,
        bool authenticate = false, bool initial = false, HttpClient? client = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(initial ? 2.5 : 30));
        try
        {
            for (var attempt = 0; attempt < (initial ? 1 : 5); attempt++)
            {
                using var request = new HttpRequestMessage(method, url)
                {
                    Content = payload == null ? null : new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json"),
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                };
                if (authenticate)
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bot", Program.Config.Token);
                request.Headers.UserAgent.ParseAdd("DiscordBot (https://discord.com/developers/docs, 1.0.0)");
                using var response = await (client ?? Http).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (response.IsSuccessStatusCode)
                    return initial || response.StatusCode == HttpStatusCode.NoContent ? "" : await response.Content.ReadAsStringAsync(deadline.Token);
                if (response.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    var code = "unknown";
                    var errorText = await response.Content.ReadAsStringAsync(deadline.Token);
                    try
                    {
                        using var error = JsonDocument.Parse(errorText);
                        if (error.RootElement.TryGetProperty("code", out var value)) code = value.ToString();
                    }
                    catch (JsonException) { }
                    Console.Error.WriteLine($"Discord {(initial ? "callback" : "response")}: HTTP {(int)response.StatusCode}, code {code}.");
                    throw new UserError($"Discord отклонил ответ (HTTP {(int)response.StatusCode}, код {code}). Повторите команду.");
                }
                if (initial || attempt == 4)
                    throw new UserError("Discord временно ограничил ответы. Повторите команду позже.");
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
                var seconds = body.RootElement.TryGetProperty("retry_after", out var delay) ? delay.GetDouble() : 1;
                if (!double.IsFinite(seconds) || seconds < 0 || seconds > 30)
                    throw new UserError("Discord временно ограничил ответы. Повторите команду позже.");
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(seconds, 0.1)), deadline.Token);
            }
            throw new UserError("Discord временно ограничил обновление панели. Повторите позже.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new UserError(initial ? "Discord не подтвердил команду вовремя. Запрос к игре не отправлен." :
                "Discord не ответил при отправке результата. Проверьте сообщения перед повторением действия.");
        }
        catch (HttpRequestException) { throw new UserError("Не удалось связаться с Discord API. Повторите команду позже."); }
    }
}
