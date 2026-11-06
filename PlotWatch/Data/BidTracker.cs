namespace PlotWatch.Data;

/// <summary>Save before publishing mutations, so failed writes leave displayed data intact.</summary>
public sealed class BidTracker
{
    private readonly IBidStore store;
    private readonly Action<bool, string>? logBidEntry;
    private ulong suppressedDiscovery;
    public BidDocument Data { get; private set; }
    public bool IsReadOnly { get; private set; }
    public bool CanRestoreBackup { get; private set; }
    public string? Error { get; private set; }
    public ulong? CurrentCharacterId { get; private set; }
    private readonly List<PendingBidCapture> pendingCaptures = [];
    public IReadOnlyList<PendingBidCapture> PendingCaptures => pendingCaptures;
    private readonly List<PendingBidRecovery> pendingResultRecoveries = [];
    public IReadOnlyList<PendingBidRecovery> PendingResultRecoveries => pendingResultRecoveries;
    public string CaptureStatus { get; private set; } = "No successful-entry message observed this session.";

    public void ReportCaptureFailure(string message)
    {
        CaptureStatus = message;
        logBidEntry?.Invoke(false, message);
    }

    public bool SubmitCapture(TrackedCharacter character, BidRecord bid)
    {
        if (TryCapture(character, bid))
        { pendingCaptures.RemoveAll(item => SameCapture(item.Bid, bid)); return true; }
        if (!Data.ExcludedCharacters.Contains(character.ContentId) && suppressedDiscovery != character.ContentId &&
            !pendingCaptures.Any(item => SameCapture(item.Bid, bid)))
        {
            if (pendingCaptures.Count < 32) pendingCaptures.Add(new(character, bid));
            else ReportCaptureFailure("Pending capture limit reached (32). New capture not retained; add it manually. Existing pending captures preserved.");
        }
        return false;
    }

    public bool RetryCapture(Guid id)
    {
        var pending = pendingCaptures.FirstOrDefault(item => item.Bid.Id == id);
        if (pending is null || !TryCapture(pending.Character, pending.Bid)) return false;
        pendingCaptures.Remove(pending);
        return true;
    }

    public void DismissCapture(Guid id) => pendingCaptures.RemoveAll(item => item.Bid.Id == id);

