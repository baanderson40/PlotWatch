using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using PlotWatch.Data;
using PlotWatch.Diagnostics;
using PlotWatch.Lottery;
using PlotWatch.Paissa;

namespace PlotWatch.Windows;

public sealed class DebugWindow : Window
{
    private static readonly (DiagnosticSection Section, string Label, string Description)[] Sections =
    [
        (DiagnosticSection.Overview, "Overview", "Overall plugin and subsystem state."),
        (DiagnosticSection.Configuration, "Configuration", "Read-only inspection of current settings and persistence state."),
        (DiagnosticSection.CommandsAndUi, "Commands & UI", "Command ownership, callback registration, and window lifecycle."),
        (DiagnosticSection.Dependencies, "Dependencies", "Installed/loaded metadata for optional companion plugins."),
        (DiagnosticSection.LotteryClock, "Lottery Clock", "Calculated 9-day housing-lottery schedule and timezone display."),
        (DiagnosticSection.PaissaApi, "PaissaDB API", "Explicit read-only checks against the live PaissaDB API."),
        (DiagnosticSection.HousingCapture, "Housing Capture", "Read-only, opt-in evidence probe. No automatic capture, bid writes, or game actions."),
        (DiagnosticSection.LogsAndErrors, "Logs & Errors", "Recent diagnostic results retained for this session."),
    ];

    private readonly Configuration configuration;
    private readonly PluginDiagnostics diagnostics;
    private readonly Action<DiagnosticAction> enqueueAction;
    private DiagnosticSection selectedSection = DiagnosticSection.Overview;
    private LotteryScheduleSnapshot? lotterySnapshot;
    private readonly HousingProbeState housingProbe;
    private readonly Action<HousingProbeOptions> armHousingProbe;
    private bool probeAllLogIds;
    private bool probeAddonNames = true;
    private int probeNumericId;

