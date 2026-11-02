using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using PlotWatch.Data;
using PlotWatch.Lottery;
using PlotWatch.Travel;

namespace PlotWatch.Windows;

public sealed partial class MainWindow
{
    private bool bidsCurrentCharacterOnly = true;
    private int bidView;
    private bool bidNewestFirst = true;
    private BidRecord? editingBid;
    private bool openBidEditor;
    private string editorWorldName = "";
    private int editorWorldId;
    private int editorDistrict;
    private int editorWard = 1;
    private int editorPlot = 1;
    private int editorSize;
    private int editorPurchase;
    private int editorOutcome;
    private int editorTicket;
    private ulong editorCharacter;
    private string editorEntry = "";
    private string editorCycle = "";
    private string editorResults = "";
    private string editorClaim = "";
    private bool editorAllowDuplicate;
    private string? editorError;
    private Guid? deleteBidId;
    private ulong? removeCharacterId;
    private bool removeAndExclude;

    private string CharacterLabel(ulong id)
    {
        var character = bidTracker.Data.Characters.FirstOrDefault(item => item.ContentId == id);
        return character is null ? "Unknown character" : $"{character.Name} @ {character.HomeWorldName}";
    }

    private string BidDate(BidTimestamp? timestamp)
        => timestamp is null ? "Unknown" : $"{FormatTimestampForDisplay(timestamp.Value)} ({timestamp.Source})";

