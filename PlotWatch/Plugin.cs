using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace PlotWatch;

public sealed class Plugin : IDalamudPlugin
{
    private const string MainCommand = "/plotwatch";
    private const string AliasCommand = "/pw";

    [PluginService]
    internal static ICommandManager Commands { get; private set; } = null!;

    [PluginService]
    internal static IChatGui Chat { get; private set; } = null!;

    public string Name => "PlotWatch";

    public Plugin()
    {
        Commands.AddHandler(MainCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open PlotWatch housing and lottery tracker.",
            ShowInHelp = true,
        });

        Commands.AddHandler(AliasCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "Alias for /plotwatch.",
            ShowInHelp = false,
        });
    }

    public void Dispose()
    {
        Commands.RemoveHandler(MainCommand);
        Commands.RemoveHandler(AliasCommand);
    }

    private static void OnCommand(string command, string arguments)
    {
        Chat.Print("PlotWatch UI is not implemented yet.");
    }
}
