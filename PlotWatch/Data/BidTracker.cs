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
        if (BidPolicy.Validate(bid) is { } validation) return RejectBidEntry(validation);
        if (!Data.Characters.Any(character => character.ContentId == bid.CharacterContentId))
            return RejectBidEntry("Choose a tracked character.");
        var existing = Data.Bids.FirstOrDefault(item => item.Id == bid.Id);
        if (existing is null && Data.ExcludedCharacters.Contains(bid.CharacterContentId))
            return RejectBidEntry("Re-enable this character before adding bids.");
        if (existing is not null)
        {
            bid = bid with
            {
                EnteredAt = PreserveObserved(existing.EnteredAt, bid.EnteredAt),
                ResultsAt = PreserveObserved(existing.ResultsAt, bid.ResultsAt),
                ClaimDeadline = PreserveObserved(existing.ClaimDeadline, bid.ClaimDeadline),
            };
        }
        if (!allowDuplicate && Data.Bids.Any(item => BidPolicy.IsDuplicate(item, bid)))
            return RejectBidEntry("A matching character/cycle/plot/type record exists. Confirm duplicate to save another record.");
        var saved = Commit(Data with { Bids = Data.Bids.Where(item => item.Id != bid.Id).Append(bid).ToArray() });
        if (!saved)
        {
            logBidEntry?.Invoke(false, $"Bid entry save failed: {Error}");
            return false;
        }
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
