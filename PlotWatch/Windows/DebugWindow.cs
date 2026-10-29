using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using PlotWatch.Data;
using PlotWatch.Diagnostics;

namespace PlotWatch.Windows;

public sealed class DebugWindow : Window
{
    private static readonly (DiagnosticSection Section, string Label, string Description)[] Sections =
    [
        (DiagnosticSection.Overview, "Overview", "Overall plugin and subsystem state."),
        (DiagnosticSection.Configuration, "Configuration", "Read-only inspection of current settings and persistence state."),
        (DiagnosticSection.CommandsAndUi, "Commands & UI", "Command ownership, callback registration, and window lifecycle."),
        (DiagnosticSection.Dependencies, "Dependencies", "Installed/loaded metadata for optional companion plugins."),
        (DiagnosticSection.LogsAndErrors, "Logs & Errors", "Recent diagnostic results retained for this session."),
    ];

    private readonly Configuration configuration;
    private readonly PluginDiagnostics diagnostics;
    private readonly Action<DiagnosticAction> enqueueAction;
    private DiagnosticSection selectedSection = DiagnosticSection.Overview;

    public DebugWindow(Configuration configuration, PluginDiagnostics diagnostics, Action<DiagnosticAction> enqueueAction)
        : base("PlotWatch Diagnostics", ImGuiWindowFlags.NoCollapse)
    {
        this.configuration = configuration;
        this.diagnostics = diagnostics;
        this.enqueueAction = enqueueAction;
        Size = new Vector2(900, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        IsOpen = false;
    }

    public override void Draw()
    {
        ImGui.SetWindowFontScale(configuration.TextScalePercent / 100f);
        try
        {
            var available = ImGui.GetContentRegionAvail();
            var navWidth = Math.Clamp(available.X * 0.25f, 165f, 225f);

            if (ImGui.BeginChild("##plotwatch-debug-navigation", new Vector2(navWidth, 0), true))
            {
                ImGui.TextUnformatted("Diagnostics");
                ImGui.Separator();
                foreach (var item in Sections)
                {
                    if (ImGui.Selectable(item.Label, selectedSection == item.Section, ImGuiSelectableFlags.None, new Vector2(0, 28f)))
                        selectedSection = item.Section;
                }
            }
            ImGui.EndChild();

            ImGui.SameLine();
            if (ImGui.BeginChild("##plotwatch-debug-content", new Vector2(0, 0), true))
                DrawSelectedSection();
            ImGui.EndChild();
        }
        finally
        {
            ImGui.SetWindowFontScale(1f);
        }
    }

    public override void PreDraw()
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 12f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f * scale);
    }

    public override void PostDraw() => ImGui.PopStyleVar(3);

    private void DrawSelectedSection()
    {
        var section = Sections.First(item => item.Section == selectedSection);
        ImGui.TextUnformatted(section.Label);
        ImGui.TextWrapped(section.Description);
        ImGui.Separator();

        switch (selectedSection)
        {
            case DiagnosticSection.Overview:
                DrawOverview();
                break;
            case DiagnosticSection.Configuration:
                DrawConfiguration();
                break;
            case DiagnosticSection.CommandsAndUi:
                DrawCommandsAndUi();
                break;
            case DiagnosticSection.Dependencies:
                DrawDependencies();
                break;
            case DiagnosticSection.LogsAndErrors:
                DrawHistory();
                break;
        }

        ImGui.Spacing();
        DrawLatestResult();
    }

    private void DrawOverview()
    {
        ImGui.TextUnformatted($"Version: {diagnostics.Version}");
        ImGui.TextUnformatted("Milestone: Phase 1 — Foundation and working UI");
        ImGui.TextUnformatted($"Configuration: {diagnostics.ConfigurationStatus}");
        ImGui.TextUnformatted($"Last check: {diagnostics.LastCheckLocal?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Not run"}");
        ImGui.TextWrapped($"Latest error: {diagnostics.LastError}");
        ImGui.Separator();
        if (DrawActionButton("Run all checks"))
            enqueueAction(DiagnosticAction.RunAll);
    }

    private void DrawConfiguration()
    {
        ImGui.TextUnformatted($"Schema: {Configuration.CurrentVersion} (current)");
        ImGui.TextUnformatted($"Load/save state: {diagnostics.ConfigurationStatus}");
        ImGui.TextUnformatted($"Default tab: {(PlotWatchTab)configuration.DefaultTab}");
        ImGui.TextUnformatted($"Text scale: {configuration.TextScalePercent:F0}%");
        ImGui.TextUnformatted($"Chat notifications: {OnOff(configuration.ChatNotificationsEnabled)}");
        ImGui.TextUnformatted($"Toast notifications: {OnOff(configuration.ToastNotificationsEnabled)}");
        ImGui.TextUnformatted($"Sound notifications: {OnOff(configuration.SoundNotificationsEnabled)}");
        ImGui.Separator();
        if (DrawActionButton("Inspect configuration"))
            enqueueAction(DiagnosticAction.InspectConfiguration);
    }

    private void DrawCommandsAndUi()
    {
        ImGui.TextUnformatted($"/plotwatch: {diagnostics.MainCommandStatus}");
        ImGui.TextUnformatted($"/pw: {diagnostics.AliasCommandStatus}");
        ImGui.TextUnformatted($"Draw callback: {diagnostics.DrawCallbackStatus}");
        ImGui.TextUnformatted($"Main UI callback: {diagnostics.MainUiCallbackStatus}");
        ImGui.TextUnformatted($"Config UI callback: {diagnostics.ConfigUiCallbackStatus}");
        ImGui.TextUnformatted($"Main window: {diagnostics.MainWindowStatus}");
        ImGui.TextUnformatted($"Debug window: {diagnostics.DebugWindowStatus}");
        ImGui.TextWrapped("These statuses report PlotWatch's tracked registrations; they do not prove a command dispatched or a callback fired.");
        ImGui.Separator();
        if (DrawActionButton("Check commands and UI"))
            enqueueAction(DiagnosticAction.InspectCommandsAndUi);
    }

    private void DrawDependencies()
    {
        ImGui.TextUnformatted($"PaissaHouse: {diagnostics.PaissaHouseStatus}");
        ImGui.TextUnformatted($"Lifestream: {diagnostics.LifestreamStatus}");
        ImGui.TextUnformatted($"Navigation: {diagnostics.NavigationStatus}");
        ImGui.TextWrapped("Detection reports installed and loaded metadata only. Integration compatibility is not validated.");
        ImGui.Separator();
        if (DrawActionButton("Check dependencies"))
            enqueueAction(DiagnosticAction.InspectDependencies);
    }

    private void DrawHistory()
    {
        ImGui.TextWrapped($"Latest error: {diagnostics.LastError}");
        if (DrawActionButton("Clear displayed history"))
            enqueueAction(DiagnosticAction.ClearHistory);

        ImGui.Separator();
        if (diagnostics.Runs.Count == 0)
        {
            ImGui.TextDisabled("No diagnostic actions run this session.");
            return;
        }

        foreach (var run in diagnostics.Runs.Reverse())
        {
            ImGui.TextUnformatted($"{run.Action} — {run.Status} — {run.StartedAtLocal:yyyy-MM-dd HH:mm:ss}");
            foreach (var finding in run.Findings)
                ImGui.BulletText($"[{finding.Severity}] {finding.Message}");
            ImGui.Separator();
        }
    }

    private void DrawLatestResult()
    {
        if (diagnostics.Runs.Count == 0 || selectedSection == DiagnosticSection.LogsAndErrors)
            return;

        ImGui.Separator();
        var run = diagnostics.Runs[^1];
        ImGui.TextUnformatted($"Latest result: {run.Action} — {run.Status}");
        ImGui.TextUnformatted($"Started {run.StartedAtLocal:yyyy-MM-dd HH:mm:ss}");
        foreach (var finding in run.Findings)
            ImGui.BulletText($"[{finding.Severity}] {finding.Message}");
    }

    private bool DrawActionButton(string label)
    {
        using var disabled = ImRaii.Disabled(diagnostics.IsRequestPending);
        if (ImGui.Button(diagnostics.IsRequestPending ? "Check pending..." : label))
            return true;

        return false;
    }

    private static string OnOff(bool value) => value ? "On" : "Off";
}