    public bool ObserveResult(TrackedCharacter character, int worldId, string worldName, BidResultMatch result,
        BidResultEvidence evidence, BidAddressEvidence address)
    {
        var characterId = character.ContentId;
        if (characterId == 0 || worldId <= 0 || string.IsNullOrWhiteSpace(worldName) || result.TemplateRow is not (7122 or 7124 or 7128) ||
            evidence.TemplateRow != result.TemplateRow || evidence.RenderedText is null || evidence.RenderedText.Length is 0 or > 4096 ||
            string.IsNullOrWhiteSpace(evidence.Language) || evidence.TemplateHash is null || evidence.TemplateHash.Length != 64 ||
            (result.TemplateRow is 7122 or 7124 ? result.WinningNumber is null or <= 0 : result.WinningNumber is not null) ||
            address.DistrictId is not (339 or 340 or 341 or 641 or 979) || address.Ward is < 1 or > 30 || address.Plot is < 1 or > 60 ||
            address.ObservedAtUtc > evidence.ObservedAtUtc || address.TemplateHash is null || address.TemplateHash.Length != 64 ||
            address.RenderedText is null || address.RenderedText.Length is 0 or > 512 || address.Language != evidence.Language)
        { CaptureStatus = "Invalid result/address evidence; no bid changed."; return false; }
        if (Data.ExcludedCharacters.Contains(characterId) || suppressedDiscovery == characterId)
        { CaptureStatus = "Result recovery ignored: character tracking disabled."; return false; }
        if (result.CycleId is null)
        {
            QueueResultRecovery(character, worldId, worldName, result, evidence, address,
                "Result matched, but displayed deadline did not verify a lottery cycle. Choose cycle during review.");
            return false;
        }

        var candidates = Data.Bids.Where(bid => bid.CharacterContentId == characterId && bid.WorldId == worldId &&
            bid.DistrictId == address.DistrictId && bid.Ward == address.Ward && bid.Plot == address.Plot &&
            bid.CycleId == result.CycleId && (result.TemplateRow != 7122 || bid.TicketNumber == result.WinningNumber)).ToArray();
        if (candidates.Length != 1)
        {
            QueueResultRecovery(character, worldId, worldName, result, evidence, address,
                candidates.Length == 0 ? "No uniquely matching saved bid; review recovery candidate before saving."
                    : "Multiple saved bids match this result; review and link or dismiss.");
            return false;
        }

        var existing = candidates[0];
        if (existing.Outcome is BidOutcome.Claimed or BidOutcome.Won or BidOutcome.Lost)
        { CaptureStatus = "Result dialog matched an already-recorded outcome; manual result preserved."; return false; }
        if (result.TemplateRow == 7124 && existing.Outcome != BidOutcome.Pending)
        { CaptureStatus = "Loss dialog matched a non-pending bid; no outcome changed."; return false; }
        var outcome = result.TemplateRow == 7124 ? BidOutcome.Lost : BidOutcome.Won;
        var nextBid = existing with
        {
            Outcome = outcome,
            OutcomeSource = BidFieldSource.GameObserved,
            Size = existing.Size ?? address.Size,
            WinningNumber = result.WinningNumber,
            ResultEvidence = evidence,
            AddressEvidence = address,
        };
        if (!Commit(Data with { Bids = Data.Bids.Select(bid => bid.Id == existing.Id ? nextBid : bid).ToArray() }))
        {
            CaptureStatus = "Result observed, but outcome save failed. Bid remains unchanged; reopen the result dialog after fixing storage or update manually.";
            return false;
        }
        CaptureStatus = $"Recorded lottery result {outcome} for {existing.WorldName}, district {existing.DistrictId}, W{existing.Ward} P{existing.Plot}; winning number {result.WinningNumber?.ToString() ?? "not displayed"}.";
        logBidEntry?.Invoke(true, CaptureStatus);
        return true;
    }

    private void QueueResultRecovery(TrackedCharacter character, int worldId, string worldName, BidResultMatch result,
        BidResultEvidence evidence, BidAddressEvidence address, string reason)
    {
        var outcome = result.TemplateRow == 7124 ? BidOutcome.Lost : BidOutcome.Won;
        var ticket = result.TemplateRow == 7122 ? result.WinningNumber : null;
        var draft = new BidRecord
        {
            Id = Guid.NewGuid(), CharacterContentId = character.ContentId, WorldId = worldId, WorldName = worldName,
            DistrictId = address.DistrictId, Ward = address.Ward, Plot = address.Plot, Size = address.Size,
            PurchaseType = BidPurchaseType.Unknown, CycleId = result.CycleId, TicketNumber = ticket,
            TicketSource = ticket is null ? null : BidFieldSource.GameObserved,
            Outcome = outcome, OutcomeSource = BidFieldSource.GameObserved, DetailsSource = BidFieldSource.GameObserved,
            WinningNumber = result.WinningNumber, ResultEvidence = evidence, AddressEvidence = address,
        };
        var duplicate = pendingResultRecoveries.FirstOrDefault(item => item.Character.ContentId == character.ContentId &&
            item.Bid.WorldId == worldId && item.Bid.DistrictId == address.DistrictId && item.Bid.Ward == address.Ward &&
            item.Bid.Plot == address.Plot && item.Bid.CycleId == result.CycleId && item.Bid.WinningNumber == result.WinningNumber &&
            item.Bid.Outcome == outcome);
        if (duplicate is not null)
        { CaptureStatus = "Repeated result observation; existing review candidate retained."; return; }
        if (pendingResultRecoveries.Count >= 32)
        { ReportCaptureFailure("Result recovery queue full (32); existing candidates preserved. Add bid manually."); return; }
        pendingResultRecoveries.Add(new(draft.Id, character, draft, reason));
        CaptureStatus = "Result dialog and placard address matched; bid recovery awaits review. No bid created yet.";
    }

