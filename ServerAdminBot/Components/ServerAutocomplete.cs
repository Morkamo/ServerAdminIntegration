using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;

namespace ServerAdminBot.Components;

public sealed class ServerAutocomplete : IAutocompleteProvider
{
    public Task<IEnumerable<DiscordAutoCompleteChoice>> Provider(AutocompleteContext context)
    {
        var search = context.OptionValue?.ToString() ?? "";
        IEnumerable<DiscordAutoCompleteChoice> choices = Program.Config.Servers.Keys
            .Where(x => x.Contains(search, StringComparison.OrdinalIgnoreCase)).Take(25)
            .Select(x => new DiscordAutoCompleteChoice(x, x));
        return Task.FromResult(choices);
    }
}
