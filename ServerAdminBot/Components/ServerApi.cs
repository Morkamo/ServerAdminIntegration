using System.Net;
using ServerAdminIntegration.Shared;

namespace ServerAdminBot.Components;

public sealed class ServerApi : IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<string> ExecuteAsync(ServerConfig server, string command, CancellationToken stop = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(ApiProtocol.TimeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server.Address, ApiProtocol.CommandPath + Uri.EscapeDataString(command)));
        request.Headers.Add(ApiProtocol.KeyHeader, server.ApiKey);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new UserError(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Сервер отклонил ключ доступа API.",
                    HttpStatusCode.ServiceUnavailable => "API игрового сервера занят. Повторите позже.",
                    _ => $"API игрового сервера вернул ошибку HTTP {(int)response.StatusCode}.",
                });
            if (response.Content.Headers.ContentLength > ApiProtocol.MaximumResponseBytes)
                throw new UserError("Ответ игрового сервера слишком большой.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var bytes = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) != 0)
            {
                if (bytes.Length + read > ApiProtocol.MaximumResponseBytes)
                    throw new UserError("Ответ игрового сервера слишком большой.");
                bytes.Write(buffer, 0, read);
            }
            var text = System.Text.Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
            return string.IsNullOrWhiteSpace(text) ? throw new UserError("API игрового сервера вернул пустой ответ.") : text;
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            throw new UserError("Timeout: игровой сервер не ответил за 10 секунд.");
        }
        catch (HttpRequestException)
        {
            throw new UserError("Не удалось подключиться к API игрового сервера.");
        }
    }

    public void Dispose() => _http.Dispose();
}
