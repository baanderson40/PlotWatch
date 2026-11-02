using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using PlotWatch.Data;
using PlotWatch.Diagnostics;
using PlotWatch.Lottery;
using PlotWatch.Paissa;
using PlotWatch.Travel;

namespace PlotWatch.Windows;

public sealed partial class MainWindow : Window
{
    private readonly Configuration configuration;
    private readonly BidTracker bidTracker;
    private readonly PluginDiagnostics diagnostics;
    private readonly Action saveConfiguration;
    private readonly ConfigurationResetConfirmation resetConfirmation;
    private readonly Action openDebugWindow;
    private readonly Action fetchHousingWorlds;
    private readonly Action refreshHousingWorlds;
    private readonly Action cancelHousingRequest;
    private readonly Dictionary<int, PaissaHousingWorldData> housingCache = [];
    private readonly Dictionary<int, string> housingWorldStatuses = [];
    private IReadOnlyList<PaissaWorld> housingWorlds = [];
    private string housingDirectoryStatus = "Not fetched";
    private readonly HashSet<int> selectedHousingWorldIds = [];
    private int? homeWorldId;
    private bool requestedHousingWorlds;
    private bool housingRequestPending;
    private DateTimeOffset refreshCooldownUntilUtc;
    private DateTimeOffset nextAutomaticRefreshUtc;
    private int refreshProgressCompleted;
    private int refreshProgressTotal;
    private readonly HashSet<int?> selectedDistrictIds = [];
    private readonly HashSet<int?> selectedSizes = [];
    private readonly HashSet<PaissaEligibilityFilter> selectedEligibility = [];
    private readonly HashSet<PaissaPhaseFilter> selectedPhases = [];
    private readonly HashSet<PaissaStatusFilter> selectedStatuses = [];
    private bool includeOutdatedReports;
    private bool initializedPlotFilters;
    private int tableSortColumn;
    private bool tableSortAscending = true;
    private static readonly (int? Id, string Name)[] HousingDistricts =
    [
        (339, "Mist"), (340, "The Lavender Beds"), (341, "The Goblet"),
        (641, "Shirogane"), (979, "Empyreum"),
    ];
    private PlotWatchTab? requestedTab;
    private LotteryScheduleSnapshot? lotterySnapshot;
    private Vector2 scheduleDetailsPopupPosition;

