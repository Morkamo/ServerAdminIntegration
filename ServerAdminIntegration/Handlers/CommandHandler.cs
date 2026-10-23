#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ServerAdminIntegration.Shared;

namespace ServerAdminIntegration.Handlers;

public sealed class CommandHandler
{
    private readonly Func<string, ApiRequest, CancellationToken, Task<ApiResult>>? _execute;
    private readonly Dictionary<string, (string Signature, DateTime Created, Task<ApiResult> Task)> _requests = new();

    public CommandHandler(Func<string, ApiRequest, CancellationToken, Task<ApiResult>>? execute = null) => _execute = execute;

    public async Task<string> ExecuteAsync(string command, CancellationToken token) =>
        (await ExecuteAsync(command, new ApiRequest(), token).ConfigureAwait(false)).Message;

    public Task<ApiResult> ExecuteAsync(string command, ApiRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        if (command == "ping")
            return Task.FromResult(new ApiResult { Success = true, Message = "pong" });

        if (command != "players"
            && command != "recentlyplayers"
            && command != "recentban"
            && command != "ban"
            && command != "oban"
            && command != "kick")
            throw new ArgumentException("Unknown command.");

        if (_execute == null)
            return Task.FromResult(ApiResult.Fail("Обработчик игрового сервера недоступен."));

        if (command == "players" || command == "recentlyplayers")
            return _execute(command, request, token);

        if (!Guid.TryParseExact(request.RequestId, "N", out _)
            || string.IsNullOrWhiteSpace(request.Target)
            || request.Target.Length > 128
            || request.Target.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(request.Reason)
            || request.Reason.Length > 1000
            || request.Reason.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(request.Issuer)
            || request.Issuer.Length > 100
            || request.Issuer.Any(char.IsControl))
            return Task.FromResult(ApiResult.Fail("Некорректные параметры команды."));

        try
        {
            if (command != "kick")
                BanDuration.Parse(request.Duration);
        }
        catch (ArgumentException exception)
        {
            return Task.FromResult(ApiResult.Fail(exception.Message));
        }

        if (command == "recentban"
            && (request.Mode != "id" && request.Mode != "ip"
                || !Components.RecentPlayersStore.IsSteamId(request.Target)))
            return Task.FromResult(ApiResult.Fail("Некорректный выбор блокировки из истории."));

        var signature = command + "\n"
            + request.Target + "\n"
            + request.Duration + "\n"
            + request.Reason + "\n"
            + request.Issuer + "\n"
            + request.Mode;

        lock (_requests)
        {
            if (_requests.TryGetValue(request.RequestId, out var previous))
                return previous.Signature == signature
                    ? previous.Task
                    : Task.FromResult(ApiResult.Fail("Идентификатор запроса уже использован."));

            foreach (var key in _requests
                .Where(x => x.Value.Task.IsCompleted && x.Value.Created < DateTime.UtcNow.AddMinutes(-15))
                .Select(x => x.Key)
                .ToArray())
                _requests.Remove(key);

            if (_requests.Count >= 2048)
                return Task.FromResult(ApiResult.Fail("Слишком много запросов. Повторите позже."));

            var task = _execute(command, request, token);
            _requests.Add(request.RequestId, (signature, DateTime.UtcNow, task));

            return task;
        }
    }
}
