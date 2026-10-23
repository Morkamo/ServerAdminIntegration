using System.Text.Json;
using System.Text.Json.Nodes;

namespace ServerAdminBot.Components;

public static class CommandLog
{
    public static void Write(string command, ulong actor, params object[] results)
    {
        if (!Program.Config.CommandLogs.TryGetValue(command, out var channel) || channel == 0)
            return;

        _ = Program.DispatchAsync(async () =>
        {
            try
            {
                foreach (var result in results)
                    await Panel.PublishAsync(channel, Build(actor, result), Program.StopToken);
            }
            catch (OperationCanceledException) when (Program.StopToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Command log /{command} -> {channel}: {exception.GetType().Name}");
            }
        });
    }

    internal static object Build(ulong actor, object result)
    {
        var payload = JsonSerializer.SerializeToNode(result)!.AsObject();
        var components = payload["components"]!.AsArray();

        foreach (var container in components)
        {
            if (container?["components"] is not JsonArray children)
                continue;

            foreach (var child in children.ToArray())
                if (child?["type"]?.GetValue<int>() == 1)
                    children.Remove(child);
        }

        components.Insert(0, JsonSerializer.SerializeToNode(Panel.Text($"От: <@{actor}>")));
        payload["flags"] = 32768 | 4096;
        payload["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() };

        return payload;
    }
}