    public MainWindow(
        Configuration configuration,
        PluginDiagnostics diagnostics,
        Action saveConfiguration,
        ConfigurationResetConfirmation resetConfirmation,
        Action openDebugWindow,
        Action fetchHousingWorlds,
        Action refreshHousingWorlds,
        Action cancelHousingRequest,
        BidTracker bidTracker,
        PlotTravel plotTravel,
        Action<PlotDestination> requestTravel,
        Action stopTravel,
        WatchedPlotTracker watchedPlots)
        : base("PlotWatch")
    {
        this.configuration = configuration;
        this.bidTracker = bidTracker;
        this.watchedPlots = watchedPlots;
        this.plotTravel = plotTravel;
        this.requestTravel = requestTravel;
        this.stopTravel = stopTravel;
        this.diagnostics = diagnostics;
        this.saveConfiguration = saveConfiguration;
        this.resetConfirmation = resetConfirmation;
        this.openDebugWindow = openDebugWindow;
        this.fetchHousingWorlds = fetchHousingWorlds;
        this.refreshHousingWorlds = refreshHousingWorlds;
        this.cancelHousingRequest = cancelHousingRequest;
        selectedHousingWorldIds.UnionWith(configuration.HousingWorldIds);
        selectedDistrictIds.UnionWith(configuration.HousingDistrictIds.Where(id => id >= 0).Select(id => (int?)id));
        if ((configuration.HousingSizeMask & 1) != 0) selectedSizes.Add(0);
        if ((configuration.HousingSizeMask & 2) != 0) selectedSizes.Add(1);
        if ((configuration.HousingSizeMask & 4) != 0) selectedSizes.Add(2);
        AddMasked(selectedEligibility, configuration.HousingEligibilityMask);
        AddMasked(selectedPhases, configuration.HousingPhaseMask);
        AddMasked(selectedStatuses, configuration.HousingStatusMask);
        includeOutdatedReports = configuration.HousingIncludeOutdatedReports;
        initializedPlotFilters = true;
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Bug,
            IconOffset = new Vector2(1, 1),
            ShowTooltip = () => PlotWatchTooltips.Show("Open PlotWatch diagnostics."),
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
            DrawTravelStatus();
            if (ImGui.BeginTabBar("##plotwatch-tabs"))
            {
                DrawTab(PlotWatchTab.Dashboard, "Dashboard", DrawDashboard);
                DrawTab(PlotWatchTab.Houses, "Available Houses", DrawHouses);
                DrawTab(PlotWatchTab.Bids, "My Bids", DrawBids);
                DrawTab(PlotWatchTab.Alerts, "Alerts", DrawPlaceholder);
                DrawTab(PlotWatchTab.Settings, "Settings", DrawSettings);
                ImGui.EndTabBar();
            }

            SaveHousingPreferencesIfChanged();

            requestedTab = null;
        }
        finally
        {
            ImGui.SetWindowFontScale(1f);
        }
    }

    public override void PreDraw()
    {
        PlotWatchTheme.Push();
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 12f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 9f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f * scale);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(3);
        PlotWatchTheme.Pop();
    }

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

    public void SetHousingWorlds(IReadOnlyList<PaissaWorld>? worlds, string status)
    {
        housingDirectoryStatus = status;
        if (worlds is not null)
        {
            requestedHousingWorlds = true;
            housingWorlds = worlds;
            selectedHousingWorldIds.RemoveWhere(id => worlds.All(world => world.Id != id));
            if (!initializedPlotFilters)
            {
                selectedDistrictIds.UnionWith(new int?[] { 339, 340, 341, 641, 979 });
                selectedSizes.UnionWith(new int?[] { 0, 1, 2 });
                selectedEligibility.UnionWith(Enum.GetValues<PaissaEligibilityFilter>()
                    .Where(option => option != PaissaEligibilityFilter.Unknown));
                selectedPhases.UnionWith([
                    PaissaPhaseFilter.Entry,
                    PaissaPhaseFilter.Results,
                    PaissaPhaseFilter.Unknown,
                ]);
                initializedPlotFilters = true;
            }
        }

    }

    public void SetHousingWorldData(int worldId, PaissaHousingWorldData? data, string status)
    {
        housingWorldStatuses[worldId] = status;
        if (data is not null)
        {
            housingCache[worldId] = data;
            watchedPlots.ObserveWorld(data);
        }
    }

    public void SetHomeWorld(int? worldId, ulong? characterContentId = null)
    {
        homeWorldId = worldId;
        if (HousingCharacterSelection.Apply(configuration, selectedHousingWorldIds, worldId, characterContentId))
            saveConfiguration();
    }

    public IReadOnlyList<int> GetSelectedHousingWorldIds() => selectedHousingWorldIds.Order().ToArray();

    public void SetHousingRequestPending(bool pending) => housingRequestPending = pending;

    public void SetHousingRefreshCooldown(DateTimeOffset untilUtc) => refreshCooldownUntilUtc = untilUtc;

    public void SetNextAutomaticRefresh(DateTimeOffset atUtc) => nextAutomaticRefreshUtc = atUtc;

    public void SetHousingRefreshProgress(int completed, int total)
    {
        refreshProgressCompleted = completed;
        refreshProgressTotal = total;
    }

    public PaissaWorld? GetHousingWorld(int worldId)
        => housingWorlds.FirstOrDefault(world => world.Id == worldId);

    public IReadOnlyList<PaissaWorld> GetAllHousingWorlds() => housingWorlds;

    public bool HasHousingSnapshot(int worldId) => housingCache.ContainsKey(worldId);

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
        if (ImGui.SmallButton("Details ▾"))
        {
            scheduleDetailsPopupPosition = new(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y);
            ImGui.OpenPopup("##lottery-details");
        }
        DrawScheduleDetailsPopup(snapshot);

        ImGui.Separator();
        ImGui.TextUnformatted(phaseName);
        ImGui.SameLine();
        ImGui.TextUnformatted($"Ends in {LotterySchedule.FormatRemaining(snapshot.Remaining)}");
        ImGui.TextWrapped($"{GetActivePeriodSummary(snapshot.CurrentPhase)} until {FormatOverviewTime(activePeriod.EndsAtUtc)}");
        ImGui.TextWrapped($"Next: {GetPeriodLabel(nextPeriod.Phase)} · {FormatOverviewTime(nextPeriod.StartsAtUtc)}");

        DrawBidSummary(snapshot.NowUtc);
        DrawWatchedPlots(snapshot.NowUtc);
    }

    private void DrawScheduleDetailsPopup(LotteryScheduleSnapshot snapshot)
    {
        if (ImGui.IsPopupOpen("##lottery-details"))
        {
            var viewport = ImGui.GetWindowViewport();
            var width = Math.Max(1f, Math.Min(560f * ImGuiHelpers.GlobalScale,
                viewport.WorkSize.X - 2f * ImGui.GetStyle().WindowPadding.X));
            ImGui.SetNextWindowPos(scheduleDetailsPopupPosition, ImGuiCond.Appearing);
            ImGui.SetNextWindowSizeConstraints(new Vector2(width, 0),
                new Vector2(width, Math.Max(ImGui.GetFrameHeightWithSpacing(), viewport.WorkSize.Y * 0.65f)));
        }
        if (!ImGui.BeginPopup("##lottery-details")) return;
        var popupViewport = ImGui.GetWindowViewport();
        var maximumPosition = Vector2.Max(popupViewport.WorkPos,
            popupViewport.WorkPos + popupViewport.WorkSize - ImGui.GetWindowSize());
        ImGui.SetWindowPos(Vector2.Clamp(scheduleDetailsPopupPosition, popupViewport.WorkPos, maximumPosition));
        ImGui.TextUnformatted("Housing Lottery — Details");
        ImGui.Separator();
        DrawPeriodDetails(snapshot.Application);
        ImGui.Spacing();
        DrawPeriodDetails(snapshot.Results);
        ImGui.Spacing();
        ImGui.TextWrapped("Schedule uses a fixed 9-day cycle: 5 application days followed by 4 results days. Individual bid deadlines may differ.");
        ImGui.TextWrapped("Source: Xenoveritas FFXIV Event/Maintenance Timers");
        if (ImGui.SmallButton("Open source"))
            Dalamud.Utility.Util.OpenLink("https://xenoveritas.org/static/ffxiv/timer.html");
        ImGui.EndPopup();
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

    private void DrawHouses()
    {
        if (!requestedHousingWorlds)
        {
            requestedHousingWorlds = true;
            fetchHousingWorlds();
        }

        if (housingWorlds.Count == 0)
        {
            ImGui.TextDisabled("Listings from PaissaDB. Download time and source observation time are separate.");
            ImGui.TextWrapped($"World directory: {housingDirectoryStatus}");
            if (ImGui.Button("Fetch world directory"))
            {
                requestedHousingWorlds = true;
                fetchHousingWorlds();
            }
            return;
        }

        var availableWidth = ImGui.GetContentRegionAvail().X;
        var sidebarWidth = Math.Min(GetWorldShortcutWidth(), Math.Max(160f, availableWidth - 260f));
        if (ImGui.BeginChild("##housing-filter-sidebar", new Vector2(sidebarWidth, 0), true))
        {
            var footerHeight = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y + 8f;
            if (ImGui.BeginChild("##housing-filter-scroll", new Vector2(0, -footerHeight), false))
            {
                DrawWorldFilters();
                DrawDistrictFilters();
                DrawSizeFilters();
                DrawEligibilityFilters();
                DrawPhaseFilters();
                DrawReportedStatusFilters();
                if (ImGui.Button("Reset Filters"))
                    ResetHousingFilters();
            }
            ImGui.EndChild();
            DrawCacheRefreshControl();
        }
        ImGui.EndChild();

        ImGui.SameLine();
        if (ImGui.BeginChild("##housing-results", new Vector2(0, 0), false))
        {
            ImGui.TextDisabled("Right-click a plot number to travel or track. * = tracked on Dashboard.");
            DrawHousingResults();
        }
        ImGui.EndChild();
    }

    private static float GetWorldShortcutWidth()
    {
        var style = ImGui.GetStyle();
        var labels = new[] { "World", "DC", "All", "Clear" };
        var textWidth = labels.Sum(label => ImGui.CalcTextSize(label).X);
        return textWidth + labels.Length * style.FramePadding.X * 2f
            + (labels.Length - 1) * style.ItemSpacing.X
            + style.WindowPadding.X * 2f + 20f;
    }

    private void DrawWorldFilters()
    {
        if (ImGui.CollapsingHeader("Worlds", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (ImGui.SmallButton("World"))
            {
                selectedHousingWorldIds.Clear();
                if (homeWorldId is { } home && housingWorlds.Any(world => world.Id == home))
                    selectedHousingWorldIds.Add(home);
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("DC"))
            {
                selectedHousingWorldIds.Clear();
                if (homeWorldId is { } home && housingWorlds.FirstOrDefault(world => world.Id == home) is { } homeWorld)
                    selectedHousingWorldIds.UnionWith(housingWorlds.Where(world => world.DataCenterId == homeWorld.DataCenterId).Select(world => world.Id));
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("All"))
                selectedHousingWorldIds.UnionWith(housingWorlds.Select(world => world.Id));
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear"))
                selectedHousingWorldIds.Clear();

            foreach (var group in housingWorlds.GroupBy(world => world.DataCenterName).OrderBy(group => group.Key))
            {
                var groupName = group.Key;
                var worlds = group.OrderBy(world => world.Name).ToArray();
                var checkedCount = worlds.Count(world => selectedHousingWorldIds.Contains(world.Id));
                var treeFlags = ImGuiTreeNodeFlags.SpanAvailWidth;
                if (string.Equals(groupName, HomeDataCenterName(), StringComparison.Ordinal))
                    treeFlags |= ImGuiTreeNodeFlags.DefaultOpen;
                if (ImGui.TreeNodeEx($"{groupName}##dc-{groupName}", treeFlags, $"{groupName}  {checkedCount}/{worlds.Length}"))
                {
                    foreach (var world in worlds)
                    {
                        var selected = selectedHousingWorldIds.Contains(world.Id);
                        if (ImGui.Checkbox($"{world.Name}##world-{world.Id}", ref selected))
                        {
                            if (selected) selectedHousingWorldIds.Add(world.Id);
                            else selectedHousingWorldIds.Remove(world.Id);
                        }
                    }
                    ImGui.TreePop();
                }
            }
        }
    }

    private string? HomeDataCenterName()
        => homeWorldId is { } home ? housingWorlds.FirstOrDefault(world => world.Id == home)?.DataCenterName : null;

    private void DrawDistrictFilters()
    {
        if (!ImGui.CollapsingHeader("Districts", ImGuiTreeNodeFlags.DefaultOpen))
            return;
        foreach (var (id, name) in HousingDistricts)
        {
            var selected = selectedDistrictIds.Contains(id);
            if (ImGui.Checkbox(name, ref selected))
            {
                if (selected) selectedDistrictIds.Add(id);
                else selectedDistrictIds.Remove(id);
            }
        }
    }

    private void DrawSizeFilters()
    {
        if (!ImGui.CollapsingHeader("Size", ImGuiTreeNodeFlags.DefaultOpen))
            return;
        DrawNullableIntCheckbox("Small", 0, selectedSizes);
        DrawNullableIntCheckbox("Medium", 1, selectedSizes);
        DrawNullableIntCheckbox("Large", 2, selectedSizes);
    }

    private void DrawEligibilityFilters()
    {
        if (!ImGui.CollapsingHeader("Eligibility", ImGuiTreeNodeFlags.DefaultOpen))
            return;
        DrawEligibilityCheckbox("Free Company", PaissaEligibilityFilter.FreeCompany);
        DrawEligibilityCheckbox("Personal & FC", PaissaEligibilityFilter.PersonalAndFreeCompany);
        DrawEligibilityCheckbox("Personal", PaissaEligibilityFilter.Personal);
    }

    private void DrawEligibilityCheckbox(string label, PaissaEligibilityFilter option)
    {
        var selected = selectedEligibility.Contains(option);
        if (ImGui.Checkbox(label, ref selected))
        {
            if (selected) selectedEligibility.Add(option);
            else selectedEligibility.Remove(option);
        }
    }

    private void DrawPhaseFilters()
    {
        if (!ImGui.CollapsingHeader("Period", ImGuiTreeNodeFlags.DefaultOpen))
            return;
        DrawPhaseCheckbox("Entry", PaissaPhaseFilter.Entry);
        DrawPhaseCheckbox("Results", PaissaPhaseFilter.Results);
        DrawPhaseCheckbox("Unknown / Not applicable", PaissaPhaseFilter.Unknown);
    }

    private void DrawPhaseCheckbox(string label, PaissaPhaseFilter option)
    {
        var selected = selectedPhases.Contains(option);
        if (ImGui.Checkbox(label, ref selected))
        {
            if (selected) selectedPhases.Add(option);
            else selectedPhases.Remove(option);
        }
    }

    private void DrawReportedStatusFilters()
    {
        if (!ImGui.CollapsingHeader("Reported status", ImGuiTreeNodeFlags.DefaultOpen)) return;
        foreach (var (label, option) in new[]
        {
            ("Accepting entries", PaissaStatusFilter.AcceptingEntries),
            ("Results##reported", PaissaStatusFilter.Results),
            ("Bidding unavailable", PaissaStatusFilter.Unavailable),
            ("Not reported / Unrecognized", PaissaStatusFilter.Unknown),
        })
        {
            var selected = selectedStatuses.Contains(option);
            if (!ImGui.Checkbox(label, ref selected)) continue;
            if (selected) selectedStatuses.Add(option);
            else selectedStatuses.Remove(option);
        }
        ImGui.Checkbox("Include outdated reports", ref includeOutdatedReports);
    }

    private static void DrawNullableIntCheckbox(string label, int? value, HashSet<int?> set)
    {
        var selected = set.Contains(value);
        if (ImGui.Checkbox(label, ref selected))
        {
            if (selected) set.Add(value);
            else set.Remove(value);
        }
    }

    private void DrawCacheRefreshControl()
    {
        var nowUtc = lotterySnapshot?.NowUtc ?? DateTimeOffset.MinValue;
        var cacheCount = housingCache.Count;
        var oldest = housingCache.Values.Select(snapshot => snapshot.DownloadedAtUtc).DefaultIfEmpty().Min();
        var label = housingRequestPending
            ? $"Refreshing: {refreshProgressCompleted}/{refreshProgressTotal} worlds"
            : $"Cached: {cacheCount} worlds - {(cacheCount == 0 ? "never" : FormatAge(oldest, nowUtc))}";
        if (ImGui.Selectable(label, false, ImGuiSelectableFlags.None, Vector2.Zero))
        {
            if (!housingRequestPending && refreshCooldownUntilUtc <= nowUtc)
                refreshHousingWorlds();
        }
        if (ImGui.IsItemHovered())
        {
            var lines = new List<string>();
            if (housingRequestPending)
            {
                lines.Add($"Refreshing world listings: {refreshProgressCompleted}/{refreshProgressTotal} completed.");
                if (refreshCooldownUntilUtc > nowUtc)
                    lines.Add($"Server cooldown: {FormatRemaining(refreshCooldownUntilUtc - nowUtc)} remaining.");
            }
            else if (refreshCooldownUntilUtc > nowUtc)
            {
                lines.Add($"Refresh available in {FormatRemaining(refreshCooldownUntilUtc - nowUtc)}.");
                lines.Add("Click to refresh after cooldown.");
            }
            else
                lines.Add("Refresh now — click to refresh all worlds.");

            lines.Add($"Cached worlds: {cacheCount}/{housingWorlds.Count}; listings: {housingCache.Values.Sum(snapshot => snapshot.Listings.Count)}.");
            lines.Add($"Oldest download: {(oldest == default ? "never" : oldest.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"))}.");
            if (nextAutomaticRefreshUtc != default)
                lines.Add($"Next automatic refresh: {nextAutomaticRefreshUtc:yyyy-MM-dd HH:mm:ss 'UTC'}.");
            PlotWatchTooltips.Show(lines.ToArray());
        }
    }

    private static string FormatAge(DateTimeOffset utc, DateTimeOffset nowUtc)
    {
        if (utc == default)
            return "unknown";
        var age = nowUtc - utc;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalDays >= 1) return $"{(int)age.TotalDays}d ago";
        if (age.TotalHours >= 1) return $"{(int)age.TotalHours}h ago";
        return $"{Math.Max(1, (int)age.TotalMinutes)}m ago";
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        return remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{remaining.Minutes:00}:{remaining.Seconds:00}";
    }

    private void ResetHousingFilters()
    {
        selectedDistrictIds.Clear();
        selectedDistrictIds.UnionWith(new int?[] { 339, 340, 341, 641, 979 });
        selectedSizes.Clear();
        selectedSizes.UnionWith(new int?[] { 0, 1, 2 });
        selectedEligibility.Clear();
        selectedEligibility.UnionWith(Enum.GetValues<PaissaEligibilityFilter>()
            .Where(option => option != PaissaEligibilityFilter.Unknown));
        selectedPhases.Clear();
        selectedPhases.UnionWith([
            PaissaPhaseFilter.Entry, PaissaPhaseFilter.Results,
            PaissaPhaseFilter.Unknown,
        ]);
        selectedStatuses.Clear();
        selectedStatuses.UnionWith(Enum.GetValues<PaissaStatusFilter>());
        includeOutdatedReports = true;
    }

    private static void AddMasked<T>(HashSet<T> values, int mask) where T : struct, Enum
    {
        var options = Enum.GetValues<T>();
        for (var index = 0; index < options.Length; index++)
            if ((mask & (1 << index)) != 0)
                values.Add(options[index]);
    }

    private void SaveHousingPreferencesIfChanged()
    {
        var worldIds = selectedHousingWorldIds.Order().ToList();
        var districtIds = selectedDistrictIds.Select(id => id ?? -1).Order().ToList();
        var sizeMask = (selectedSizes.Contains(0) ? 1 : 0)
            | (selectedSizes.Contains(1) ? 2 : 0)
            | (selectedSizes.Contains(2) ? 4 : 0)
            | (selectedSizes.Contains(null) ? 8 : 0);
        var eligibilityMask = Mask(selectedEligibility);
        var phaseMask = Mask(selectedPhases);
        var statusMask = Mask(selectedStatuses);
        var changed = !configuration.HousingWorldIds.SequenceEqual(worldIds)
            || !configuration.HousingDistrictIds.SequenceEqual(districtIds)
            || configuration.HousingSizeMask != sizeMask
            || configuration.HousingEligibilityMask != eligibilityMask
            || configuration.HousingPhaseMask != phaseMask
            || configuration.HousingStatusMask != statusMask
            || configuration.HousingIncludeOutdatedReports != includeOutdatedReports;
        if (!changed)
            return;
        configuration.HousingWorldIds = worldIds;
        configuration.HousingDistrictIds = districtIds;
        configuration.HousingSizeMask = sizeMask;
        configuration.HousingEligibilityMask = eligibilityMask;
        configuration.HousingPhaseMask = phaseMask;
        configuration.HousingStatusMask = statusMask;
        configuration.HousingIncludeOutdatedReports = includeOutdatedReports;
        saveConfiguration();
    }

    private static int Mask<T>(IReadOnlySet<T> values) where T : struct, Enum
    {
        var mask = 0;
        var options = Enum.GetValues<T>();
        for (var index = 0; index < options.Length; index++)
            if (values.Contains(options[index]))
                mask |= 1 << index;
        return mask;
    }

    private void DrawHousingResults()
    {
        var selectedWorlds = housingWorlds.Where(world => selectedHousingWorldIds.Contains(world.Id)).ToArray();

        if (housingRequestPending)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                cancelHousingRequest();
        }

        if (selectedWorlds.Length == 0)
        {
            ImGui.TextDisabled("Select one or more worlds in the sidebar.");
            return;
        }

        var missing = selectedWorlds.Where(world => !housingCache.ContainsKey(world.Id)).ToArray();
        if (missing.Length > 0)
            ImGui.TextWrapped($"Not loaded or unavailable: {string.Join(", ", missing.Select(world => world.Name))}");

        var snapshots = selectedWorlds.Where(world => housingCache.ContainsKey(world.Id))
            .Select(world => housingCache[world.Id]).ToArray();
        if (snapshots.Length == 0)
        {
            ImGui.TextDisabled("No successful listing responses cached. Select worlds, then refresh selected.");
            return;
        }
        foreach (var failedWorld in selectedWorlds.Where(world => housingWorldStatuses.TryGetValue(world.Id, out var state) && state.Contains("failed", StringComparison.OrdinalIgnoreCase)))
            ImGui.BulletText($"{failedWorld.Name}: {housingWorldStatuses[failedWorld.Id]}");

        DrawHousingTable(snapshots);
    }

    private void DrawHousingTable(IReadOnlyList<PaissaHousingWorldData> snapshots)
    {
        if (lotterySnapshot is not { } schedule)
        {
            ImGui.TextDisabled("Waiting for lottery schedule snapshot...");
            return;
        }
        var asOfUtc = schedule.NowUtc;
        var filters = new PaissaHousingFilters(
            selectedDistrictIds,
            selectedSizes,
            selectedEligibility,
            selectedPhases,
            selectedStatuses,
            includeOutdatedReports);
        var rows = snapshots.SelectMany(snapshot => snapshot.Listings.Select(plot => (Snapshot: snapshot, Plot: plot)))
            .Where(row => PaissaHousingPolicy.Matches(row.Plot, filters, asOfUtc))
            .ToList();

        const ImGuiTableFlags tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
            ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable |
            ImGuiTableFlags.Sortable | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX;
        var footerHeight = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y;
        var tableHeight = Math.Max(1f, ImGui.GetContentRegionAvail().Y - footerHeight);
        if (!ImGui.BeginTable("##paissa-housing-listings", 12, tableFlags, new Vector2(0, tableHeight)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.DefaultSort, 0, 0);
        ImGui.TableSetupColumn("District", ImGuiTableColumnFlags.None, 0, 1);
        ImGui.TableSetupColumn("Ward", ImGuiTableColumnFlags.None, 0, 2);
        ImGui.TableSetupColumn("Plot", ImGuiTableColumnFlags.None, 0, 3);
        ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.None, 0, 4);
        ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.None, 0, 5);
        ImGui.TableSetupColumn("Eligibility", ImGuiTableColumnFlags.None, 0, 6);
        ImGui.TableSetupColumn("Period", ImGuiTableColumnFlags.None, 0, 7);
        ImGui.TableSetupColumn("Reported status", ImGuiTableColumnFlags.None, 0, 11);
        ImGui.TableSetupColumn("Entries", ImGuiTableColumnFlags.None, 0, 8);
        ImGui.TableSetupColumn("Period ends", ImGuiTableColumnFlags.None, 0, 9);
        ImGui.TableSetupColumn("Last observed", ImGuiTableColumnFlags.None, 0, 10);
        ImGui.TableHeadersRow();

        var sortSpecs = ImGui.TableGetSortSpecs();
        if (sortSpecs.SpecsCount > 0)
        {
            var spec = sortSpecs.Specs[0];
            tableSortColumn = (int)spec.ColumnUserID;
            tableSortAscending = spec.SortDirection == ImGuiSortDirection.Ascending;
            sortSpecs.SpecsDirty = false;
        }

        rows = PaissaHousingSorting.Sort(rows, tableSortColumn, tableSortAscending, asOfUtc);

        foreach (var row in rows)
        {
            var plot = row.Plot;
            var snapshot = row.Snapshot;
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(snapshot.World.Name);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(plot.DistrictName);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(plot.WardNumber?.ToString() ?? "Unknown");
            ImGui.TableNextColumn();
            var watchAddress = WatchedPlotPolicy.AddressOf(plot);
            var tracked = watchAddress is not null && watchedPlots.Contains(watchAddress);
            ImGui.TextUnformatted((plot.PlotNumber?.ToString() ?? "Unknown") + (tracked ? " *" : ""));
            var plotHovered = ImGui.IsItemHovered();
            if (BeginPlotContextPopup($"travel-{plot.WorldId}-{plot.DistrictId}-{plot.WardNumber}-{plot.PlotNumber}"))
            {
                DrawTravelAction(new PlotDestination(snapshot.World.Id, snapshot.World.Name,
                    plot.DistrictId, plot.WardNumber, plot.PlotNumber));
                ImGui.Separator();
                DrawWatchAction(snapshot, plot, asOfUtc);
                ImGui.EndPopup();
            }
            else if (plotHovered)
                PlotWatchTooltips.Show(tracked ? "Tracked on shared Dashboard. Right-click to travel or untrack." : "Right-click to travel or track on shared Dashboard.");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(FormatHousingSize(plot.Size));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(plot.Price?.ToString("N0") ?? "Unknown");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(PaissaHousingPolicy.DescribeEligibility(plot.PurchaseSystem));
            ImGui.TableNextColumn();
            var phase = PaissaHousingPolicy.GetPhaseLabel(plot, asOfUtc);
            var displayedDeadline = PaissaHousingPolicy.GetDisplayedPhaseDeadline(plot, asOfUtc);
            var freshness = PaissaHousingPolicy.GetSourceFreshness(plot, asOfUtc);
            ImGui.TextUnformatted(freshness switch
            {
                PaissaSourceFreshness.Stale => $"{phase} · Stale",
                PaissaSourceFreshness.Unknown => $"{phase} · Unknown freshness",
                _ => phase,
            });
            if (ImGui.IsItemHovered())
            {
                PlotWatchTooltips.Show(PaissaHousingPolicy.GetPhaseExplanation(plot, asOfUtc),
                    $"Raw PaissaDB deadline: {FormatDeadline(PaissaApiParser.UnixSecondsToUtc(plot.LottoPhaseUntil))}",
                    $"Global period ends: {FormatDeadline(displayedDeadline)}",
                    $"Source freshness: {freshness}");
            }
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(PaissaHousingPolicy.GetReportedStatusLabel(plot.LottoPhase, plot.LottoPhaseUntil, asOfUtc));
            if (ImGui.IsItemHovered())
            {
                var lines = new List<string> { PaissaHousingPolicy.GetReportedStatusExplanation(plot.LottoPhase, plot.LottoPhaseUntil, asOfUtc) };
                if (PaissaHousingPolicy.GetReportedStatusTiming(plot.LottoPhase, plot.LottoPhaseUntil, asOfUtc) == PaissaReportTiming.Outdated)
                    lines.Add(PaissaHousingPolicy.GetStatusRefreshGuidance(asOfUtc, refreshCooldownUntilUtc, housingRequestPending));
                lines.Add($"Raw API phase: {plot.LottoPhase?.ToString() ?? "Not reported"}");
                lines.Add($"Raw status deadline: {FormatDeadline(PaissaApiParser.UnixSecondsToUtc(plot.LottoPhaseUntil))}");
                lines.Add($"Raw deadline Unix seconds: {plot.LottoPhaseUntil?.ToString() ?? "Not reported"}");
                lines.Add($"Source observed: {FormatUnixSourceTime(plot.LastUpdatedTime)}");
                lines.Add($"Downloaded: {FormatTimestampForDisplay(snapshot.DownloadedAtUtc, includeSeconds: true)}");
                PlotWatchTooltips.Show(lines.ToArray());
            }
            ImGui.TableNextColumn(); ImGui.TextUnformatted(plot.LottoEntries?.ToString() ?? "Not reported");
            if (ImGui.IsItemHovered())
                PlotWatchTooltips.Show(plot.LottoEntries is null
                    ? "PaissaDB has not reported an entrant count. A ward sweep does not necessarily update lottery details."
                    : "PaissaDB-reported entrants. Counts may belong to an earlier period. A ward sweep does not necessarily update lottery details.");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(FormatDeadline(displayedDeadline));
            if (ImGui.IsItemHovered())
            {
                PlotWatchTooltips.Show(PaissaHousingPolicy.GetClosureCountdown(plot, asOfUtc),
                    "Calculated global period end, not a verified bidding deadline for this plot. See Reported status.");
            }
            ImGui.TableNextColumn(); ImGui.TextUnformatted(FormatUnixSourceTime(plot.LastUpdatedTime));
            if (ImGui.IsItemHovered())
            {
                var lines = new List<string>
                {
                    $"{snapshot.World.Name} / {plot.DistrictName}",
                    $"IDs: world={plot.WorldId?.ToString() ?? "Unknown"}, district={plot.DistrictId?.ToString() ?? "Unknown"}",
                    $"API address: ward={plot.ApiWardNumber?.ToString() ?? "Unknown"}, plot={plot.ApiPlotNumber?.ToString() ?? "Unknown"}",
                    $"Raw purchase flags: {plot.PurchaseSystem?.ToString() ?? "Unknown"}; raw phase: {plot.LottoPhase?.ToString() ?? "Unknown"}",
                    $"First seen: {FormatUnixSourceTime(plot.FirstSeenTime)}",
                    "PaissaDB lists plots reported for sale; continued vacancy is not guaranteed. Last observed can reflect a ward sweep without refreshed lottery details. Download time is separate.",
                    $"Downloaded: {FormatTimestampForDisplay(snapshot.DownloadedAtUtc, includeSeconds: true)}",
                };
                if (snapshot.Warnings.Count > 0)
                    lines.Add($"World response has {snapshot.Warnings.Count} schema warning(s).");
                PlotWatchTooltips.Show(lines.ToArray());
            }
        }

        ImGui.EndTable();
        var worldLabel = selectedHousingWorldIds.Count == 1 ? "world" : "worlds";
        var summary = $"{rows.Count} plots · {selectedHousingWorldIds.Count} {worldLabel}";
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f,
            ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(summary).X));
        ImGui.TextUnformatted(summary);
    }

    private static string FormatHousingSize(int? size) => size switch
    {
        0 => "Small",
        1 => "Medium",
        2 => "Large",
        null => "Unknown",
        _ => $"Unknown ({size})",
    };

    private string FormatDeadline(DateTimeOffset? deadline)
        => deadline is { } timestamp ? FormatTimestampForDisplay(timestamp) : "Unknown";

    private string FormatUnixSourceTime(double? timestamp)
        => PaissaApiParser.UnixSecondsToUtc(timestamp) is { } utc
            ? FormatTimestampForDisplay(utc)
            : "Unknown";

    private string FormatTimestampForDisplay(DateTimeOffset utc, bool includeSeconds = false)
    {
        if (configuration.DisplayLotteryTimesInUtc)
            return utc.ToUniversalTime().ToString(includeSeconds ? "MMM d HH:mm:ss 'UTC'" : "MMM d HH:mm 'UTC'");

        return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.Local)
            .ToString(includeSeconds ? "MMM d HH:mm:ss" : "MMM d HH:mm");
    }

    private static void DrawPlaceholder()
        => ImGui.TextWrapped("This section is planned but not implemented yet. PlotWatch will not display fabricated housing or lottery data.");

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
                if (tab == PlotWatchTab.Sweeps)
                    continue;
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
        if (ImGui.Checkbox("Display times in UTC", ref displayUtc))
        {
            configuration.DisplayLotteryTimesInUtc = displayUtc;
            saveConfiguration();
        }

        ImGui.Spacing();
        DrawBidStorageStatus();
        DrawCharacterManagement();
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
