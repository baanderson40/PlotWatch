using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using PlotWatch.Data;
using PlotWatch.Diagnostics;

namespace PlotWatch.Windows;

public sealed class MainWindow : Window
{
    private readonly Configuration configuration;
    private readonly PluginDiagnostics diagnostics;
    private readonly Action saveConfiguration;
    private readonly ConfigurationResetConfirmation resetConfirmation;
    private readonly Action openDebugWindow;
    private PlotWatchTab? requestedTab;

    public MainWindow(
        Configuration configuration,
        PluginDiagnostics diagnostics,
        Action saveConfiguration,
        ConfigurationResetConfirmation resetConfirmation,
        Action openDebugWindow)
        : base("PlotWatch", ImGuiWindowFlags.NoCollapse)
    {
        this.configuration = configuration;
        this.diagnostics = diagnostics;
        this.saveConfiguration = saveConfiguration;
        this.resetConfirmation = resetConfirmation;
        this.openDebugWindow = openDebugWindow;
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Bug,
            IconOffset = new Vector2(1, 1),
            ShowTooltip = () => ImGui.SetTooltip("Open PlotWatch diagnostics."),
            Click = _ => openDebugWindow(),
        });
        Size = new Vector2(820, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen = false;
    }

    public override void Draw()
    {
        ImGui.SetWindowFontScale(configuration.TextScalePercent / 100f);
        try
        {
            if (ImGui.BeginTabBar("##plotwatch-tabs"))
            {
                DrawTab(PlotWatchTab.Dashboard, "Dashboard", DrawDashboard);
                DrawTab(PlotWatchTab.Houses, "Houses", DrawPlaceholder);
                DrawTab(PlotWatchTab.Bids, "Bids", DrawPlaceholder);
                DrawTab(PlotWatchTab.Alerts, "Alerts", DrawPlaceholder);
                DrawTab(PlotWatchTab.Sweeps, "Sweeps", DrawPlaceholder);
                DrawTab(PlotWatchTab.Settings, "Settings", DrawSettings);
                ImGui.EndTabBar();
            }

            requestedTab = null;
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
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 9f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f * scale);
    }

    public override void PostDraw() => ImGui.PopStyleVar(3);

    public void SelectDefaultTab() => requestedTab = (PlotWatchTab)configuration.DefaultTab;

    public void SelectSettingsTab() => requestedTab = PlotWatchTab.Settings;

    private void DrawTab(PlotWatchTab tab, string label, Action drawContents)
    {
        var flags = requestedTab == tab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        if (!ImGui.BeginTabItem(label, flags))
            return;

        drawContents();
        ImGui.EndTabItem();
    }

    private static void DrawDashboard()
    {
        ImGui.TextUnformatted("PlotWatch housing and lottery tracker");
        ImGui.Separator();
        ImGui.TextWrapped("Foundation phase: the UI shell is ready. Lottery dates, bid tracking, PaissaDB listings, alerts, and sweep automation are planned for later phases.");
        ImGui.Spacing();
        ImGui.BulletText("Available houses and watchlists are not implemented yet.");
        ImGui.BulletText("No bids are being captured or stored yet.");
        ImGui.BulletText("No travel, navigation, or sweep automation is active.");
    }

    private static void DrawPlaceholder()
    {
        ImGui.TextWrapped("This section is planned but not implemented yet. PlotWatch will not display fabricated housing or lottery data.");
    }

    private void DrawSettings()
    {
        ImGui.TextUnformatted("Preferences");
        ImGui.Separator();

        ImGui.TextUnformatted("Default opening tab");
        var currentTab = configuration.DefaultTab;
        var currentName = ((PlotWatchTab)currentTab).ToString();
        if (ImGui.BeginCombo("##default-tab", currentName))
        {
            foreach (var tab in Enum.GetValues<PlotWatchTab>())
            {
                var selected = currentTab == (int)tab;
                if (ImGui.Selectable(tab.ToString(), selected))
                {
                    configuration.DefaultTab = (int)tab;
                    saveConfiguration();
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        var textScale = configuration.TextScalePercent;
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("Text scale", ref textScale, 75f, 150f, "%.0f%%"))
        {
            configuration.TextScalePercent = textScale;
            saveConfiguration();
        }

        var chat = configuration.ChatNotificationsEnabled;
        if (ImGui.Checkbox("Enable chat notifications", ref chat))
        {
            configuration.ChatNotificationsEnabled = chat;
            saveConfiguration();
        }

        var toast = configuration.ToastNotificationsEnabled;
        if (ImGui.Checkbox("Enable toast notifications", ref toast))
        {
            configuration.ToastNotificationsEnabled = toast;
            saveConfiguration();
        }

        var sound = configuration.SoundNotificationsEnabled;
        if (ImGui.Checkbox("Enable notification sound (off by default)", ref sound))
        {
            configuration.SoundNotificationsEnabled = sound;
            saveConfiguration();
        }

        ImGui.Spacing();
        DrawDiagnostics();
    }

    private void DrawDiagnostics()
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Diagnostics");
        ImGui.TextWrapped("Open the separate diagnostics window for plugin status, dependency checks, and session results.");
        if (ImGui.Button("Open diagnostics"))
            openDebugWindow();

        ImGui.TextUnformatted($"Configuration state: {diagnostics.ConfigurationStatus}");

        if (diagnostics.ConfigurationStatus.StartsWith("Load failed", StringComparison.Ordinal))
        {
            ImGui.Separator();
            ImGui.TextWrapped("Saved configuration could not be loaded. Changes will not overwrite it until you explicitly reset to defaults.");
            if (ImGui.Button("Reset configuration to defaults"))
            {
                resetConfirmation.Request();
                ImGui.OpenPopup("Confirm configuration reset");
            }

            DrawResetConfirmation();
        }
    }

    private void DrawResetConfirmation()
    {
        if (!ImGui.BeginPopupModal("Confirm configuration reset", ImGuiWindowFlags.AlwaysAutoResize))
            return;

        ImGui.TextWrapped("This replaces the saved PlotWatch configuration with defaults. This cannot be undone.");
        ImGui.Spacing();
        if (ImGui.Button("Reset and save"))
        {
            var result = resetConfirmation.Confirm();
            if (result.Succeeded)
                ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            resetConfirmation.Cancel();
            ImGui.CloseCurrentPopup();
        }

        if (resetConfirmation.LastError is { } error)
        {
            ImGui.Spacing();
            ImGui.TextWrapped($"Reset failed: {error}");
        }

        ImGui.EndPopup();
    }
}
