using System.Net;
using Npgsql;

namespace ServerAdminBot.Database;

public sealed class DatabaseConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "server_admin";
    public string Username { get; set; } = "server_admin_bot";
    public string Password { get; set; } = "";
    public int MaximumPoolSize { get; set; } = 10;

    public NpgsqlDataSource CreateDataSource()
    {
        if (string.IsNullOrWhiteSpace(Host)
            || !(
                Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || (IPAddress.TryParse(Host, out var address) && IPAddress.IsLoopback(address))
                || Host.StartsWith('/')
            ))
            throw new InvalidOperationException(
                "PostgreSQL должен быть локальным: loopback, localhost или Unix socket."
            );

        if (Port < 1 || Port > 65535 || MaximumPoolSize < 1 || MaximumPoolSize > 100)
            throw new InvalidOperationException("Некорректный порт или размер пула PostgreSQL.");

        if (string.IsNullOrWhiteSpace(Database)
            || string.IsNullOrWhiteSpace(Username)
            || string.IsNullOrWhiteSpace(Password))
            throw new InvalidOperationException("Укажите имя БД, пользователя и пароль PostgreSQL.");

        return NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder
            {
                Host = Host,
                Port = Port,
                Database = Database,
                Username = Username,
                Password = Password,
                MaxPoolSize = MaximumPoolSize,
                Timeout = 5,
                CommandTimeout = 10,
                ApplicationName = "ServerAdminBot",
                IncludeErrorDetail = false,
                PersistSecurityInfo = false,
            }.ConnectionString
        );
    }
}
