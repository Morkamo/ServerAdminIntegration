using System;
using System.Net;

namespace ServerAdminIntegration.Configs;

public sealed class ApiConfig
{
    public bool IsEnabled { get; set; } = true;
    public string Ip { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7777;
    public string ApiKey { get; set; } = "";
    public int MaximumConcurrentRequests { get; set; } = 32;

    public void Validate()
    {
        if (!IPAddress.TryParse(Ip, out _))
            throw new ArgumentException("API ip must be an IP address.");

        if (Port < 1 || Port > 65535)
            throw new ArgumentException("API port must be between 1 and 65535.");

        if (string.IsNullOrWhiteSpace(ApiKey) || ApiKey.Length < 32 || ApiKey.Length > 256)
            throw new ArgumentException("API key must contain 32 to 256 characters.");

        foreach (var character in ApiKey)
            if (character < 33 || character > 126)
                throw new ArgumentException("API key must contain printable ASCII without spaces.");

        if (MaximumConcurrentRequests < 1 || MaximumConcurrentRequests > 1024)
            throw new ArgumentException("MaximumConcurrentRequests must be between 1 and 1024.");
    }
}
