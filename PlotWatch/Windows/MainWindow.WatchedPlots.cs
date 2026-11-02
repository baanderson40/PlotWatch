using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using PlotWatch.Data;
using PlotWatch.Paissa;
using PlotWatch.Travel;

namespace PlotWatch.Windows;

public sealed partial class MainWindow
{
    private readonly WatchedPlotTracker watchedPlots;
    private Vector2 watchedPopupPosition;

    private void DrawWatchAction(PaissaHousingWorldData snapshot, PaissaPlotListing plot, DateTimeOffset now)
    {
        var address = WatchedPlotPolicy.AddressOf(plot);
        var tracked = address is not null && watchedPlots.Contains(address);
        var enabled = !watchedPlots.IsReadOnly && address is not null && address.WorldId == snapshot.World.Id;
        if (ImGui.MenuItem(tracked ? "Untrack plot" : "Track plot", "", false, enabled))
        {
            if (tracked) watchedPlots.Untrack(address!);
            else watchedPlots.Track(snapshot, plot, now);
        }
        if (watchedPlots.IsReadOnly) DrawPlotMenuNote("Watchlist is read-only; see Dashboard for recovery.");
        else if (address is null) DrawPlotMenuNote("Tracking needs a valid world, district, ward, and plot.");
        if (watchedPlots.Error is { } error) DrawPlotMenuNote(error);
        DrawPlotMenuNote("Shared across characters. Tracking is not a bid record.");
    }

