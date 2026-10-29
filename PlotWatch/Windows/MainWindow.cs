using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using PlotWatch.Data;
using PlotWatch.Diagnostics;
using PlotWatch.Lottery;

namespace PlotWatch.Windows;

public sealed class MainWindow : Window
{
    private readonly Configuration configuration;
    private readonly PluginDiagnostics diagnostics;
    private readonly Action saveConfiguration;
    private readonly ConfigurationResetConfirmation resetConfirmation;
    private readonly Action openDebugWindow;
    private PlotWatchTab? requestedTab;
    private LotteryScheduleSnapshot? lotterySnapshot;
    private bool showScheduleDetails;

    public MainWindow(
        Configuration configuration,
        PluginDiagnostics diagnostics,
        Action saveConfiguration,
        ConfigurationResetConfirmation resetConfirmation,
        Action openDebugWindow)
        : base("PlotWatch")
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

    public void SetLotterySnapshot(LotteryScheduleSnapshot snapshot) => lotterySnapshot = snapshot;

    private void DrawDashboard()
    {
        if (lotterySnapshot is not { } snapshot)
        {
            ImGui.TextDisabled("Waiting for lottery schedule snapshot...");
            return;
        }

        var phaseName = snapshot.CurrentPhase == LotteryPhase.Application ? "Application Period" : "Results Period";
        var activePeriod = snapshot.CurrentPhase == LotteryPhase.Application ? snapshot.Application : snapshot.Results;
        var nextPeriod = snapshot.CurrentPhase == LotteryPhase.Application ? snapshot.Results : snapshot.Application;

        ImGui.TextUnformatted("Housing Lottery");
        ImGui.SameLine();
        var detailsLabel = showScheduleDetails ? "Details ▾" : "Details ▸";
        if (ImGui.SmallButton(detailsLabel))
            showScheduleDetails = !showScheduleDetails;

        ImGui.Separator();
        ImGui.TextUnformatted(phaseName);
        ImGui.SameLine();
        ImGui.TextUnformatted($"Ends in {LotterySchedule.FormatRemaining(snapshot.Remaining)}");
        ImGui.TextWrapped($"{GetActivePeriodSummary(snapshot.CurrentPhase)} until {FormatOverviewTime(activePeriod.EndsAtUtc)}");
        ImGui.TextWrapped($"Next: {GetPeriodLabel(nextPeriod.Phase)} · {FormatOverviewTime(nextPeriod.StartsAtUtc)}");

        if (!showScheduleDetails)
            return;

        ImGui.Spacing();
        ImGui.Separator();
        DrawPeriodDetails(snapshot.Application);
        ImGui.Spacing();
        DrawPeriodDetails(snapshot.Results);
        ImGui.Spacing();
        ImGui.TextWrapped("Schedule uses a fixed 9-day cycle: 5 application days followed by 4 results days. Individual bid deadlines may differ.");
        ImGui.TextDisabled("Source: Xenoveritas FFXIV Event/Maintenance Timers");
        ImGui.SameLine();
        if (ImGui.SmallButton("Open source"))
            Dalamud.Utility.Util.OpenLink("https://xenoveritas.org/static/ffxiv/timer.html");
    }

    private void DrawPeriodDetails(LotteryPeriodOccurrence period)
    {
        ImGui.TextUnformatted(GetPeriodLabel(period.Phase));
        ImGui.SameLine();
        ImGui.TextDisabled(period.IsActive ? "ACTIVE" : "UPCOMING");
        ImGui.TextWrapped(period.Phase == LotteryPhase.Application
            ? LotterySchedule.ApplicationExplanation
            : LotterySchedule.ResultsExplanation);
        ImGui.TextWrapped($"Starts at: {FormatScheduleTime(period.StartsAtUtc)}");
        ImGui.TextWrapped($"Ends at: {FormatScheduleTime(period.EndsAtUtc)}");
        ImGui.TextWrapped($"Next at: {FormatScheduleTime(period.NextAtUtc)}");
    }

    private string FormatScheduleTime(DateTimeOffset utc)
        => LotterySchedule.FormatTimestamp(utc, configuration.DisplayLotteryTimesInUtc, TimeZoneInfo.Local);

    private string FormatOverviewTime(DateTimeOffset utc)
    {
        var displayTime = configuration.DisplayLotteryTimesInUtc
            ? utc.ToUniversalTime()
            : TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.Local);
        return displayTime.ToString(configuration.DisplayLotteryTimesInUtc
            ? "ddd, MMM d · h:mm tt 'UTC'"
            : "ddd, MMM d · h:mm tt");
    }

    private static string GetPeriodLabel(LotteryPhase phase)
        => phase == LotteryPhase.Application ? "Applications" : "Results";

    private static string GetActivePeriodSummary(LotteryPhase phase)
        => phase == LotteryPhase.Application
            ? "Entries can be placed"
            : "Winning bids can be accepted";

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

        var displayUtc = configuration.DisplayLotteryTimesInUtc;
        if (ImGui.Checkbox("Display lottery schedule in UTC", ref displayUtc))
        {
            configuration.DisplayLotteryTimesInUtc = displayUtc;
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
