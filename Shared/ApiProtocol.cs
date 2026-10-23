namespace ServerAdminIntegration.Shared;

public static class ApiProtocol
{
    public const int TimeoutSeconds = 10;
    public const int MaximumResponseBytes = 64 * 1024;
    public const string CommandPath = "/api/commands/";
    public const string KeyHeader = "X-Api-Key";
}