    public void DismissResultRecovery(Guid id) => pendingResultRecoveries.RemoveAll(item => item.Id == id);

    private static bool SameCapture(BidRecord left, BidRecord right) =>
        left.CharacterContentId == right.CharacterContentId && left.WorldId == right.WorldId &&
        left.DistrictId == right.DistrictId && left.Ward == right.Ward && left.Plot == right.Plot &&
        left.CycleId == right.CycleId && left.TicketNumber == right.TicketNumber;

    private bool TryCapture(TrackedCharacter character, BidRecord bid)
    {
        bool Fail(string reason) { ReportCaptureFailure(reason); return false; }
        if (character.ContentId != bid.CharacterContentId || bid.MessageEvidence is null || bid.CycleId is null ||
            bid.ResultsAt is null || bid.TicketNumber is null || BidPolicy.Validate(bid) is not null)
            return Fail("Invalid successful-entry capture; manual review required.");
        if (Data.ExcludedCharacters.Contains(character.ContentId) || suppressedDiscovery == character.ContentId)
            return Fail("Successful-entry capture ignored: character tracking disabled.");
        if (IsReadOnly) return Fail("Capture pending: bid storage is read-only. Recover storage, then retry in My Bids.");
        var candidates = Data.Bids.Where(item => item.CharacterContentId == bid.CharacterContentId &&
            item.WorldId == bid.WorldId && item.DistrictId == bid.DistrictId && item.Ward == bid.Ward && item.Plot == bid.Plot &&
            (item.CycleId == bid.CycleId || item.CycleId is null)).ToArray();
        if (candidates.Length > 1) return Fail("Capture pending: multiple matching records. Resolve duplicates manually, then retry.");
        var existing = candidates.SingleOrDefault();
        if (existing is not null)
        {
            if ((existing.CycleId is null && existing.ResultsAt?.Value != bid.ResultsAt.Value) ||
                (existing.TicketNumber is { } ticket && ticket != bid.TicketNumber) ||
                (existing.ResultsAt is { } results && results.Value != bid.ResultsAt.Value))
                return Fail("Capture pending: conflicting ticket, date, or uncertain cycle. Review existing record, then retry.");
            // Repeated delivery must not refresh historical cache evidence or write again.
            if (existing.MessageEvidence is not null && SameCapture(existing, bid))
            { CaptureStatus = "Duplicate successful-entry message ignored."; return true; }
            bid = existing with
            {
                CycleId = existing.CycleId ?? bid.CycleId,
                TicketNumber = existing.TicketNumber ?? bid.TicketNumber,
                TicketSource = existing.TicketNumber is null ? BidFieldSource.GameObserved : existing.TicketSource,
                ResultsAt = existing.ResultsAt ?? bid.ResultsAt,
                Size = existing.Size ?? (existing.CacheEvidence is null ? bid.Size : existing.CacheEvidence.Size),
                SizeFromCache = existing.Size is null ? (existing.CacheEvidence is null ? bid.SizeFromCache : existing.CacheEvidence.Size is not null) : existing.SizeFromCache,
                Price = existing.Price ?? (existing.CacheEvidence is null ? bid.Price : existing.CacheEvidence.Price),
                CacheEvidence = existing.CacheEvidence ?? bid.CacheEvidence,
                MessageEvidence = bid.MessageEvidence,
            };
        }
        var next = Data with
        {
            Characters = Data.Characters.Any(item => item.ContentId == character.ContentId)
                ? Data.Characters : Data.Characters.Append(character).ToArray(),
            Bids = Data.Bids.Where(item => item.Id != bid.Id).Append(bid).ToArray(),
        };
        if (!Commit(next)) return Fail("Capture pending: " + (Error ?? "Save failed; recover storage and retry."));
        CaptureStatus = $"Recorded successful entry: {bid.WorldName}, district {bid.DistrictId}, W{bid.Ward} P{bid.Plot}, ticket {bid.TicketNumber}.";
        logBidEntry?.Invoke(true, CaptureStatus);
        return true;
    }

