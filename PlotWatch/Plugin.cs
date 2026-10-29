using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System.Reflection;
using PlotWatch.Commands;
using PlotWatch.Data;
using PlotWatch.Diagnostics;
using PlotWatch.Windows;

namespace PlotWatch;

public sealed class Plugin : IDalamudPlugin
{
    private const string MainCommand = "/plotwatch";
    private const string AliasCommand = "/pw";

    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal static ICommandManager Commands { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    [PluginService]
    internal static IFramework Framework { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("PlotWatch");
    private readonly Configuration configuration;
    private readonly MainWindow mainWindow;
    private readonly DebugWindow debugWindow;
    private readonly PluginDiagnostics diagnostics;
    private readonly ConfigurationResetConfirmation resetConfirmation;
    private readonly OwnedCommandRegistration mainCommandRegistration;
    private readonly OwnedCommandRegistration aliasCommandRegistration;
    private readonly Queue<DiagnosticAction> diagnosticQueue = new();
    private bool configurationSaveBlocked;

    public string Name => "PlotWatch";

    public Plugin()
    {
        configuration = LoadConfiguration(out configurationSaveBlocked);
        diagnostics = new PluginDiagnostics(
            configurationSaveBlocked ? "Load failed; saving disabled until reset." : "Loaded",
            RefreshDependencyStatus,
            typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "Unknown",
            typeof(Plugin).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "PlotWatchBuildId")?.Value ?? "Unknown");
        if (configurationSaveBlocked)
            diagnostics.SetLastError("Configuration could not be loaded safely; saving is blocked until explicit reset.");
        resetConfirmation = new ConfigurationResetConfirmation(ResetConfigurationAndSave);
        mainCommandRegistration = CreateCommandRegistration(MainCommand, "Open PlotWatch housing and lottery tracker.", true);
        aliasCommandRegistration = CreateCommandRegistration(AliasCommand, "Alias for /plotwatch.", false);
        mainWindow = new MainWindow(configuration, diagnostics, SaveConfiguration, resetConfirmation, OpenDebugWindow);
        debugWindow = new DebugWindow(configuration, diagnostics, QueueDiagnostic);
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(debugWindow);
        diagnostics.MainWindowStatus = "Registered";
        diagnostics.DebugWindowStatus = "Registered";

        if (!mainCommandRegistration.Register())
            Log.Error("Could not register /plotwatch; another plugin may own the command.");

        if (!aliasCommandRegistration.Register())
            Log.Warning("Could not register /pw because command is already in use. PlotWatch will leave it untouched.");
        RefreshCommandStatus();

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        PluginInterface.ActivePluginsChanged += OnActivePluginsChanged;
        Framework.Update += OnFrameworkUpdate;
        diagnostics.DrawCallbackStatus = "Registered";
        diagnostics.MainUiCallbackStatus = "Registered";
        diagnostics.ConfigUiCallbackStatus = "Registered";
        RefreshDependencyStatus();
        Log.Debug("PlotWatch loaded. Version {Version:l}; build ID {BuildId:l}; Dalamud load timestamp UTC {LoadTimestamp:O}.",
            diagnostics.Version,
            diagnostics.BuildId,
            PluginInterface.LoadTimeUTC);
    }

    public void Dispose()
    {
        PluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;

        mainCommandRegistration.Dispose();
        aliasCommandRegistration.Dispose();

        windowSystem.RemoveAllWindows();
    }

    private static Configuration LoadConfiguration(out bool failed)
    {
        failed = false;
        try
        {
            if (PluginInterface.GetPluginConfig() is not { } stored)
                return new Configuration();

            if (stored is Configuration configuration)
            {
                if (configuration.Version > Configuration.CurrentVersion)
                {
                    failed = true;
                    Log.Warning("Configuration was written by a newer PlotWatch version; preserving it and disabling saves until explicitly reset.");
                    return new Configuration();
                }

                configuration.Normalize();
                return configuration;
            }

            failed = true;
            Log.Error("Stored PlotWatch configuration has an unexpected type. It will not be overwritten unless reset is explicitly requested.");
        }
        catch (Exception ex)
        {
            failed = true;
            Log.Error(ex, "Failed to load PlotWatch configuration. Saving is disabled until configuration is explicitly reset.");
        }

        return new Configuration();
    }

    private void SaveConfiguration()
    {
        if (configurationSaveBlocked)
            return;

        configuration.Version = Configuration.CurrentVersion;
        try
        {
            PluginInterface.SavePluginConfig(configuration);
            diagnostics.SetConfigurationStatus("Loaded and saved");
        }
        catch (Exception ex)
        {
            diagnostics.SetConfigurationStatus("Save failed");
            diagnostics.SetLastError(ex.Message);
            Log.Error(ex, "Failed to save PlotWatch configuration.");
        }
    }

    private string? ResetConfigurationAndSave()
    {
        configuration.ResetToDefaults();
        configurationSaveBlocked = false;
        diagnostics.SetConfigurationStatus("Reset to defaults");
        try
        {
            PluginInterface.SavePluginConfig(configuration);
            Log.Warning("PlotWatch configuration reset to defaults by user request.");
            return null;
        }
        catch (Exception ex)
        {
            diagnostics.SetConfigurationStatus("Reset; save failed");
            diagnostics.SetLastError(ex.Message);
            Log.Error(ex, "Failed to save reset PlotWatch configuration.");
            return $"Could not save reset configuration: {ex.Message}";
        }
    }

    private void OnCommand(string command, string arguments)
    {
        mainWindow.IsOpen = true;
        mainWindow.SelectDefaultTab();
    }

    private void OpenMainUi()
    {
        mainWindow.SelectDefaultTab();
        mainWindow.IsOpen = true;
    }

    private void OpenConfigUi()
    {
        mainWindow.SelectSettingsTab();
        mainWindow.IsOpen = true;
    }

    private void OpenDebugWindow()
    {
        debugWindow.IsOpen = true;
        debugWindow.BringToFront();
    }

    private void QueueDiagnostic(DiagnosticAction action)
    {
        diagnosticQueue.Enqueue(action);
        diagnostics.IsRequestPending = true;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (diagnosticQueue.Count == 0)
        {
            diagnostics.IsRequestPending = false;
            return;
        }

        var action = diagnosticQueue.Dequeue();
        diagnostics.IsRequestPending = diagnosticQueue.Count > 0;
        RunDiagnostic(action);
    }

    private void RunDiagnostic(DiagnosticAction action)
    {
        var actionName = action switch
        {
            DiagnosticAction.RunAll => "All read-only checks",
            DiagnosticAction.InspectConfiguration => "Configuration inspection",
            DiagnosticAction.InspectCommandsAndUi => "Commands and UI check",
            DiagnosticAction.InspectDependencies => "Dependency check",
            DiagnosticAction.ClearHistory => "Clear displayed history",
            _ => "Unknown diagnostic action",
        };

        Log.Debug("{Action:l} started.", actionName);
        try
        {
            if (action == DiagnosticAction.ClearHistory)
                diagnostics.ClearHistory();

            diagnostics.BeginRun(actionName);
            var succeeded = true;
            switch (action)
            {
                case DiagnosticAction.RunAll:
                    InspectConfiguration();
                    InspectCommandsAndUi();
                    succeeded = InspectDependencies();
                    break;
                case DiagnosticAction.InspectConfiguration:
                    InspectConfiguration();
                    break;
                case DiagnosticAction.InspectCommandsAndUi:
                    InspectCommandsAndUi();
                    break;
                case DiagnosticAction.InspectDependencies:
                    succeeded = InspectDependencies();
                    break;
                case DiagnosticAction.ClearHistory:
                    RecordDiagnostic(DiagnosticSeverity.Debug, "Displayed diagnostic history cleared.");
                    break;
            }

            if (succeeded)
            {
                RecordDiagnostic(DiagnosticSeverity.Debug, $"{actionName} completed.");
                diagnostics.CompleteRun("Completed");
            }
            else
            {
                RecordDiagnostic(DiagnosticSeverity.Warning, $"{actionName} failed: one or more checks were unavailable.");
                diagnostics.CompleteRun("Warning");
            }
        }
        catch (Exception ex)
        {
            diagnostics.SetLastError(ex.Message);
            RecordDiagnostic(DiagnosticSeverity.Error, $"{actionName} failed: {ex.Message}");
            diagnostics.CompleteRun("Failed");
            Log.Error(ex, "{Action} failed.", actionName);
        }
    }

    private void InspectConfiguration()
    {
        var summary = $"Schema {configuration.Version}/{Configuration.CurrentVersion}; state: {diagnostics.ConfigurationStatus}; default tab: {(PlotWatchTab)configuration.DefaultTab}; text scale: {configuration.TextScalePercent:F0}%; notifications chat={configuration.ChatNotificationsEnabled}, toast={configuration.ToastNotificationsEnabled}, sound={configuration.SoundNotificationsEnabled}.";
        RecordDiagnostic(DiagnosticSeverity.Debug, summary);
    }

    private void InspectCommandsAndUi()
    {
        RefreshCommandStatus();
        RecordDiagnostic(DiagnosticSeverity.Debug,
            $"Commands/UI: /plotwatch={diagnostics.MainCommandStatus}; /pw={diagnostics.AliasCommandStatus}; Draw={diagnostics.DrawCallbackStatus}; OpenMainUi={diagnostics.MainUiCallbackStatus}; OpenConfigUi={diagnostics.ConfigUiCallbackStatus}; main window={diagnostics.MainWindowStatus}; debug window={diagnostics.DebugWindowStatus}. Command status checks the live handler delegate; callback/window statuses are PlotWatch's tracked registrations.");
    }

    private bool InspectDependencies()
    {
        if (!RefreshDependencyStatus())
        {
            RecordDiagnostic(DiagnosticSeverity.Warning, $"Dependency inspection failed: {diagnostics.LastError}");
            return false;
        }

        RecordDiagnostic(DiagnosticSeverity.Debug,
            $"Dependencies: PaissaHouse={diagnostics.PaissaHouseStatus}; Lifestream={diagnostics.LifestreamStatus}; navigation={diagnostics.NavigationStatus}. Detection means installed/loaded metadata only; compatibility is unvalidated.");
        return true;
    }

    private void RecordDiagnostic(DiagnosticSeverity severity, string message)
    {
        diagnostics.Record(severity, message);
        switch (severity)
        {
            case DiagnosticSeverity.Warning:
                Log.Warning("{DiagnosticResult}", message);
                break;
            case DiagnosticSeverity.Error:
                Log.Error("{DiagnosticResult}", message);
                break;
            default:
                Log.Debug("{DiagnosticResult:l}", message);
                break;
        }
    }

    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs args) => RefreshDependencyStatus();