    public DebugWindow(Configuration configuration, PluginDiagnostics diagnostics, Action<DiagnosticAction> enqueueAction,
        HousingProbeState housingProbe, Action<HousingProbeOptions> armHousingProbe)
        : base("PlotWatch Diagnostics", ImGuiWindowFlags.NoCollapse)
    {
        this.configuration = configuration;
        this.diagnostics = diagnostics;
        this.enqueueAction = enqueueAction;
        this.housingProbe = housingProbe;
        this.armHousingProbe = armHousingProbe;
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

    public void SetLotterySnapshot(LotteryScheduleSnapshot snapshot) => lotterySnapshot = snapshot;

    public override void PreDraw()
    {
        PlotWatchTheme.Push();
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 12f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f * scale);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(3);
        PlotWatchTheme.Pop();
    }

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
            case DiagnosticSection.LotteryClock:
                DrawLotteryClock();
                break;
            case DiagnosticSection.PaissaApi:
                DrawPaissaApi();
                break;
            case DiagnosticSection.LogsAndErrors:
                DrawHistory();
                break;
            case DiagnosticSection.HousingCapture:
                DrawHousingCapture();
                break;
        }

        ImGui.Spacing();
        if (selectedSection != DiagnosticSection.HousingCapture)
            DrawLatestResult();
    }

    private void DrawHousingCapture()
    {
        var view = housingProbe.View;
        ImGui.TextWrapped("Probe only: no bids are created, edited, or deleted; no game actions or native hooks. Stop/expiry invalidates observation context. Templates do not reveal live lnum values.");
        ImGui.TextUnformatted($"{view.Status} Remaining: {view.Remaining.TotalSeconds:F1}s");
        ImGui.TextUnformatted($"Character available: {view.Context.CharacterAvailable}; home world: {view.Context.HomeWorld}; current world: {view.Context.CurrentWorld}");
        ImGui.TextUnformatted($"Territory: {view.Context.Territory}; client language: {view.Context.Language}");
        ImGui.BeginDisabled(view.Armed);
        ImGui.Checkbox("All LogMessage IDs (metadata only, while armed)", ref probeAllLogIds);
        ImGui.Checkbox("Discover addon lifecycle names (names only)", ref probeAddonNames);
        ImGui.SetNextItemWidth(180);
        ImGui.InputInt("Numeric parameters for selected LogMessage ID (0 = off)", ref probeNumericId);
        if (probeNumericId < 0) probeNumericId = 0;
        if (ImGui.Button("Arm 15-second probe"))
            armHousingProbe(new(probeAllLogIds, probeNumericId > 0 ? (uint)probeNumericId : null, probeAddonNames));
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Stop")) enqueueAction(DiagnosticAction.StopHousingProbe);
        ImGui.SameLine();
        if (ImGui.Button("Clear results")) enqueueAction(DiagnosticAction.ClearHousingProbe);
        if (ImGui.Button("Inspect sheet templates")) enqueueAction(DiagnosticAction.InspectHousingTemplates);
        ImGui.SameLine();
        if (ImGui.Button("Copy diagnostic report")) ImGui.SetClipboardText(housingProbe.BuildReport(diagnostics.BuildId));
        ImGui.SameLine();
        if (ImGui.Button("Copy events only")) ImGui.SetClipboardText(housingProbe.BuildReport(diagnostics.BuildId, includeTemplates: false));
        ImGui.TextWrapped("LogMessage IDs belong to the LogMessage sheet, NOT Addon/custom row IDs. All-ID tracing withholds argument values; numeric inspection requires a selected LogMessage ID. Strings, entities, Content IDs, 64-bit values and pointers are withheld. System chat/template matches are candidates only, not native identity proof.");

        if (ImGui.CollapsingHeader($"Sheet templates ({view.Templates.Count})"))
        {
            foreach (var template in view.Templates)
            {
                if (!ImGui.TreeNode($"{template.Sheet}/{template.Row}##housing-template-{template.Sheet}-{template.Row}")) continue;
                ImGui.TextUnformatted($"Column {template.Column}; language={template.Language}; lnums=[{string.Join(",", template.LocalNumbers)}]");
                ImGui.TextWrapped(template.Structure);
                ImGui.TextWrapped(template.Macro);
                ImGui.TreePop();
            }
        }
        if (ImGui.CollapsingHeader("Test checklist"))
        {
            ImGui.BulletText("A: inspect templates; confirm expected local-number references, not live values.");
            ImGui.BulletText("B: arm; open Duty > Timers > Estate > Housing Lottery Status; repeat and copy report.");
            ImGui.BulletText("C: arm; view known placard, close it, view a different plot; cancel claim/refund prompts.");
            ImGui.BulletText("D: unrelated UI, expiry, logout/character switch; verify context is invalidated.");
            ImGui.BulletText("E/F: wait for Entry; observe a real intended bid and its status. Verify Personal/FC separately.");
            ImGui.TextWrapped("Do not spend gil just to test. If no standard event exposes custom-sheet identity/parameters, diagnostics are inconclusive; a separate native-probe decision is required.");
        }
        ImGui.Separator();
        ImGui.TextUnformatted($"Events: {view.Entries.Count}/{HousingProbeState.Capacity}; oldest dropped: {view.Dropped}");
        if (ImGui.BeginChild("##housing-probe-events", new Vector2(0, 0), true, ImGuiWindowFlags.HorizontalScrollbar))
            foreach (var entry in view.Entries)
            {
                ImGui.TextUnformatted($"{entry.AtUtc:HH:mm:ss.fff} UTC [{entry.Source}]");
                ImGui.TextWrapped(entry.Detail);
            }
        ImGui.EndChild();
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
        ImGui.TextUnformatted($"Lifestream: {diagnostics.LifestreamStatus}");
        ImGui.TextWrapped("Lifestream detection reports installed and loaded metadata only. Compatibility and travel behavior are not validated.");
        ImGui.Separator();
        if (DrawActionButton("Check dependencies"))
            enqueueAction(DiagnosticAction.InspectDependencies);
    }

    private void DrawLotteryClock()
    {
        ImGui.TextWrapped(diagnostics.LotteryClockStatus);
        ImGui.TextWrapped("Schedule calculations use the local system clock converted to UTC. The check is read-only and does not inspect bids or game state.");
        ImGui.Separator();
        if (DrawActionButton("Inspect lottery clock"))
            enqueueAction(DiagnosticAction.InspectLotteryClock);
    }

    private void DrawPaissaApi()
    {
        ImGui.TextWrapped("Requests run only when you press a check button. Responses are kept in memory for this session; failed requests do not erase the last successful result.");
        ImGui.TextDisabled($"Base URL: {PaissaApiClient.BaseUrl}");
        ImGui.Separator();

        if (DrawActionButton("Fetch worlds"))
            enqueueAction(DiagnosticAction.FetchPaissaWorlds);
        ImGui.SameLine();
        if (DrawActionButton("Inspect OpenAPI schema"))
            enqueueAction(DiagnosticAction.InspectPaissaSchema);
        if (diagnostics.IsRequestPending && ImGui.Button("Cancel current API request"))
            enqueueAction(DiagnosticAction.CancelPaissaRequest);
        ImGui.TextWrapped($"World list: {diagnostics.PaissaWorldsStatus}; cached worlds: {diagnostics.PaissaWorlds.Count}; last success: {diagnostics.PaissaWorldsLastSuccessUtc?.ToString("O") ?? "none"}");

        if (diagnostics.PaissaWorlds.Count > 0)
        {
            var selected = diagnostics.PaissaWorlds.FirstOrDefault(world => world.Id == diagnostics.SelectedPaissaWorldId)
                ?? diagnostics.PaissaWorlds[0];
            if (ImGui.BeginCombo("World", $"{selected.Name} ({selected.DataCenterName}) · ID {selected.Id}"))
            {
                foreach (var world in diagnostics.PaissaWorlds)
                {
                    var isSelected = world.Id == diagnostics.SelectedPaissaWorldId;
                    if (ImGui.Selectable($"{world.Name} ({world.DataCenterName}) · ID {world.Id}", isSelected))
                        diagnostics.SelectedPaissaWorldId = world.Id;
                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            if (DrawActionButton("Inspect selected world"))
                enqueueAction(DiagnosticAction.InspectPaissaWorld);
        }
        else
        {
            ImGui.TextDisabled("Fetch world list before selecting a world.");
        }

        ImGui.TextWrapped($"World inspection: {diagnostics.PaissaInspectionStatus}; last success: {diagnostics.PaissaInspectionLastSuccessUtc?.ToString("O") ?? "none"}");
        if (diagnostics.PaissaInspection is { } inspection)
        {
            ImGui.TextUnformatted($"{inspection.Name} (ID {inspection.Id}) · reported open plots: {inspection.ReportedOpenPlots?.ToString() ?? "Unknown"} · oldest observation: {FormatUnix(inspection.OldestPlotTime)}");
            if (inspection.Warnings.Count > 0)
            {
                ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f), $"Partial/unexpected response: {inspection.Warnings.Count} schema issue(s)");
                foreach (var warning in inspection.Warnings.Take(8))
                    ImGui.BulletText(warning);
            }
            ImGui.TextUnformatted("Districts:");
            foreach (var district in inspection.Districts)
                ImGui.BulletText($"{district.Name} (ID {district.Id}): API count {district.ReportedOpenPlots?.ToString() ?? "Unknown"}; parsed {district.ActualPlotCount}; oldest {FormatUnix(district.OldestPlotTime)}");

            ImGui.TextUnformatted($"Field presence across {inspection.Districts.Sum(district => district.ActualPlotCount)} returned plots:");
            if (ImGui.BeginTable("##paissa-fields", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0, 180f * ImGuiHelpers.GlobalScale)))
            {
                ImGui.TableSetupColumn("Field");
                ImGui.TableSetupColumn("Present");
                ImGui.TableSetupColumn("Null");
                ImGui.TableSetupColumn("Missing");
                ImGui.TableSetupColumn("Sample");
                ImGui.TableHeadersRow();
                foreach (var field in inspection.Fields)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(field.Name);
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(field.PresentCount.ToString());
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(field.NullCount.ToString());
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(field.MissingCount.ToString());
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(field.SampleValue);
                }
                ImGui.EndTable();
            }

            ImGui.TextUnformatted("Sample plots (API ward/plot values are zero-based):");
            foreach (var plot in inspection.Samples)
            {
                var address = plot.WardNumber is { } ward && plot.PlotNumber is { } number
                    ? $"W{ward + 1} P{number + 1} (raw {ward}/{number})"
                    : "Address unknown";
                var globalPhase = lotterySnapshot is { } schedule ? PaissaHousingPolicy.GetPhaseLabel(plot.PurchaseSystem, schedule.NowUtc) : "Waiting for schedule";
                var globalClose = lotterySnapshot is { } clock ? PaissaHousingPolicy.GetDisplayedPhaseDeadline(plot.PurchaseSystem, clock.NowUtc)?.ToString("O") ?? "Unknown" : "Unknown";
                ImGui.BulletText($"{address}; size={FormatSize(plot.Size)}; price={plot.Price?.ToString("N0") ?? "Unknown"}; type={PaissaApiParser.DescribePurchaseSystem(plot.PurchaseSystem)}; PW global phase={globalPhase}; global close={globalClose}; raw API phase={PaissaApiParser.DescribeLotteryPhase(plot.LottoPhase)}; entrants={plot.LottoEntries?.ToString() ?? "Unknown"}; raw API deadline={FormatUnix(plot.LottoPhaseUntil)}; last observed={FormatUnix(plot.LastUpdatedTime)}");
            }
        }

        ImGui.Separator();
        ImGui.TextWrapped($"OpenAPI schema: {diagnostics.PaissaSchemaStatus}; last success: {diagnostics.PaissaSchemaLastSuccessUtc?.ToString("O") ?? "none"}");
        if (!string.IsNullOrEmpty(diagnostics.PaissaSchemaSummary))
            ImGui.TextWrapped(diagnostics.PaissaSchemaSummary);
    }

    private static string FormatUnix(double? seconds)
        => PaissaApiParser.UnixSecondsToUtc(seconds)?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "Unknown";

    private static string FormatUnix(long? seconds)
    {
        if (seconds is null)
            return "Unknown";
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds.Value).ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
        }
        catch (ArgumentOutOfRangeException)
        {
            return "Invalid timestamp";
        }
    }

    private static string FormatSize(int? size) => size switch
    {
        0 => "Small",
        1 => "Medium",
        2 => "Large",
        null => "Unknown",
        _ => $"Unrecognized ({size})",
    };

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
