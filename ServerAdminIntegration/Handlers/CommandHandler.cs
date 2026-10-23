using System;
using System.Threading;
using System.Threading.Tasks;

namespace ServerAdminIntegration.Handlers;

public sealed class CommandHandler
{
    public Task<string> ExecuteAsync(string command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (command)
        {
            case "ping":
                return Task.FromResult("pong");
            default:
                throw new ArgumentException("Unknown command.", nameof(command));
        }
    }
}