    private OwnedCommandRegistration CreateCommandRegistration(string command, string help, bool showInHelp)
    {
        CommandInfo? ownInfo = null;
        return new OwnedCommandRegistration(
            command,
            () =>
            {
                ownInfo = new CommandInfo(OnCommand)
                {
                    HelpMessage = help,
                    ShowInHelp = showInHelp,
                };
                return Commands.AddHandler(command, ownInfo);
            },
            () => ownInfo is not null && Commands.Commands.TryGetValue(command, out var current) && current.Handler == ownInfo.Handler,
            () => Commands.Commands.ContainsKey(command),
            () => Commands.RemoveHandler(command));
    }

    private void RefreshCommandStatus()
    {
        diagnostics.MainCommandStatus = mainCommandRegistration.GetStatus();
        diagnostics.AliasCommandStatus = aliasCommandRegistration.GetStatus();
    }

    private bool RefreshDependencyStatus()
    {
        try
        {
            var loadedNames = PluginInterface.InstalledPlugins
                .Where(plugin => plugin.IsLoaded)
                .Select(plugin => plugin.InternalName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            diagnostics.SetDependencies(
                FindDependency(loadedNames, "AutoSweep", "PaissaHouse"),
                FindDependency(loadedNames, "Lifestream"),
                FindDependency(loadedNames, "vnavmesh"));
            return true;
        }
        catch (Exception ex)
        {
            diagnostics.SetDependencies("Unknown", "Unknown", "Unknown");
            diagnostics.SetLastError($"Could not inspect installed plugins: {ex.Message}");
            Log.Warning(ex, "Could not inspect installed plugin metadata.");
            return false;
        }
    }

    private static string FindDependency(HashSet<string> loadedNames, params string[] candidates)
    {
        if (candidates.Any(loadedNames.Contains))
            return "Detected (compatibility not validated)";

        return "Not detected";
    }
}
