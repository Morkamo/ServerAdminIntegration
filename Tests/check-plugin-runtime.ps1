param(
    [string]$Configuration = 'Release',
    [string]$GameManagedDirectory = 'C:\SteamLibrary\steamapps\common\SCP Secret Laboratory Dedicated Server\SCPSL_Data\Managed'
)

$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core')
{
    throw 'Run with Windows PowerShell (powershell.exe) to test .NET Framework.'
}

$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskPlugin = Join-Path $taskRoot "ServerAdminIntegration\bin\x64\$Configuration\ServerAdminIntegration.dll"
$taskDependencies = Get-ChildItem -LiteralPath $GameManagedDirectory -Filter '*.dll' | Select-Object -ExpandProperty FullName

if ([Reflection.AssemblyName]::GetAssemblyName($taskPlugin).ProcessorArchitecture -ne 'Amd64')
{
    throw 'Expected x64 plugin.'
}

[Reflection.Assembly]::LoadFrom($taskPlugin) | Out-Null

Add-Type -ReferencedAssemblies @($taskPlugin, 'System.dll', 'System.Core.dll', 'System.Net.Http.dll') -TypeDefinition @'
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ServerAdminIntegration.Components;
using ServerAdminIntegration.Configs;
using ServerAdminIntegration.Handlers;
using ServerAdminIntegration.Shared;

public static class PluginRuntimeCheck
{
    public static void Run(string[] dependencies)
    {
        var forbidden = new[]
        {
            "System.Text.Json",
            "System.Text.Encodings.Web",
            "System.Memory",
            "System.Runtime.Serialization",
        };

        if (typeof(ApiServer).Assembly.GetReferencedAssemblies().Any(assembly => forbidden.Contains(assembly.Name)))
            throw new Exception("Plugin still depends on an incompatible serializer.");

        ResolveEventHandler resolve = (sender, args) =>
        {
            var name = new AssemblyName(args.Name).Name;
            var path = dependencies.FirstOrDefault(file => Path.GetFileNameWithoutExtension(file) == name);

            return path == null ? null : Assembly.LoadFrom(path);
        };
        AppDomain.CurrentDomain.AssemblyResolve += resolve;

        try
        {
            CheckAsync().GetAwaiter().GetResult();
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyResolve -= resolve;
        }
    }

    private static async Task CheckAsync()
    {
        await CheckHistoryAsync();

        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();

        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();

        var key = "framework-test-key-0123456789abcdef";
        var calls = 0;
        var player = new PlayerInfo
        {
            PlayerId = 2,
            Nickname = "Тест 😀",
            UserId = "123456789@steam",
        };
        var handler = new CommandHandler(
            (command, request, token) =>
            {
                if (command == "players")
                    return Task.FromResult(new ApiResult { Success = true, Players = new[] { player } });

                if (request.Reason != "Причина с \"кавычками\"")
                    throw new Exception("Request Unicode changed.");

                Interlocked.Increment(ref calls);

                return Task.FromResult(
                    new ApiResult
                    {
                        Success = true,
                        Player = player,
                        Reason = request.Reason,
                    }
                );
            }
        );

        using (var stop = new CancellationTokenSource())
        using (var http = new HttpClient(new HttpClientHandler { UseProxy = false }))
        {
            var api = new ApiServer(new ApiConfig { Port = port, ApiKey = key }, handler, Console.WriteLine);
            var server = api.RunAsync(stop.Token);
            http.BaseAddress = new Uri("http://127.0.0.1:" + port + "/api/commands/");
            http.DefaultRequestHeaders.Add(ApiProtocol.KeyHeader, key);

            try
            {
                using (var roster = await http.PostAsync("players", null))
                {
                    var body = await roster.Content.ReadAsStringAsync();

                    if (!roster.IsSuccessStatusCode
                        || !body.Contains("\"PlayerId\":2")
                        || !body.Contains(player.UserId))
                        throw new Exception("Roster serialization failed: " + body);
                }

                var payload =
                    "{\"RequestId\":\""
                    + Guid.NewGuid().ToString("N")
                    + "\",\"Target\":\"123456789@steam\",\"Duration\":\"1h\",\"Reason\":\"Причина с \\\"кавычками\\\"\",\"Issuer\":\"Discord test\"}";

                using (
                    var response = await http.PostAsync(
                        "ban",
                        new StringContent(payload, Encoding.UTF8, "application/json")
                    )
                )
                {
                    var body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode || calls != 1 || !body.Contains("\"Success\":true"))
                        throw new Exception("Mutation JSON round trip failed: " + body);
                }

                using (
                    var response = await http.PostAsync(
                        "ban",
                        new StringContent("{invalid", Encoding.UTF8, "application/json")
                    )
                )
                    if (response.StatusCode != HttpStatusCode.BadRequest || calls != 1)
                        throw new Exception("Invalid JSON was not rejected.");

                Console.WriteLine(
                    ".NET Framework plugin HTTP checks passed using game assemblies: roster, Unicode mutation, invalid JSON."
                );
            }
            finally
            {
                stop.Cancel();
                server.GetAwaiter().GetResult();
            }
        }
    }

    private static async Task CheckHistoryAsync()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "server-admin-framework-history-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, "recent-players.json");

        using (var stop = new CancellationTokenSource())
        {
            var history = new RecentPlayersStore(path, Console.WriteLine);
            var worker = Task.Run(() => history.RunAsync(stop.Token));

            try
            {
                await history.WaitReadyAsync(CancellationToken.None);
                history.Record("76561198000000001@steam", "Тест 😀 \"ник\"", "203.0.113.40", DateTime.UtcNow.Ticks);
                await Task.Delay(500);
                history.Record("76561198000000001@steam", "Обновлённый", "2001:db8::1", DateTime.UtcNow.Ticks);
                await Task.Delay(500);

                var saved = ApiJson.ReadHistory(File.ReadAllBytes(path));

                if (saved.Length != 1 || saved[0].IpAddress != "2001:db8::1" || saved[0].Nickname != "Обновлённый")
                    throw new Exception("Framework history save/replace failed.");

                if (history.Latest()[0].IpAddress != "")
                    throw new Exception("History API exposes IP.");

                Console.WriteLine(
                    ".NET Framework history checks passed: game JSON, atomic replacement, unique Steam ID, hidden IP."
                );
            }
            finally
            {
                history.Stop();
                stop.Cancel();

                try
                {
                    worker.GetAwaiter().GetResult();
                }
                finally
                {
                    Directory.Delete(directory, true);
                }
            }
        }
    }
}
'@
[PluginRuntimeCheck]::Run([string[]]$taskDependencies)
