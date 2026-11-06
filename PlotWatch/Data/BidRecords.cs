using PlotWatch.Lottery;

namespace PlotWatch.Data;

public enum BidOutcome { Pending, Won, Lost, Claimed }
public enum BidPurchaseType { Personal, FreeCompany, Unknown }
public enum BidFieldSource { Manual, Calculated, GameObserved }

public sealed record TrackedCharacter(ulong ContentId, string Name, int HomeWorldId, string HomeWorldName);
public sealed record BidTimestamp(DateTimeOffset Value, BidFieldSource Source);
public sealed record BidMessageEvidence(DateTimeOffset ObservedAtUtc, string Language, string TimeZoneId,
    int DateFormat, string TemplateHash, string RenderedText);
public sealed record BidCacheEvidence(DateTimeOffset FetchedAtUtc, double? ReportUpdatedAtUnix,
    int? Size, long? Price);
public sealed record PendingBidCapture(TrackedCharacter Character, BidRecord Bid);

public sealed record BidRecord
{
    public Guid Id { get; init; }
    public ulong CharacterContentId { get; init; }
    public int WorldId { get; init; }
    public string WorldName { get; init; } = "";
    public int DistrictId { get; init; }
    public int Ward { get; init; }
    public int Plot { get; init; }
    public int? Size { get; init; }
    public BidPurchaseType PurchaseType { get; init; } = BidPurchaseType.Unknown;
    public long? CycleId { get; init; }
    public int? TicketNumber { get; init; }
    public BidTimestamp? EnteredAt { get; init; }
    public BidTimestamp? ResultsAt { get; init; }
    public BidTimestamp? ClaimDeadline { get; init; }
    public BidOutcome Outcome { get; init; }
    public BidFieldSource DetailsSource { get; init; } = BidFieldSource.Manual;
    public BidFieldSource OutcomeSource { get; init; } = BidFieldSource.Manual;
    public BidMessageEvidence? MessageEvidence { get; init; }
    public BidCacheEvidence? CacheEvidence { get; init; }
    public long? Price { get; init; }
    public bool SizeFromCache { get; init; }
    public BidFieldSource? TicketSource { get; init; }
}

public sealed record BidDocument
{
    public const int CurrentVersion = 2;
    public int Version { get; init; } = CurrentVersion;
    public IReadOnlyList<TrackedCharacter> Characters { get; init; } = [];
    public IReadOnlyList<BidRecord> Bids { get; init; } = [];
    public IReadOnlyList<ulong> ExcludedCharacters { get; init; } = [];
}

public static class BidPolicy
{
    private static readonly int[] DistrictIds = [339, 340, 341, 641, 979];

