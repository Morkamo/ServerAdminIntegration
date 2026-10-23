using Npgsql;
using NpgsqlTypes;

namespace ServerAdminBot.Database;

public sealed record PermissionSettings(bool Everyone, HashSet<ulong> Roles)
{
    public bool Allows(bool administrator, IEnumerable<ulong> roles) => administrator || Everyone || roles.Any(Roles.Contains);
}

public sealed class CommandPermissions(NpgsqlDataSource source)
{
    public async Task InitializeAsync(CancellationToken token)
    {
        await using var stream = typeof(CommandPermissions).Assembly.GetManifestResourceStream("ServerAdminBot.Database.Schema.sql")!;
        using var reader = new StreamReader(stream);
        await using var query = source.CreateCommand(await reader.ReadToEndAsync(token));
        await query.ExecuteNonQueryAsync(token);
    }

    public async Task<PermissionSettings> GetAsync(ulong guild, string command, CancellationToken token = default)
    {
        await using var query = source.CreateCommand("SELECT everyone, role_ids FROM bot.command_permissions WHERE guild_id = $1 AND command = $2");
        query.Parameters.AddWithValue(checked((long)guild));
        query.Parameters.AddWithValue(command);
        await using var reader = await query.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)
            ? new PermissionSettings(reader.GetBoolean(0), reader.GetFieldValue<long[]>(1).Select(x => checked((ulong)x)).ToHashSet())
            : new PermissionSettings(true, []);
    }

    private async Task EnsureAsync(ulong guild, string command, CancellationToken token)
    {
        await using var query = source.CreateCommand("INSERT INTO bot.command_permissions(guild_id, command) VALUES ($1, $2) ON CONFLICT DO NOTHING");
        query.Parameters.AddWithValue(checked((long)guild));
        query.Parameters.AddWithValue(command);
        await query.ExecuteNonQueryAsync(token);
    }

    public async Task ToggleEveryoneAsync(ulong guild, string command, CancellationToken token = default)
    {
        await EnsureAsync(guild, command, token);
        await using var query = source.CreateCommand("UPDATE bot.command_permissions SET everyone = NOT everyone WHERE guild_id = $1 AND command = $2");
        query.Parameters.AddWithValue(checked((long)guild));
        query.Parameters.AddWithValue(command);
        await query.ExecuteNonQueryAsync(token);
    }

    public async Task UpdateRolesAsync(ulong guild, string command, IEnumerable<ulong> pageRoles,
        IEnumerable<ulong> selectedRoles, bool? everyone = null, CancellationToken token = default)
    {
        await EnsureAsync(guild, command, token);
        await using var query = source.CreateCommand("""
            UPDATE bot.command_permissions SET role_ids = ARRAY(
                SELECT DISTINCT r FROM unnest(role_ids || $4::bigint[]) r
                WHERE NOT (r = ANY($3::bigint[])) OR r = ANY($4::bigint[])),
                everyone = CASE WHEN $5 THEN $6 ELSE everyone END
            WHERE guild_id = $1 AND command = $2
            """);
        query.Parameters.AddWithValue(checked((long)guild));
        query.Parameters.AddWithValue(command);
        query.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bigint, pageRoles.Select(x => checked((long)x)).ToArray());
        query.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bigint, selectedRoles.Select(x => checked((long)x)).ToArray());
        query.Parameters.AddWithValue(everyone.HasValue);
        query.Parameters.AddWithValue(everyone.GetValueOrDefault());
        await query.ExecuteNonQueryAsync(token);
    }
}