    public BidTracker(IBidStore store, Action<bool, string>? logBidEntry = null)
    {
        this.store = store;
        this.logBidEntry = logBidEntry;
        var loaded = store.Load();
        Data = loaded.Data;
        IsReadOnly = loaded.ReadOnly;
        Error = loaded.Error;
        CanRestoreBackup = loaded.CanRestoreBackup;
    }

    public bool ObserveCharacter(TrackedCharacter? character)
    {
        CurrentCharacterId = character?.ContentId;
        if (character is null) { suppressedDiscovery = 0; return false; }
        if (suppressedDiscovery != 0 && suppressedDiscovery != character.ContentId) suppressedDiscovery = 0;
        if (IsReadOnly || character.ContentId == 0 || string.IsNullOrWhiteSpace(character.Name) ||
            character.HomeWorldId <= 0 || string.IsNullOrWhiteSpace(character.HomeWorldName) ||
            Data.ExcludedCharacters.Contains(character.ContentId) || suppressedDiscovery == character.ContentId)
            return false;
        var existing = Data.Characters.FirstOrDefault(item => item.ContentId == character.ContentId);
        if (existing == character) return false;
        return Commit(Data with { Characters = Data.Characters.Where(item => item.ContentId != character.ContentId).Append(character).ToArray() });
    }

    public bool SetExcluded(ulong id, bool excluded)
    {
        if (!Commit(Data with { ExcludedCharacters = excluded
            ? Data.ExcludedCharacters.Append(id).Distinct().ToArray()
            : Data.ExcludedCharacters.Where(value => value != id).ToArray() })) return false;
        if (!excluded && suppressedDiscovery == id) suppressedDiscovery = 0;
        return true;
    }

    public bool RemoveCharacter(ulong id, bool exclude)
    {
        var next = Data with
        {
            Characters = Data.Characters.Where(character => character.ContentId != id).ToArray(),
            Bids = Data.Bids.Where(bid => bid.CharacterContentId != id).ToArray(),
            ExcludedCharacters = exclude ? Data.ExcludedCharacters.Append(id).Distinct().ToArray()
                : Data.ExcludedCharacters.Where(value => value != id).ToArray(),
        };
        if (!Commit(next)) return false;
        // Removal alone permits rediscovery on next login, not immediately on the next framework tick.
        if (CurrentCharacterId == id) suppressedDiscovery = id;
        return true;
    }