    private void DrawBidStorageStatus()
    {
        if (bidTracker.Error is { } error) ImGui.TextWrapped(error);
        if (!bidTracker.IsReadOnly) return;
        ImGui.TextWrapped("Bid records are read-only. Settings and the housing cache are separate files.");
        if (bidTracker.CanRestoreBackup && ImGui.Button("Restore bid backup..."))
            ImGui.OpenPopup("Restore bid backup");
        if (ImGui.BeginPopupModal("Restore bid backup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("Restore the displayed backup? The unreadable original will be preserved in a separate file.");
            if (bidTracker.Error is { } recoveryError) ImGui.TextWrapped(recoveryError);
            if (ImGui.Button("Restore backup") && bidTracker.RestoreBackup()) ImGui.CloseCurrentPopup();
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    private void DrawBids()
    {
        DrawBidStorageStatus();
        ImGui.TextWrapped("Manual records only. No chat, placard, or lottery-status capture yet. Outcomes change only when explicitly recorded.");
        ImGui.Checkbox("Current character only", ref bidsCurrentCharacterOnly);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130);
        ImGui.Combo("View", ref bidView, new[] { "All records", "Active", "History" }, 3);
        ImGui.SameLine();
        ImGui.Checkbox("Newest cycle first", ref bidNewestFirst);
        ImGui.BeginDisabled(bidTracker.IsReadOnly || bidTracker.Data.Characters.Count == 0);
        if (ImGui.Button("Add bid")) StartBidEditor(null);
        ImGui.EndDisabled();
        if (bidTracker.Data.Characters.Count == 0)
            ImGui.TextWrapped("Log in to discover a character, or re-enable an excluded character in Settings.");

        var bids = bidTracker.Data.Bids.Where(bid =>
            (!bidsCurrentCharacterOnly || bid.CharacterContentId == bidTracker.CurrentCharacterId) &&
            (bidView == 0 || BidPolicy.IsActive(bid) == (bidView == 1)));
        var ordered = bidNewestFirst
            ? bids.OrderBy(bid => bid.CycleId is null).ThenByDescending(bid => bid.CycleId)
            : bids.OrderBy(bid => bid.CycleId is null).ThenBy(bid => bid.CycleId);
        if (ImGui.BeginTable("##bid-records", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable,
                new Vector2(0, 240)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            foreach (var column in new[] { "Character", "Address", "Type / ticket", "Outcome", "Results", "Claim deadline", "Actions" })
                ImGui.TableSetupColumn(column);
            ImGui.TableHeadersRow();
            foreach (var bid in ordered.ThenBy(bid => bid.WorldName).ThenBy(bid => bid.DistrictId).ThenBy(bid => bid.Ward).ThenBy(bid => bid.Plot).ThenBy(bid => bid.Id).ToArray())
            {
                ImGui.PushID(bid.Id.ToString());
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(CharacterLabel(bid.CharacterContentId));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{bid.WorldName} / {DistrictLabel(bid.DistrictId)} / W{bid.Ward} P{bid.Plot}");
                var addressHovered = ImGui.IsItemHovered();
                if (BeginPlotContextPopup("bid-travel"))
                {
                    DrawTravelAction(new PlotDestination(bid.WorldId, bid.WorldName, bid.DistrictId, bid.Ward, bid.Plot));
                    ImGui.EndPopup();
                }
                else if (addressHovered)
                    PlotWatchTooltips.Show($"Details: {bid.DetailsSource}\nSize: {FormatHousingSize(bid.Size)}\nEntered: {BidDate(bid.EnteredAt)}\nCycle: {bid.CycleId?.ToString() ?? "Unknown"}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{PurchaseLabel(bid.PurchaseType)} / {bid.TicketNumber?.ToString() ?? "Unknown"}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{bid.Outcome} ({bid.OutcomeSource})");
                var dates = BidPolicy.GetDates(bid);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(BidDate(dates.Results));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(BidDate(dates.Claim));
                ImGui.TableNextColumn();
                ImGui.BeginDisabled(bidTracker.IsReadOnly);
                if (ImGui.SmallButton("Edit")) StartBidEditor(bid);
                ImGui.SameLine();
                if (ImGui.SmallButton("Delete")) deleteBidId = bid.Id;
                ImGui.EndDisabled();
                if (ImGui.SmallButton("Travel...")) ImGui.OpenPopup("bid-action-travel");
                if (ImGui.BeginPopup("bid-action-travel"))
                {
                    DrawTravelAction(new PlotDestination(bid.WorldId, bid.WorldName, bid.DistrictId, bid.Ward, bid.Plot));
                    ImGui.EndPopup();
                }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        DrawBidEditor();
        DrawBidDeleteConfirmation();
    }

    private static string DistrictLabel(int id) => HousingDistricts.FirstOrDefault(item => item.Id == id).Name ?? "Unknown";
    private static string PurchaseLabel(BidPurchaseType type) => type == BidPurchaseType.FreeCompany ? "FC" : type.ToString();
    private static string EditDate(BidTimestamp? timestamp) => timestamp?.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "";

    private void StartBidEditor(BidRecord? bid)
    {
        editingBid = bid ?? new BidRecord { Id = Guid.NewGuid() };
        var character = bidTracker.Data.Characters.FirstOrDefault(item => item.ContentId == (bid?.CharacterContentId ?? bidTracker.CurrentCharacterId))
            ?? bidTracker.Data.Characters.FirstOrDefault();
        editorCharacter = character?.ContentId ?? 0;
        editorWorldId = bid?.WorldId ?? character?.HomeWorldId ?? 0;
        editorWorldName = bid?.WorldName ?? character?.HomeWorldName ?? "";
        editorDistrict = Math.Max(0, Array.FindIndex(HousingDistricts, item => item.Id == bid?.DistrictId));
        editorWard = bid?.Ward ?? 1;
        editorPlot = bid?.Plot ?? 1;
        editorSize = bid?.Size is { } size ? size + 1 : 0;
        editorPurchase = (int)(bid?.PurchaseType ?? BidPurchaseType.Personal);
        editorOutcome = (int)(bid?.Outcome ?? BidOutcome.Pending);
        editorTicket = bid?.TicketNumber ?? 0;
        editorEntry = EditDate(bid?.EnteredAt);
        var cycle = bid is null ? lotterySnapshot?.CycleId : bid.CycleId;
        editorCycle = cycle is { } cycleId
            ? LotterySchedule.ApplicationAnchorUtc.AddTicks(checked(cycleId * LotterySchedule.CycleLength.Ticks)).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "";
        editorResults = EditDate(bid?.ResultsAt);
        editorClaim = EditDate(bid?.ClaimDeadline);
        editorAllowDuplicate = false;
        editorError = null;
        openBidEditor = true;
    }

    private void DrawBidEditor()
    {
        if (openBidEditor) { ImGui.OpenPopup("Bid editor"); openBidEditor = false; }
        if (!ImGui.BeginPopupModal("Bid editor", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextWrapped("Dates: UTC, yyyy-MM-dd HH:mm. Blank means unknown. Cycle reference selects a lottery cycle, not an observed entry time.");
        if (ImGui.BeginCombo("Character", CharacterLabel(editorCharacter)))
        {
            foreach (var character in bidTracker.Data.Characters)
                if (ImGui.Selectable($"{character.Name} @ {character.HomeWorldName}", editorCharacter == character.ContentId))
                    editorCharacter = character.ContentId;
            ImGui.EndCombo();
        }
        if (housingWorlds.Count > 0 && ImGui.BeginCombo("World directory", editorWorldName))
        {
            foreach (var world in housingWorlds.OrderBy(item => item.Name))
                if (ImGui.Selectable(world.Name, editorWorldId == world.Id))
                { editorWorldId = world.Id; editorWorldName = world.Name; }
            ImGui.EndCombo();
        }
        ImGui.InputInt("World ID (manual/offline)", ref editorWorldId);
        ImGui.InputText("World name", ref editorWorldName, 100);
        ImGui.Combo("District", ref editorDistrict, HousingDistricts.Select(item => item.Name).ToArray(), HousingDistricts.Length);
        ImGui.InputInt("Ward (1–30)", ref editorWard);
        ImGui.InputInt("Plot (1–60)", ref editorPlot);
        ImGui.Combo("Size", ref editorSize, new[] { "Unknown", "Small", "Medium", "Large" }, 4);
        ImGui.Combo("Purchase type", ref editorPurchase, new[] { "Personal", "FC", "Unknown" }, 3);
        ImGui.InputInt("Ticket (0 = unknown)", ref editorTicket);
        ImGui.InputText("Entered at (UTC, optional)", ref editorEntry, 40);
        ImGui.InputText("Cycle reference (UTC, optional)", ref editorCycle, 40);
        ImGui.InputText("Results start (UTC override)", ref editorResults, 40);
        ImGui.InputText("Claim deadline (UTC override)", ref editorClaim, 40);
        ImGui.TextWrapped("Blank deadline overrides use calculated cycle estimates when a cycle is known. Game-observed fields take precedence over manual edits.");
        ImGui.Combo("Outcome", ref editorOutcome, Enum.GetNames<BidOutcome>(), 4);
        ImGui.Checkbox("Save duplicate deliberately", ref editorAllowDuplicate);
        if (editorError is { } error) ImGui.TextWrapped(error);
        ImGui.BeginDisabled(bidTracker.IsReadOnly);
        if (ImGui.Button("Save bid"))
        {
            if (TryBuildBid(out var bid) && bidTracker.SaveBid(bid!, editorAllowDuplicate))
                ImGui.CloseCurrentPopup();
            else if (editorError is null) editorError = bidTracker.Error;
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private bool TryBuildBid(out BidRecord? bid)
    {
        bid = null;
        editorError = null;
        if (!ParseBidDate(editorEntry, out var entry) || !ParseBidDate(editorCycle, out var cycleReference) ||
            !ParseBidDate(editorResults, out var results) || !ParseBidDate(editorClaim, out var claim))
        {
            editorError = "Dates must use UTC yyyy-MM-dd HH:mm, or be blank.";
            return bidTracker.RejectBidEntry(editorError);
        }
        if (editorTicket < 0)
        {
            editorError = "Ticket must be positive, or 0 for unknown.";
            return bidTracker.RejectBidEntry(editorError);
        }
        bid = editingBid! with
        {
            CharacterContentId = editorCharacter, WorldId = editorWorldId, WorldName = editorWorldName.Trim(),
            DistrictId = HousingDistricts[editorDistrict].Id!.Value, Ward = editorWard, Plot = editorPlot,
            Size = editorSize == 0 ? null : editorSize - 1, PurchaseType = (BidPurchaseType)editorPurchase,
            TicketNumber = editorTicket == 0 ? null : editorTicket, Outcome = (BidOutcome)editorOutcome,
            OutcomeSource = BidFieldSource.Manual,
            DetailsSource = BidFieldSource.Manual,
            CycleId = (cycleReference ?? entry) is { } reference ? LotterySchedule.GetSnapshot(reference.Value).CycleId : null,
            EnteredAt = entry, ResultsAt = results, ClaimDeadline = claim,
        };
        return true;
    }

    private static bool ParseBidDate(string text, out BidTimestamp? timestamp)
    {
        timestamp = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!DateTimeOffset.TryParseExact(text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)) return false;
        timestamp = new(value, BidFieldSource.Manual);
        return true;
    }

    private void DrawBidDeleteConfirmation()
    {
        if (deleteBidId is not null && !ImGui.IsPopupOpen("Delete bid record"))
            ImGui.OpenPopup("Delete bid record");
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + viewport.Size * 0.5f, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        if (!ImGui.BeginPopupModal("Delete bid record", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings)) return;
        ImGui.TextWrapped("Permanently delete this bid record? This cannot be undone from the UI.");
        if (ImGui.Button("Delete record") && deleteBidId is { } id && bidTracker.DeleteBid(id))
        { deleteBidId = null; ImGui.CloseCurrentPopup(); }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) { deleteBidId = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    private void DrawCharacterManagement()
    {
        if (!ImGui.CollapsingHeader("Characters")) return;
        ImGui.TextWrapped("Exclude stops discovery/new records but keeps existing bids. Remove deletes saved bids; the character can return on next login unless excluded.");
        ImGui.BeginDisabled(bidTracker.IsReadOnly);
        var index = 0;
        foreach (var character in bidTracker.Data.Characters.OrderBy(item => item.Name).ThenBy(item => item.HomeWorldName).ToArray())
        {
            ImGui.PushID(index++);
            ImGui.TextUnformatted($"{character.Name} @ {character.HomeWorldName} · {bidTracker.Data.Bids.Count(bid => bid.CharacterContentId == character.ContentId)} bids");
            ImGui.SameLine();
            var excluded = bidTracker.Data.ExcludedCharacters.Contains(character.ContentId);
            if (ImGui.SmallButton(excluded ? "Re-enable" : "Exclude")) bidTracker.SetExcluded(character.ContentId, !excluded);
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove...")) { removeCharacterId = character.ContentId; removeAndExclude = excluded; }
            ImGui.PopID();
        }
        foreach (var id in bidTracker.Data.ExcludedCharacters.Where(id => !bidTracker.Data.Characters.Any(character => character.ContentId == id)).ToArray())
        {
            ImGui.PushID(index++);
            ImGui.TextUnformatted("Removed character · discovery excluded");
            ImGui.SameLine();
            if (ImGui.SmallButton("Allow rediscovery")) bidTracker.SetExcluded(id, false);
            ImGui.PopID();
        }
        ImGui.EndDisabled();
        if (removeCharacterId is not null) ImGui.OpenPopup("Remove character records");
        if (ImGui.BeginPopupModal("Remove character records", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped($"Remove {CharacterLabel(removeCharacterId ?? 0)} and all associated bid records? This cannot be undone from the UI.");
            ImGui.Checkbox("Also exclude future discovery", ref removeAndExclude);
            if (ImGui.Button("Remove character and bids") && removeCharacterId is { } id && bidTracker.RemoveCharacter(id, removeAndExclude))
            { removeCharacterId = null; ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) { removeCharacterId = null; ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
    }

    private void DrawBidSummary(DateTimeOffset now)
    {
        ImGui.Separator();
        var active = bidTracker.Data.Bids.Where(BidPolicy.IsActive).ToArray();
        ImGui.TextUnformatted($"My bids · {active.Count(bid => bid.Outcome == BidOutcome.Pending)} pending · {active.Count(bid => bid.Outcome == BidOutcome.Won)} won / unclaimed");
        if (bidTracker.IsReadOnly) ImGui.TextWrapped("Bid storage needs recovery. Open My Bids for details.");
        var upcoming = active.Select(bid => (Bid: bid, Dates: BidPolicy.GetDates(bid)))
            .Select(item => (item.Bid, Deadline: item.Bid.Outcome == BidOutcome.Won ? item.Dates.Claim : item.Dates.Results))
            .OrderBy(item => item.Deadline is null ? 2 : item.Deadline.Value > now ? 0 : 1)
            .ThenBy(item => item.Deadline?.Value).ThenBy(item => item.Bid.Id)
            .Take(5);
        foreach (var (bid, deadline) in upcoming)
        {
            var status = deadline is null ? "Deadline unknown" : deadline.Value > now
                ? $"In {LotterySchedule.FormatRemaining(deadline.Value - now)}" : "Time reached; outcome unconfirmed";
            if (bid.Outcome == BidOutcome.Won && deadline is not null && deadline.Value <= now) status = "Claim deadline passed; claim status unconfirmed";
            ImGui.TextWrapped($"{CharacterLabel(bid.CharacterContentId)} · {bid.WorldName} / {DistrictLabel(bid.DistrictId)} W{bid.Ward} P{bid.Plot} · {(bid.Outcome == BidOutcome.Won ? "Claim" : "Results")}: {BidDate(deadline)} · {status}");
        }
        if (ImGui.SmallButton("Open My Bids")) requestedTab = PlotWatchTab.Bids;
    }
}
