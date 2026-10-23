using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Npgsql;
using ServerAdminBot;
using ServerAdminBot.Components;
using ServerAdminBot.Database;
using ServerAdminIntegration.Components;
using ServerAdminIntegration.Configs;
using ServerAdminIntegration.Handlers;
using ServerAdminIntegration.Shared;

namespace IntegrationTests;

public static class Program
{
    private const string Key = "integration-test-key-0123456789abcdef";
    private static int _checks;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            await CheckConfigAsync();
            CheckPresentation();
            await CheckApiAsync();
            await CheckTimeoutsAsync();
            if (args.Length != 0)
                await CheckDatabaseAsync(int.Parse(args[0]));
            Console.WriteLine($"Passed {_checks} checks.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException("Failed: " + name);
        Interlocked.Increment(ref _checks);
    }

    private static async Task ExpectAsync<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); }
        catch (T) { Check(true, name); return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + name);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task ReadyAsync(int port, Task server)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            if (server.IsCompleted)
                await server;
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, limit.Token);
                return;
            }
            catch (SocketException) { await Task.Delay(25, limit.Token); }
        }
    }

    private static async Task CheckApiAsync()
    {
        var port = FreePort();
        var configuration = new ApiConfig { Port = port, ApiKey = Key, MaximumConcurrentRequests = 4 };
        using var stop = new CancellationTokenSource();
        var server = Task.Run(() => new ApiServer(configuration, new CommandHandler(), _ => { }).RunAsync(stop.Token));
        await ReadyAsync(port, server);
        using var api = new ServerApi();
        var connection = new ServerConfig { Port = port, ApiKey = Key };
        try
        {
            Check(await api.ExecuteAsync(connection, "ping") == "pong", "real plugin handler pong");
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            async Task<HttpStatusCode> RequestAsync(string path, HttpMethod method, string? key, bool body = false)
            {
                using var request = new HttpRequestMessage(method, new Uri(connection.Address, path));
                if (key != null) request.Headers.Add(ApiProtocol.KeyHeader, key);
                if (body) request.Content = new StringContent("unexpected");
                using var response = await http.SendAsync(request);
                return response.StatusCode;
            }
            Check(await RequestAsync("api/commands/ping", HttpMethod.Post, null) == HttpStatusCode.Unauthorized, "missing key rejected");
            Check(await RequestAsync("api/commands/ping", HttpMethod.Post, Key + "x") == HttpStatusCode.Unauthorized, "wrong key rejected");
            Check(await RequestAsync("api/commands/other", HttpMethod.Post, Key) == HttpStatusCode.NotFound, "unknown command rejected");
            Check(await RequestAsync("api/commands/ping?extra=1", HttpMethod.Post, Key) == HttpStatusCode.NotFound, "unexpected query rejected");
            Check(await RequestAsync("api/commands/ping", HttpMethod.Get, Key) == HttpStatusCode.MethodNotAllowed, "wrong method rejected");
            Check(await RequestAsync("api/commands/ping", HttpMethod.Post, Key, true) == HttpStatusCode.BadRequest, "unexpected body rejected");
            var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(async _ =>
            {
                try { return await api.ExecuteAsync(connection, "ping") == "pong"; }
                catch (UserError exception) when (exception.Message.Contains("занят")) { return true; }
            }));
            Check(results.All(x => x), "parallel requests bounded without losing listener");
            Check(await api.ExecuteAsync(connection, "ping") == "pong", "listener survives rejected requests");
        }
        finally
        {
            await stop.CancelAsync();
            await server.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Check(server.IsCompletedSuccessfully, "listener shutdown completes");
        using var restartStop = new CancellationTokenSource();
        var restarted = Task.Run(() => new ApiServer(configuration, new CommandHandler(), _ => { }).RunAsync(restartStop.Token));
        try
        {
            await ReadyAsync(port, restarted);
            Check(await api.ExecuteAsync(connection, "ping") == "pong", "port can be reused after stop");
        }
        finally
        {
            await restartStop.CancelAsync();
            await restarted.WaitAsync(TimeSpan.FromSeconds(3));
        }
        await ExpectAsync<UserError>(() => api.ExecuteAsync(connection, "ping"), "offline server fails clearly");
        Console.WriteLine("API: routing, authentication, concurrent requests, stop and restart passed.");
    }

    private static async Task CheckTimeoutsAsync()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var api = new ServerApi();
        var connection = new ServerConfig { Port = port, ApiKey = Key };

        async Task TimeoutAsync(bool sendHeaders)
        {
            var timer = Stopwatch.StartNew();
            var request = api.ExecuteAsync(connection, "ping");
            var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(3));
            try
            {
                if (sendHeaders)
                {
                    context.Response.SendChunked = true;
                    var prefix = Encoding.UTF8.GetBytes("p");
                    await context.Response.OutputStream.WriteAsync(prefix);
                    await context.Response.OutputStream.FlushAsync();
                }
                try { await request; throw new InvalidOperationException("Timeout missing"); }
                catch (UserError exception) { Check(exception.Message.StartsWith("Timeout:"), "timeout response"); }
                Check(timer.Elapsed.TotalSeconds >= 9 && timer.Elapsed.TotalSeconds < 13, "10-second total deadline");
            }
            finally { context.Response.Abort(); }
        }
        await Task.WhenAll(TimeoutAsync(false), TimeoutAsync(true));

        using (var cancelled = new CancellationTokenSource())
        {
            var request = api.ExecuteAsync(connection, "ping", cancelled.Token);
            var context = await listener.GetContextAsync();
            await cancelled.CancelAsync();
            await ExpectAsync<OperationCanceledException>(() => request, "shutdown cancellation preserved");
            context.Response.Abort();
        }

        async Task ErrorAsync(int status, string text, bool chunked, string expected)
        {
            var request = api.ExecuteAsync(connection, "ping");
            var context = await listener.GetContextAsync();
            try
            {
                context.Response.StatusCode = status;
                var bytes = Encoding.UTF8.GetBytes(text);
                if (chunked) context.Response.SendChunked = true;
                else context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
                try { await request; throw new InvalidOperationException("Expected rejection"); }
                catch (UserError exception) { Check(exception.Message.Contains(expected), "response validation: " + expected); }
            }
            finally { context.Response.Abort(); }
        }
        await ErrorAsync(401, "secret", false, "ключ");
        await ErrorAsync(302, "redirect", false, "HTTP 302");
        await ErrorAsync(200, "", false, "пустой");
        await ErrorAsync(200, new string('x', ApiProtocol.MaximumResponseBytes + 1), false, "большой");
        await ErrorAsync(200, new string('x', ApiProtocol.MaximumResponseBytes + 1), true, "большой");
        listener.Close();
        Console.WriteLine("Transport: 10-second header/body timeout, cancellation and response limits passed.");
    }

    private static async Task CheckConfigAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "server-admin-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json");
        try
        {
            await ExpectAsync<InvalidOperationException>(() => Config.LoadAsync(path, false, default), "missing config creates template");
            var generated = await Config.LoadAsync(path, false, default);
            Check(generated.Servers.Single().Key == "Classic" && generated.Servers.Single().Value.Port == 7777, "default server configuration");
            var text = """
                {"token":"test","guild_id":123,"servers":{
                  "Classic":{"ip":"127.0.0.1","port":7777,"api_key":"KEY"},
                  "Модовый":{"ip":"::1","port":7778,"api_key":"KEY"}}}
                """.Replace("KEY", Key);
            await File.WriteAllTextAsync(path, text);
            var config = await Config.LoadAsync(path, true, default);
            Check(config.Servers.ContainsKey("classic") && config.Servers.Count == 2, "arbitrary names and case-insensitive lookup");
            Check(config.Servers["Модовый"].Address.Host.Contains("::1"), "IPv6 URI");
            await File.WriteAllTextAsync(path, text.Replace("Модовый", "classic"));
            await ExpectAsync<InvalidOperationException>(() => Config.LoadAsync(path, true, default), "duplicate names rejected");
            await File.WriteAllTextAsync(path, text.Replace("127.0.0.1", "0.0.0.0"));
            await ExpectAsync<InvalidOperationException>(() => Config.LoadAsync(path, true, default), "wildcard destination rejected");
            await File.WriteAllTextAsync(path, text.Replace(Key, ""));
            await ExpectAsync<InvalidOperationException>(() => Config.LoadAsync(path, true, default), "empty API key rejected");
            await ExpectAsync<InvalidOperationException>(() => Task.Run(() => new DatabaseConfig
                { Host = "192.0.2.1", Password = "test" }.CreateDataSource().Dispose()), "remote database rejected");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void CheckPresentation()
    {
        var text = new string('a', 1999) + "😀" + new string('b', 2500);
        var parts = Ui.SplitText(text).ToArray();
        Check(string.Concat(parts) == text && parts.All(x => x.Length <= 2000), "text preserved across Discord message boundaries");
        Check(parts.All(x => !char.IsHighSurrogate(x[^1])), "surrogate pairs preserved");
        var controls = Enumerable.Range(0, 10).Select(index => Panel.Row(Panel.Select("select" + index, "Выбор", [("Опция", "value", false)])));
        var payload = JsonSerializer.SerializeToElement(Panel.Build(new[] { Panel.Text("Панель") }.Concat(controls)));
        Check(payload.GetProperty("flags").GetInt32() == 32768, "Components V2 enabled");
        var containers = payload.GetProperty("components").EnumerateArray().ToArray();
        Check(containers.All(x => x.GetProperty("components").GetArrayLength() <= 10) && containers.Length == 2, "ten select menus fit V2 containers");
        Check(!new PermissionSettings(false, []).Allows(false, []), "closed command denied");
        Check(new PermissionSettings(false, []).Allows(true, []), "administrator bypass");
        Check(new PermissionSettings(true, []).Allows(false, []), "everyone access");
        Check(new PermissionSettings(false, [42]).Allows(false, [42]), "role access");
    }

    private static async Task CheckDatabaseAsync(int port)
    {
        await using var source = new DatabaseConfig { Port = port, Password = "test-server-admin-password" }.CreateDataSource();
        var permissions = new CommandPermissions(source);
        await permissions.InitializeAsync(default);
        await permissions.InitializeAsync(default);
        const ulong guild = 123;
        Check((await permissions.GetAsync(guild, "ping")).Everyone, "new command open by default");
        await permissions.ToggleEveryoneAsync(guild, "ping");
        Check(!(await permissions.GetAsync(guild, "ping")).Everyone, "everyone toggle persisted");
        await permissions.UpdateRolesAsync(guild, "ping", [1, 2, 3], [2]);
        await permissions.UpdateRolesAsync(guild, "ping", [26, 27], [27]);
        Check((await permissions.GetAsync(guild, "ping")).Roles.SetEquals([2UL, 27UL]), "other role pages preserved");
        await Task.WhenAll(
            permissions.UpdateRolesAsync(guild, "ping", [1, 2, 3], [3]),
            permissions.UpdateRolesAsync(guild, "ping", [26, 27], [26]));
        Check((await permissions.GetAsync(guild, "ping")).Roles.SetEquals([3UL, 26UL]), "parallel page updates preserved");
        await permissions.UpdateRolesAsync(guild, "ping", [1, 2, 3], [], true);
        Check((await permissions.GetAsync(guild, "ping")).Everyone &&
            (await permissions.GetAsync(guild, "ping")).Roles.SetEquals([26UL]), "clear page and enable everyone");
        Check((await permissions.GetAsync(guild + 1, "ping")).Everyone &&
            (await permissions.GetAsync(guild, "pingserver")).Everyone, "guild and command isolation");
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => permissions.ToggleEveryoneAsync(guild, "ping")));
        Check((await permissions.GetAsync(guild, "ping")).Everyone, "concurrent toggles atomic");
        var fresh = new CommandPermissions(source);
        Check((await fresh.GetAsync(guild, "ping")).Roles.SetEquals([26UL]), "permissions survive new repository instance");
        await using var connection = await source.OpenConnectionAsync();
        await using var query = new NpgsqlCommand("CREATE ROLE forbidden_role", connection);
        await ExpectAsync<PostgresException>(() => query.ExecuteNonQueryAsync(), "bot is not a database administrator");
        Console.WriteLine("PostgreSQL: schema, defaults, isolation, concurrent updates and database privileges passed.");
    }
}
