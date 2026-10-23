using DSharpPlus.SlashCommands;
using ServerAdminBot.Components;

namespace ServerAdminBot.Commands;

public sealed class PermissionsControlCommand : ApplicationCommandModule
{
    [SlashCommand("permissionscontrol", "Настроить доступ ролей к командам")]
    public Task PermissionsControl(InteractionContext context) =>
        Ui.RunAsync(
            context,
            () => PermissionsUi.CreateAsync(context.Interaction, context.Member),
            administratorOnly: true
        );
}