    public static string? Validate(BidRecord bid)
    {
        if (bid.Id == Guid.Empty || bid.CharacterContentId == 0) return "Choose a tracked character.";
        if (bid.WorldId <= 0 || string.IsNullOrWhiteSpace(bid.WorldName)) return "Choose a valid world.";
        if (!DistrictIds.Contains(bid.DistrictId)) return "Choose a housing district.";
        if (bid.Ward is < 1 or > 30 || bid.Plot is < 1 or > 60) return "Ward must be 1–30; plot must be 1–60.";
        if (bid.Size is < 0 or > 2) return "Size must be unknown, small, medium, or large.";
        if (bid.TicketNumber is <= 0) return "Ticket number must be positive or unknown.";
        if (bid.Price is < 0 || (bid.SizeFromCache && (bid.Size is null || bid.CacheEvidence?.Size != bid.Size)) ||
            (bid.Price is not null && bid.CacheEvidence?.Price != bid.Price)) return "Invalid cached bid enrichment.";
        if (bid.CacheEvidence is { } cache && (cache.Size is < 0 or > 2 || cache.Price is < 0 ||
            (cache.ReportUpdatedAtUnix is { } updated && !double.IsFinite(updated)))) return "Invalid cache evidence.";
        if (bid.TicketSource is { } ticketSource && ticketSource is not (BidFieldSource.Manual or BidFieldSource.GameObserved))
            return "Invalid ticket provenance.";
        if (bid.MessageEvidence is { } evidence && (evidence.RenderedText is null || evidence.RenderedText.Length is 0 or > 4096 ||
            string.IsNullOrWhiteSpace(evidence.Language) || string.IsNullOrWhiteSpace(evidence.TimeZoneId) ||
            evidence.TemplateHash is null || evidence.TemplateHash.Length != 64 || evidence.DateFormat is < 0 or > 3))
            return "Invalid system-message evidence.";
        if (!Enum.IsDefined(bid.PurchaseType) || !Enum.IsDefined(bid.Outcome) ||
            !Enum.IsDefined(bid.DetailsSource) || bid.DetailsSource == BidFieldSource.Calculated ||
            !Enum.IsDefined(bid.OutcomeSource) || bid.OutcomeSource == BidFieldSource.Calculated)
            return "Invalid bid type, outcome, or provenance.";
        foreach (var timestamp in new[] { bid.EnteredAt, bid.ResultsAt, bid.ClaimDeadline })
            if (timestamp is not null && (!Enum.IsDefined(timestamp.Source) || timestamp.Source == BidFieldSource.Calculated))
                return "Recorded timestamps must be manual or game-observed; estimates are calculated from the cycle.";
        if (bid.ResultsAt is { } results && bid.ClaimDeadline is { } claim && claim.Value <= results.Value)
            return "Claim deadline must be after results start.";
        try
        {
            if (bid.CycleId is { } cycle) _ = GetCalculatedDates(cycle);
            if (bid.CycleId is { } knownCycle && bid.EnteredAt is { } entry &&
                LotterySchedule.GetSnapshot(entry.Value).CycleId != knownCycle)
                return "Entry timestamp and cycle reference belong to different lottery cycles.";
            var dates = GetDates(bid);
            if (dates.Results is { } effectiveResults && dates.Claim is { } effectiveClaim && effectiveClaim.Value <= effectiveResults.Value)
                return "Claim deadline must be after results start.";
            if (bid.EnteredAt is { } entered && dates.Results is { } resultsStart && entered.Value >= resultsStart.Value)
                return "Entry timestamp must be before results start.";
        }
        catch (ArgumentOutOfRangeException) { return "Lottery cycle is outside supported dates."; }
        catch (OverflowException) { return "Lottery cycle is outside supported dates."; }
        return null;
    }

    public static bool IsDuplicate(BidRecord left, BidRecord right)
        => left.Id != right.Id && left.CharacterContentId == right.CharacterContentId &&
           left.CycleId == right.CycleId && left.WorldId == right.WorldId &&
           left.DistrictId == right.DistrictId && left.Ward == right.Ward && left.Plot == right.Plot &&
           left.PurchaseType == right.PurchaseType;

    public static bool IsActive(BidRecord bid) => bid.Outcome is BidOutcome.Pending or BidOutcome.Won;

    public static (BidTimestamp? Results, BidTimestamp? Claim) GetDates(BidRecord bid)
    {
        var calculated = bid.CycleId is { } cycle ? GetCalculatedDates(cycle) : (null, null);
        return (bid.ResultsAt ?? calculated.Item1, bid.ClaimDeadline ?? calculated.Item2);
    }

    public static (BidTimestamp Results, BidTimestamp Claim) GetCalculatedDates(long cycle)
    {
        var start = LotterySchedule.ApplicationAnchorUtc.AddTicks(checked(cycle * LotterySchedule.CycleLength.Ticks));
        return (new(start + LotterySchedule.ApplicationLength, BidFieldSource.Calculated),
            new(start + LotterySchedule.CycleLength, BidFieldSource.Calculated));
    }

    public static string? ValidateDocument(BidDocument data)
    {
        if (data.Characters is null || data.Bids is null || data.ExcludedCharacters is null)
            return "Bid data contains missing collections.";
        if (data.Characters.Any(character => character is null || character.ContentId == 0 ||
                string.IsNullOrWhiteSpace(character.Name) || character.HomeWorldId <= 0 || string.IsNullOrWhiteSpace(character.HomeWorldName)) ||
            data.Characters.Select(character => character.ContentId).Distinct().Count() != data.Characters.Count)
            return "Bid data contains invalid or duplicate characters.";
        if (data.ExcludedCharacters.Any(id => id == 0) || data.ExcludedCharacters.Distinct().Count() != data.ExcludedCharacters.Count)
            return "Bid data contains invalid exclusions.";
        if (data.Bids.Any(bid => bid is null || Validate(bid) is not null ||
                !data.Characters.Any(character => character.ContentId == bid.CharacterContentId)) ||
            data.Bids.Select(bid => bid.Id).Distinct().Count() != data.Bids.Count)
            return "Bid data contains invalid, orphaned, or duplicate record identifiers.";
        return null;
    }
}