    public bool SaveBid(BidRecord bid, bool allowDuplicate = false)
    {
        if (IsReadOnly)
        {
            logBidEntry?.Invoke(false, "Bid entry rejected: bid storage is read-only; recovery required.");
            return false;
        }
        if (!Data.Characters.Any(character => character.ContentId == bid.CharacterContentId))
            return RejectBidEntry("Choose a tracked character.");
        var existing = Data.Bids.FirstOrDefault(item => item.Id == bid.Id);
        if (existing is null && Data.ExcludedCharacters.Contains(bid.CharacterContentId))
            return RejectBidEntry("Re-enable this character before adding bids.");
        if (existing is not null)
        {
            var sameLocation = existing.CharacterContentId == bid.CharacterContentId && existing.WorldId == bid.WorldId &&
                existing.DistrictId == bid.DistrictId && existing.Ward == bid.Ward && existing.Plot == bid.Plot && existing.CycleId == bid.CycleId;
            bid = bid with
            {
                EnteredAt = sameLocation ? PreserveObserved(existing.EnteredAt, bid.EnteredAt) : bid.EnteredAt,
                ResultsAt = sameLocation ? PreserveObserved(existing.ResultsAt, bid.ResultsAt) : bid.ResultsAt,
                ClaimDeadline = sameLocation ? PreserveObserved(existing.ClaimDeadline, bid.ClaimDeadline) : bid.ClaimDeadline,
                SizeFromCache = sameLocation && existing.Size == bid.Size && bid.SizeFromCache,
                TicketSource = sameLocation && existing.TicketNumber == bid.TicketNumber ? bid.TicketSource : BidFieldSource.Manual,
                // Moving a manual record must not attribute the old plot's cache/message evidence to the new address.
                Price = sameLocation ? bid.Price : null,
                CacheEvidence = sameLocation ? bid.CacheEvidence : null,
                MessageEvidence = sameLocation ? bid.MessageEvidence : null,
                WinningNumber = sameLocation ? bid.WinningNumber : null,
                ResultEvidence = sameLocation ? bid.ResultEvidence : null,
                AddressEvidence = sameLocation && existing.DistrictId == bid.DistrictId && existing.Ward == bid.Ward && existing.Plot == bid.Plot
                    ? bid.AddressEvidence : null,
            };
        }
        if (BidPolicy.Validate(bid) is { } validation) return RejectBidEntry(validation);
        if (!allowDuplicate && Data.Bids.Any(item => BidPolicy.IsDuplicate(item, bid)))
            return RejectBidEntry("A matching character/cycle/plot/type record exists. Confirm duplicate to save another record.");
        var saved = Commit(Data with { Bids = Data.Bids.Where(item => item.Id != bid.Id).Append(bid).ToArray() });
        if (!saved)
        {
            logBidEntry?.Invoke(false, $"Bid entry save failed: {Error}");
            return false;
        }
        pendingResultRecoveries.RemoveAll(item => item.Id == bid.Id);
        var dates = BidPolicy.GetDates(bid);
        logBidEntry?.Invoke(true,
            $"Bid entry {(existing is null ? "added" : "updated")}: world={bid.WorldId}; district={bid.DistrictId}; ward={bid.Ward}; plot={bid.Plot}; " +
            $"type={bid.PurchaseType}; cycle={bid.CycleId?.ToString() ?? "Unknown"}; ticket={bid.TicketNumber?.ToString() ?? "Unknown"}; " +
            $"outcome={bid.Outcome}; results={dates.Results?.Value.ToString("O") ?? "Unknown"} ({dates.Results?.Source.ToString() ?? "Unknown"}); " +
            $"claim-deadline={dates.Claim?.Value.ToString("O") ?? "Unknown"} ({dates.Claim?.Source.ToString() ?? "Unknown"}); duplicate-override={allowDuplicate}.");
        return true;
    }

    public bool RejectBidEntry(string reason)
    {
        Error = reason;
        logBidEntry?.Invoke(false, $"Bid entry rejected: {reason}");
        return false;
    }

    private static BidTimestamp? PreserveObserved(BidTimestamp? existing, BidTimestamp? proposed)
        => existing?.Source == BidFieldSource.GameObserved && proposed?.Source != BidFieldSource.GameObserved ? existing : proposed;

    public bool DeleteBid(Guid id) => Commit(Data with { Bids = Data.Bids.Where(bid => bid.Id != id).ToArray() });

    public bool RestoreBackup()
    {
        if (!CanRestoreBackup) return false;
        try
        {
            store.RestoreBackup(Data);
            IsReadOnly = CanRestoreBackup = false;
            Error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { Error = "Backup restore failed. Files remain preserved; check directory permissions and retry."; return false; }
    }

    private bool Commit(BidDocument next)
    {
        if (IsReadOnly) return false;
        if (BidPolicy.ValidateDocument(next) is { } error) { Error = error; return false; }
        try
        {
            store.Save(next);
            Data = next;
            Error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { Error = "Bid data could not be saved. Changes were not applied. Check disk space/permissions and retry."; return false; }
    }
}
