#nullable enable
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ServerAdminIntegration.Shared;

namespace ServerAdminIntegration.Components;

public sealed class RecentPlayersStore
{
    private static readonly SemaphoreSlim Writer = new SemaphoreSlim(1, 1);
    private readonly ConcurrentDictionary<string, PlayerInfo> _players =
        new ConcurrentDictionary<string, PlayerInfo>(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(PlayerInfo Player, bool OnlyIfMissing)> _pending =
        new ConcurrentQueue<(PlayerInfo, bool)>();
    private readonly SemaphoreSlim _signal = new SemaphoreSlim(0, 1);
    private readonly TaskCompletionSource<bool> _ready =
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _path;
    private readonly Action<string> _log;
    private int _signaled;
    private int _stopped;

    public RecentPlayersStore(string path, Action<string> log)
    {
        _path = Path.GetFullPath(path);
        _log = log;
    }

    public static bool IsSteamId(string id) =>
        id != null
        && id.Length == 23
        && id.EndsWith("@steam", StringComparison.Ordinal)
        && ulong.TryParse(id.Substring(0, 17), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value > 0;

    public void Record(string id, string nickname, string ip, long seen, bool onlyIfMissing = false)
    {
        if (Volatile.Read(ref _stopped) != 0 || !IsSteamId(id) || seen <= 0 || seen > DateTime.MaxValue.Ticks)
            return;

        _pending.Enqueue((Create(id, nickname, ip, seen), onlyIfMissing));
        Signal();
    }

    private static PlayerInfo Create(string id, string nickname, string ip, long seen)
    {
        var name = new string(
            (nickname ?? "")
                .Where(c =>
                    !char.IsControl(c)
                    && c != '\u2028'
                    && c != '\u2029'
                    && char.GetUnicodeCategory(c) != UnicodeCategory.Format
                )
                .ToArray()
        );

        if (name.Length > 100)
            name = name.Substring(0, char.IsHighSurrogate(name[99]) ? 99 : 100);

        return new PlayerInfo
        {
            UserId = id,
            Nickname = name,
            IpAddress = IPAddress.TryParse(ip, out var address) ? address.ToString() : "",
            LastSeenUtcTicks = seen,
        };
    }

    private void Signal()
    {
        if (Interlocked.Exchange(ref _signaled, 1) == 0)
            _signal.Release();
    }

    public void Stop() => Interlocked.Exchange(ref _stopped, 1);

    public async Task WaitReadyAsync(CancellationToken token)
    {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using (token.Register(() => cancelled.TrySetCanceled()))
        {
            var completed = await Task.WhenAny(_ready.Task, cancelled.Task).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }

        token.ThrowIfCancellationRequested();
    }

    public PlayerInfo[] Latest() =>
        _players
            .Values.OrderByDescending(p => p.LastSeenUtcTicks)
            .ThenBy(p => p.UserId, StringComparer.Ordinal)
            .Take(75)
            .Select(p => new PlayerInfo
            {
                Nickname = p.Nickname,
                UserId = p.UserId,
                LastSeenUtcTicks = p.LastSeenUtcTicks,
            })
            .ToArray();

    public PlayerInfo? Find(string id) =>
        _players.TryGetValue(id, out var player)
            ? new PlayerInfo
            {
                Nickname = player.Nickname,
                UserId = player.UserId,
                IpAddress = player.IpAddress,
                LastSeenUtcTicks = player.LastSeenUtcTicks,
            }
            : null;

    public async Task RunAsync(CancellationToken stop)
    {
        var loaded = false;
        var temporary = _path + ".tmp";
        var acquired = false;

        try
        {
            await Writer.WaitAsync(stop).ConfigureAwait(false);
            acquired = true;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            var source = File.Exists(_path) ? _path : temporary;

            if (File.Exists(source))
            {
                using (var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true))
                using (var bytes = new MemoryStream())
                {
                    await file.CopyToAsync(bytes, 8192, stop).ConfigureAwait(false);

                    foreach (var player in ApiJson.ReadHistory(bytes.ToArray()))
                    {
                        if (!IsSteamId(player.UserId)
                            || player.LastSeenUtcTicks <= 0
                            || player.LastSeenUtcTicks > DateTime.MaxValue.Ticks)
                            throw new InvalidDataException("Invalid player history entry.");

                        var entry = Create(player.UserId, player.Nickname, player.IpAddress, player.LastSeenUtcTicks);
                        _players.AddOrUpdate(
                            entry.UserId,
                            entry,
                            (_, previous) => entry.LastSeenUtcTicks >= previous.LastSeenUtcTicks ? entry : previous
                        );
                    }
                }

                if (source == temporary)
                    File.Move(temporary, _path);
            }

            Drain();
            loaded = true;

            if (File.Exists(temporary))
                File.Delete(temporary);

            _ready.TrySetResult(true);

            while (true)
            {
                await _signal.WaitAsync(stop).ConfigureAwait(false);
                await Task.Delay(200, stop).ConfigureAwait(false);
                Interlocked.Exchange(ref _signaled, 0);
                Drain();

                try
                {
                    await SaveAsync(temporary).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
                {
                    _log("History save failed: " + exception.Message);
                    await Task.Delay(1000, stop).ConfigureAwait(false);
                    Signal();
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);

            throw;
        }
        finally
        {
            Stop();
            _ready.TrySetCanceled();

            try
            {
                if (loaded)
                {
                    Drain();
                    await SaveAsync(temporary).ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    if (acquired && loaded && File.Exists(temporary))
                        File.Delete(temporary);
                }
                finally
                {
                    if (acquired)
                        Writer.Release();
                }
            }
        }
    }

    private void Drain()
    {
        while (_pending.TryDequeue(out var update))
        {
            var player = update.Player;

            if (update.OnlyIfMissing)
                _players.TryAdd(player.UserId, player);
            else
                _players.AddOrUpdate(
                    player.UserId,
                    player,
                    (_, previous) => player.LastSeenUtcTicks >= previous.LastSeenUtcTicks ? player : previous
                );
        }
    }

    private async Task SaveAsync(string temporary)
    {
        var bytes = ApiJson.WriteHistory(_players.Values);

        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                await file.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                await file.FlushAsync().ConfigureAwait(false);
            }

            if (File.Exists(_path))
                File.Replace(temporary, _path, null);
            else
                File.Move(temporary, _path);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
