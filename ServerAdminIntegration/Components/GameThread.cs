using System;
using System.Threading;
using System.Threading.Tasks;

namespace ServerAdminIntegration.Components;

public static class GameThread
{
    public static async Task<T> RunAsync<T>(Action<Action> dispatch, Func<T> action, CancellationToken token)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = 0;
        using (token.Register(() =>
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
                completion.TrySetCanceled();
        }))
        {
            if (!token.IsCancellationRequested)
                dispatch(() =>
                {
                    if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
                        return;
                    try { completion.TrySetResult(action()); }
                    catch (Exception exception) { completion.TrySetException(exception); }
                });
            return await completion.Task.ConfigureAwait(false);
        }
    }
}