    private void DrawWatchedPlots(DateTimeOffset now)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted($"Tracked plots ({watchedPlots.Data.Plots.Count}) — shared across characters");
        if (watchedPlots.Error is { } error) ImGui.TextWrapped(error);
        if (watchedPlots.IsReadOnly) ImGui.TextWrapped("Watchlist is read-only. Bids, settings, and the API cache are separate files.");
        if (watchedPlots.CanRestoreBackup && ImGui.SmallButton("Restore watchlist backup..."))
            ImGui.OpenPopup("Restore watchlist backup");
        if (ImGui.BeginPopupModal("Restore watchlist backup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("Restore displayed watchlist backup? The unreadable original will be preserved in a separate file.");
            if (watchedPlots.Error is { } recoveryError) ImGui.TextWrapped(recoveryError);
            if (ImGui.Button("Restore backup##watch") && watchedPlots.RestoreBackup())
            {
                foreach (var world in housingCache.Values) watchedPlots.ObserveWorld(world);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel##watch")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        if (watchedPlots.Data.Plots.Count == 0)
        {
            ImGui.TextWrapped("Right-click a plot number in Available Houses to track it.");
            return;
        }

        foreach (var group in WatchedPlotPolicy.GroupByWorld(watchedPlots.Data.Plots, housingCache))
        {
            ImGui.PushID(group.WorldId);
            if (ImGui.Button($"{group.WorldName} ({group.Plots.Count}) ▾###watched-world-dropdown"))
            {
                watchedPopupPosition = new(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y);
                ImGui.OpenPopup("watched-world-popup");
            }
            if (ImGui.IsItemHovered()) PlotWatchTooltips.Show("Open tracked plots for this server. Click outside the popup or press Escape to close.");
            if (ImGui.IsPopupOpen("watched-world-popup"))
            {
                var viewport = ImGui.GetWindowViewport();
                var maximumWidth = Math.Max(1f, viewport.WorkSize.X - 2f * ImGui.GetStyle().WindowPadding.X);
                ImGui.SetNextWindowPos(watchedPopupPosition, ImGuiCond.Appearing);
                ImGui.SetNextWindowSizeConstraints(new Vector2(Math.Min(450f * ImGuiHelpers.GlobalScale, maximumWidth), 0),
                    new Vector2(maximumWidth, Math.Max(ImGui.GetFrameHeightWithSpacing(), viewport.WorkSize.Y * 0.65f)));
            }
            if (ImGui.BeginPopup("watched-world-popup", ImGuiWindowFlags.HorizontalScrollbar))
            {
                var viewport = ImGui.GetWindowViewport();
                var maximumPosition = Vector2.Max(viewport.WorkPos, viewport.WorkPos + viewport.WorkSize - ImGui.GetWindowSize());
                ImGui.SetWindowPos(Vector2.Clamp(watchedPopupPosition, viewport.WorkPos, maximumPosition));
                ImGui.TextUnformatted($"{group.WorldName} — tracked plots ({group.Plots.Count})");
                DrawWatchedWorldTable(group, now);
                ImGui.EndPopup();
            }
            ImGui.PopID();
        }
    }

    private void DrawWatchedWorldTable(WatchedPlotGroup group, DateTimeOffset now)
    {
        // Fit actual row heights; the bounded popup handles scrolling, not individual tables.
        if (!ImGui.BeginTable("##watched-plots", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
            ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoHostExtendX | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable)) return;
        ImGui.TableSetupColumn("Address", ImGuiTableColumnFlags.DefaultSort, 0, (uint)WatchedPlotSortColumn.Address);
        ImGui.TableSetupColumn("Size / price", ImGuiTableColumnFlags.None, 0, (uint)WatchedPlotSortColumn.SizePrice);
        ImGui.TableSetupColumn("Entrants", ImGuiTableColumnFlags.None, 0, (uint)WatchedPlotSortColumn.Entrants);
        ImGui.TableSetupColumn("Reported status", ImGuiTableColumnFlags.None, 0, (uint)WatchedPlotSortColumn.ReportedStatus);
        ImGui.TableSetupColumn("Cache age", ImGuiTableColumnFlags.None, 0, (uint)WatchedPlotSortColumn.CacheAge);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.NoSort);
        ImGui.TableHeadersRow();
        var sortColumn = WatchedPlotSortColumn.Address;
        var ascending = true;
        var specs = ImGui.TableGetSortSpecs();
        if (specs.SpecsCount > 0)
        {
            sortColumn = (WatchedPlotSortColumn)specs.Specs[0].ColumnUserID;
            ascending = specs.Specs[0].SortDirection == ImGuiSortDirection.Ascending;
            specs.SpecsDirty = false;
        }
        foreach (var row in WatchedPlotSorting.Sort(group.Plots, housingCache, sortColumn, ascending, now))
        {
            var watch = row.Watch;
            var address = watch.Address;
            housingCache.TryGetValue(address.WorldId, out var world);
            var view = row.View;
            var plot = view.Listing;
            ImGui.PushID($"watch-{address.WorldId}-{address.DistrictId}-{address.Ward}-{address.Plot}");
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{world?.World.Name ?? watch.WorldName} / {DistrictLabel(address.DistrictId)} / W{address.Ward} P{address.Plot}");
            if (ImGui.IsItemHovered())
            {
                PlotWatchTooltips.Show($"Tracked: {FormatTimestampForDisplay(watch.TrackedAtUtc, includeSeconds: true)}\n" +
                    "Shared watchlist only; this does not indicate a lottery entry. Untracking does not cancel travel.");
            }
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{FormatHousingSize(plot?.Size)} / {plot?.Price?.ToString("N0") ?? "Not reported"}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(plot?.LottoEntries?.ToString() ?? "Not reported");
            if (ImGui.IsItemHovered()) PlotWatchTooltips.Show(view.Historical
                ? "Last-known reported entrant count, not a current count. Not reported does not mean zero."
                : "PaissaDB-reported count; may belong to an earlier period. Not reported does not mean zero.");
            ImGui.TableNextColumn();
            if (view.Availability == WatchedPlotAvailability.Listed && plot is not null)
                ImGui.TextUnformatted(PaissaHousingPolicy.GetReportedStatusLabel(plot.LottoPhase, plot.LottoPhaseUntil, now));
            else
                ImGui.TextUnformatted(view.Availability == WatchedPlotAvailability.NotInLatestListings
                    ? "Not in latest listings" : view.CacheOlderThanSaved ? "Waiting for newer cached data" : "Waiting for cached data");
            if (ImGui.IsItemHovered())
            {
                var lines = new List<string>();
                if (view.Historical) lines.Add("Current plot status is unverified. Absence from listings does not prove sold or claimed. " +
                    (plot is null ? "No last-known listing saved." : "Other displayed details are last known."));
                if (plot is not null)
                {
                    lines.Add(view.Historical ? $"Last-known report: {PaissaHousingPolicy.GetReportedStatusLabel(plot.LottoPhase)}."
                        : PaissaHousingPolicy.GetReportedStatusExplanation(plot.LottoPhase, plot.LottoPhaseUntil, now));
                    lines.Add($"Raw report deadline: {FormatDeadline(PaissaApiParser.UnixSecondsToUtc(plot.LottoPhaseUntil))}");
                    lines.Add($"Source observed: {FormatUnixSourceTime(plot.LastUpdatedTime)}");
                }
                if (watch.LastKnown is { } known)
                    lines.Add($"Last-known listing downloaded: {FormatTimestampForDisplay(known.DownloadedAtUtc, includeSeconds: true)}");
                lines.Add($"Available Houses: {PaissaHousingPolicy.GetStatusRefreshGuidance(now, refreshCooldownUntilUtc, housingRequestPending)}");
                if (housingWorldStatuses.TryGetValue(address.WorldId, out var status)) lines.Add(status);
                PlotWatchTooltips.Show(lines.ToArray());
            }
            if (view.Historical && plot is not null) ImGui.TextDisabled("Details: last known");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(view.DownloadedAtUtc is { } downloaded ? FormatAge(downloaded, now) : "No cache loaded");
            if (ImGui.IsItemHovered() && view.DownloadedAtUtc is { } download)
                PlotWatchTooltips.Show($"World snapshot downloaded: {FormatTimestampForDisplay(download, includeSeconds: true)}. Download age is not source observation age.");
            ImGui.TableNextColumn();
            var destination = new PlotDestination(address.WorldId, world?.World.Name ?? watch.WorldName, address.DistrictId, address.Ward, address.Plot);
            var reason = plotTravel.GetDisabledReason(destination);
            ImGui.BeginDisabled(reason is not null);
            if (ImGui.SmallButton("Travel to")) requestTravel(destination);
            ImGui.EndDisabled();
            if (reason is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) PlotWatchTooltips.Show(reason);
            ImGui.SameLine();
            ImGui.BeginDisabled(watchedPlots.IsReadOnly);
            if (ImGui.SmallButton("Untrack") && watchedPlots.Untrack(address) &&
                !watchedPlots.Data.Plots.Any(item => item.Address.WorldId == group.WorldId))
                ImGui.CloseCurrentPopup();
            ImGui.EndDisabled();
            ImGui.PopID();
        }
        ImGui.EndTable();
    }
}
