using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DSharpPlus.Entities;

namespace ServerAdminBot.Components;

public static class Panel
{
    private static readonly HttpClient Http = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static object Text(string text) => new { type = 10, content = text };
    public static object Row(params object[] components) => new { type = 1, components };
    public static object Button(string id, string label, int style = 2, bool disabled = false) =>
        new { type = 2, custom_id = id, label, style, disabled };

    public static object Select(string id, string placeholder, IEnumerable<(string Label, string Value, bool Selected)> choices, bool multiple = false)
    {
        var options = choices.Select(x => new { label = x.Label, value = x.Value, @default = x.Selected }).ToArray();
        return new { type = 3, custom_id = id, placeholder, min_values = multiple ? 0 : 1, max_values = multiple ? options.Length : 1, options };
    }

    public static object Build(IEnumerable<object> components, int color = 0xFFA500) => new
    {
        flags = 32768,
        content = (string?)null,
        embeds = Array.Empty<object>(),
        allowed_mentions = new { parse = Array.Empty<string>() },
        components = components.Chunk(10).Select(chunk => new { type = 17, accent_color = color, components = chunk }).ToArray(),
    };

    public static async Task SendAsync(DiscordInteraction interaction, object payload, CancellationToken token = default)
    {
        await Gate.WaitAsync(token);
        try
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Patch,
                    $"https://discord.com/api/v10/webhooks/{interaction.ApplicationId}/{interaction.Token}/messages/@original?with_components=true")
                {
                    Content = JsonContent.Create(payload),
                };
                request.Headers.UserAgent.ParseAdd("DiscordBot (https://discord.com/developers/docs, 1.0.0)");
                using var response = await Http.SendAsync(request, token);
                if (response.IsSuccessStatusCode)
                    return;
                if (response.StatusCode != HttpStatusCode.TooManyRequests)
                    throw new UserError($"Discord не принял панель (HTTP {(int)response.StatusCode}). Откройте её заново.");
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                var seconds = body.RootElement.TryGetProperty("retry_after", out var delay) ? delay.GetDouble() : 1;
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 0.1, 60)), token);
            }
            throw new UserError("Discord временно ограничил обновление панели. Повторите позже.");
        }
        finally
        {
            Gate.Release();
        }
    }
}
