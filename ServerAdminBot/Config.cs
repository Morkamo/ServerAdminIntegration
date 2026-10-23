using System.Net;
using System.Text.Json;
using ServerAdminBot.Database;

namespace ServerAdminBot;

public sealed class Config
{
    public string Token { get; set; } = "";
    public ulong GuildId { get; set; }
    public DatabaseConfig Database { get; set; } = new();
    public Dictionary<string, ServerConfig> Servers { get; set; } = new() { ["Classic"] = new() };

    public static async Task<Config> LoadAsync(string path, bool requireDiscord, CancellationToken token)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true,
        };
        if (!File.Exists(path))
        {
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Options = FileOptions.Asynchronous,
            };
            if (!OperatingSystem.IsWindows())
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(path, fileOptions))
                await JsonSerializer.SerializeAsync(stream, new Config(), options, token);
            throw new InvalidOperationException($"Создан конфиг {path}. Заполните настройки бота, БД и ключ API.");
        }

        var json = await File.ReadAllTextAsync(path, token);
        using (var document = JsonDocument.Parse(json))
            ValidateUniqueProperties(document.RootElement);
        var config = JsonSerializer.Deserialize<Config>(json, options)
            ?? throw new InvalidOperationException("Конфиг пуст.");
        config.Token = Environment.GetEnvironmentVariable("SERVERADMIN_DISCORD_TOKEN") ?? config.Token;
        if (config.Database == null)
            throw new InvalidOperationException("Укажите database.");
        config.Database.Password = Environment.GetEnvironmentVariable("SERVERADMIN_DB_PASSWORD") ?? config.Database.Password;
        if (requireDiscord && (string.IsNullOrWhiteSpace(config.Token) || config.GuildId == 0))
            throw new InvalidOperationException("Укажите token и guild_id.");
        if (config.Servers == null || config.Servers.Count == 0)
            throw new InvalidOperationException("Добавьте хотя бы один сервер.");
        var servers = new Dictionary<string, ServerConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, server) in config.Servers)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name != name.Trim() || name.Any(char.IsControl))
                throw new InvalidOperationException("Название сервера должно содержать от 1 до 100 символов без краевых пробелов.");
            if (server == null || !servers.TryAdd(name, server))
                throw new InvalidOperationException("Серверы должны иметь уникальные названия и настройки.");
            if (requireDiscord)
                server.Validate();
        }
        config.Servers = servers;
        return config;
    }

    private static void ValidateUniqueProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
                throw new InvalidOperationException("Повторяющаяся настройка: " + property.Name);
            ValidateUniqueProperties(property.Value);
        }
    }
}

public sealed class ServerConfig
{
    public string Ip { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7777;
    public string ApiKey { get; set; } = "";

    public void Validate()
    {
        if (!IPAddress.TryParse(Ip, out var address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            throw new InvalidOperationException("ip сервера должен быть конкретным IP-адресом.");
        if (Port < 1 || Port > 65535)
            throw new InvalidOperationException("port сервера должен быть от 1 до 65535.");
        if (ApiKey == null || ApiKey.Length < 32 || ApiKey.Length > 256 || ApiKey.Any(x => x < 33 || x > 126))
            throw new InvalidOperationException("api_key должен содержать от 32 до 256 печатных ASCII-символов без пробелов.");
    }

    public Uri Address => new UriBuilder("http", Ip, Port).Uri;
}
