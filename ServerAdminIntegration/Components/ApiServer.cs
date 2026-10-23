#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ServerAdminIntegration.Configs;
using ServerAdminIntegration.Handlers;
using ServerAdminIntegration.Shared;

namespace ServerAdminIntegration.Components;

public sealed class ApiServer
{
    private readonly ApiConfig _config;
    private readonly CommandHandler _commands;
    private readonly Action<string> _log;

    public ApiServer(ApiConfig config, CommandHandler commands, Action<string> log)
    {
        _config = config;
        _commands = commands;
        _log = log;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _config.Validate();
        var host = _config.Ip.Contains(":") ? "[" + _config.Ip + "]" : _config.Ip;
        using (var listener = new HttpListener())
        using (var slots = new SemaphoreSlim(_config.MaximumConcurrentRequests))
        {
            listener.Prefixes.Add($"http://{host}:{_config.Port}/");
            cancellationToken.ThrowIfCancellationRequested();
            listener.Start();
            _log($"API listening on {_config.Ip}:{_config.Port}.");
            var pending = new HashSet<Task>();
            using (cancellationToken.Register(listener.Close))
            {
                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var context = await listener.GetContextAsync().ConfigureAwait(false);
                        pending.RemoveWhere(task => task.IsCompleted);
                        if (!await slots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                        {
                            context.Response.StatusCode = 503;
                            context.Response.Close();
                            continue;
                        }
                        pending.Add(ProcessAsync(context, slots, cancellationToken));
                    }
                }
                catch (Exception exception) when (cancellationToken.IsCancellationRequested &&
                    (exception is HttpListenerException || exception is ObjectDisposedException || exception is OperationCanceledException)) { }
                finally
                {
                    listener.Close();
                    await Task.WhenAll(pending.ToArray()).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task ProcessAsync(HttpListenerContext context, SemaphoreSlim slots, CancellationToken stop)
    {
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(ApiProtocol.TimeoutSeconds));
            using (timeout.Token.Register(context.Response.Abort))
            {
                try
                {
                    var request = context.Request;
                    var url = request.Url;
                    var status = 200;
                    string text;
                    if (!MatchesKey(request.Headers[ApiProtocol.KeyHeader]))
                    {
                        status = 401;
                        text = "Unauthorized.";
                    }
                    else if (request.HttpMethod != "POST")
                    {
                        status = 405;
                        context.Response.Headers["Allow"] = "POST";
                        text = "Method not allowed.";
                    }
                    else if (request.HasEntityBody)
                    {
                        status = 400;
                        text = "This command does not accept a request body.";
                    }
                    else if (url == null || url.AbsolutePath != ApiProtocol.CommandPath + "ping" || url.Query.Length != 0)
                    {
                        status = 404;
                        text = "Unknown command.";
                    }
                    else
                    {
                        text = await _commands.ExecuteAsync("ping", timeout.Token).ConfigureAwait(false);
                    }
                    await ReplyAsync(context.Response, status, text, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    if (!timeout.IsCancellationRequested)
                    {
                        _log("API request failed: " + exception.GetType().Name);
                        try
                        {
                            await ReplyAsync(context.Response, 500, "Command failed.", timeout.Token).ConfigureAwait(false);
                        }
                        catch (Exception) { }
                    }
                }
                finally
                {
                    context.Response.Abort();
                    slots.Release();
                }
            }
        }
    }

    private bool MatchesKey(string? candidate)
    {
        if (candidate == null || candidate.Length != _config.ApiKey.Length)
            return false;
        var difference = 0;
        for (var index = 0; index < candidate.Length; index++)
            difference |= candidate[index] ^ _config.ApiKey[index];
        return difference == 0;
    }

    private static async Task ReplyAsync(HttpListenerResponse response, int status, string text, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.StatusCode = status;
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
        response.Close();
    }
}
