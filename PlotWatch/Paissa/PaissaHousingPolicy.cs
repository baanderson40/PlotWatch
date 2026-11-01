using PlotWatch.Lottery;

namespace PlotWatch.Paissa;

public enum PaissaPhaseFilter
{
    Entry,
    Results,
    Unknown,
}

public enum PaissaSourceFreshness
{
    Unknown,
    Current,
    Stale,
}

public enum PaissaStatusFilter { AcceptingEntries, Results, Unavailable, Unknown }
public enum PaissaReportTiming { Unverified, NotExpired, Outdated }

public enum PaissaEligibilityFilter
{
    FreeCompany,
    PersonalAndFreeCompany,
    Personal,
    Unknown,
}

public sealed record PaissaHousingFilters(
    IReadOnlySet<int?> DistrictIds,
    IReadOnlySet<int?> Sizes,
    IReadOnlySet<PaissaEligibilityFilter> Eligibility,
    IReadOnlySet<PaissaPhaseFilter> Phases,
    IReadOnlySet<PaissaStatusFilter>? Statuses = null,
    bool IncludeOutdatedReports = true);

public sealed record PaissaHousingWorldData(
    PaissaWorld World,
    DateTimeOffset DownloadedAtUtc,
    IReadOnlyList<PaissaPlotListing> Listings,
    IReadOnlyList<string> Warnings);

public static class PaissaHousingPolicy
{
    public static PaissaStatusFilter GetReportedStatusFilter(int? phase) => phase switch
    {
        1 => PaissaStatusFilter.AcceptingEntries,
        2 => PaissaStatusFilter.Results,
        3 => PaissaStatusFilter.Unavailable,
        _ => PaissaStatusFilter.Unknown,
    };

    public static string GetReportedStatusLabel(int? phase) => phase switch
    {
        null => "Not reported", 1 => "Accepting entries", 2 => "Results", 3 => "Bidding unavailable", _ => "Unrecognized",
    };

    public static PaissaReportTiming GetReportedStatusTiming(int? phase, long? deadline, DateTimeOffset asOfUtc)
        => phase is null || PaissaApiParser.UnixSecondsToUtc(deadline) is not { } time
            ? PaissaReportTiming.Unverified
            : time <= asOfUtc ? PaissaReportTiming.Outdated : PaissaReportTiming.NotExpired;

    public static string GetReportedStatusLabel(int? phase, long? deadline, DateTimeOffset asOfUtc)
        => GetReportedStatusTiming(phase, deadline, asOfUtc) == PaissaReportTiming.Outdated
            ? "Status outdated — refresh cache" : GetReportedStatusLabel(phase);

    public static string GetReportedStatusExplanation(int? phase, long? deadline, DateTimeOffset asOfUtc)
    {
        if (GetReportedStatusTiming(phase, deadline, asOfUtc) == PaissaReportTiming.Outdated)
            return $"Previous report: {GetReportedStatusLabel(phase)}. Its deadline has passed; current bidding status is unverified. " +
                "Refresh the housing cache to check for a newer report. PaissaDB may still return an expired report after refresh. " +
                "This warning concerns the report deadline, not cache download age. Last observed may reflect a ward sweep without refreshed lottery details.";
        var timing = GetReportedStatusTiming(phase, deadline, asOfUtc) switch
        {
            PaissaReportTiming.NotExpired => "Report deadline has not expired; this does not prove recently collected lottery details.",
            _ => "Report timing unverified: status or deadline is missing/invalid.",
        };
        return $"PaissaDB-reported plot status: {GetReportedStatusLabel(phase)}. {timing} " +
            "Global lottery period is separate and does not guarantee this plot accepts bids. Last observed may reflect a ward sweep without refreshed lottery details.";
    }

    public static string GetStatusRefreshGuidance(DateTimeOffset asOfUtc, DateTimeOffset cooldownUntilUtc, bool refreshing)
        => refreshing ? "Housing cache refresh in progress."
            : cooldownUntilUtc > asOfUtc
                ? $"Cache refresh available in {LotterySchedule.FormatRemaining(cooldownUntilUtc - asOfUtc)}."
                : "Cache refresh available now: use the cache refresh control below the filters.";

    public static string GetPhaseLabel(PaissaPlotListing plot, DateTimeOffset asOfUtc)
        => GetPhaseLabel(plot.PurchaseSystem, asOfUtc);

    public static string GetPhaseLabel(int? purchaseSystem, DateTimeOffset asOfUtc)
        => GetPhaseFilter(purchaseSystem, asOfUtc) switch
        {
            PaissaPhaseFilter.Entry => "Entry",
            PaissaPhaseFilter.Results => "Results",
            _ => purchaseSystem is >= 0 and <= 7 ? "Not applicable" : "Unknown",
        };

    public static PaissaPhaseFilter GetPhaseFilter(PaissaPlotListing plot, DateTimeOffset asOfUtc)
        => GetPhaseFilter(plot.PurchaseSystem, asOfUtc);

    public static PaissaPhaseFilter GetPhaseFilter(int? purchaseSystem, DateTimeOffset asOfUtc)
    {
        if (!IsLottery(purchaseSystem)) return PaissaPhaseFilter.Unknown;
        return LotterySchedule.GetSnapshot(asOfUtc).CurrentPhase == LotteryPhase.Application
            ? PaissaPhaseFilter.Entry : PaissaPhaseFilter.Results;
    }

    private static bool IsLottery(int? purchaseSystem)
        => purchaseSystem is >= 0 and <= 7 && (purchaseSystem.Value & 1) != 0;

    public static PaissaSourceFreshness GetSourceFreshness(PaissaPlotListing plot, DateTimeOffset asOfUtc)
    {
        var updated = PaissaApiParser.UnixSecondsToUtc(plot.LastUpdatedTime);
        if (updated is null || updated.Value > asOfUtc.AddMinutes(5))
            return PaissaSourceFreshness.Unknown;

        var nowSchedule = LotterySchedule.GetSnapshot(asOfUtc);
        var updatedSchedule = LotterySchedule.GetSnapshot(updated.Value);
        var nowPhaseIndex = nowSchedule.CycleId * 2 + (nowSchedule.CurrentPhase == LotteryPhase.Results ? 1 : 0);
        var updatedPhaseIndex = updatedSchedule.CycleId * 2 + (updatedSchedule.CurrentPhase == LotteryPhase.Results ? 1 : 0);
        return nowPhaseIndex - updatedPhaseIndex >= 2
            ? PaissaSourceFreshness.Stale
            : PaissaSourceFreshness.Current;
    }

    public static string GetPhaseExplanation(PaissaPlotListing plot, DateTimeOffset asOfUtc)
    {
        var interpretation = IsLottery(plot.PurchaseSystem)
            ? $"Global housing-lottery period: {GetPhaseLabel(plot, asOfUtc)}. This does not confirm continued vacancy or that this plot accepts bids. See Reported status separately."
            : "No lottery period assigned: purchase system is non-lottery, missing, or unrecognized.";
        return $"{interpretation} Raw PaissaDB phase: {PaissaApiParser.DescribeLotteryPhase(plot.LottoPhase)}.";
    }

    public static DateTimeOffset? GetDisplayedPhaseDeadline(PaissaPlotListing plot, DateTimeOffset asOfUtc)
        => GetDisplayedPhaseDeadline(plot.PurchaseSystem, asOfUtc);

    public static DateTimeOffset? GetDisplayedPhaseDeadline(int? purchaseSystem, DateTimeOffset asOfUtc)
        => IsLottery(purchaseSystem) ? LotterySchedule.GetSnapshot(asOfUtc).CurrentPhaseEndsAtUtc : null;

    public static string GetClosureCountdown(PaissaPlotListing plot, DateTimeOffset asOfUtc)
    {
        if (GetDisplayedPhaseDeadline(plot, asOfUtc) is not { } deadline)
            return "Period end is unknown.";
        var remaining = deadline - asOfUtc;
        return remaining > TimeSpan.Zero
            ? $"Period ends in {LotterySchedule.FormatRemaining(remaining)}"
            : "Period end has passed.";
    }

    public static bool Matches(PaissaPlotListing plot, PaissaHousingFilters filters, DateTimeOffset asOfUtc)
    {
        if (!filters.DistrictIds.Contains(plot.DistrictId))
            return false;
        if (!filters.Sizes.Contains(plot.Size))
            return false;
        if (!filters.Eligibility.Contains(GetEligibility(plot.PurchaseSystem)))
            return false;
        return filters.Phases.Contains(GetPhaseFilter(plot, asOfUtc)) &&
            (filters.Statuses is null || filters.Statuses.Contains(GetReportedStatusFilter(plot.LottoPhase))) &&
            (filters.IncludeOutdatedReports || GetReportedStatusTiming(plot.LottoPhase, plot.LottoPhaseUntil, asOfUtc) != PaissaReportTiming.Outdated);
    }

    public static PaissaEligibilityFilter GetEligibility(int? purchaseSystem)
    {
        if (purchaseSystem is null)
            return PaissaEligibilityFilter.Unknown;
        return (purchaseSystem.Value & 6) switch
        {
            2 => PaissaEligibilityFilter.FreeCompany,
            4 => PaissaEligibilityFilter.Personal,
            6 => PaissaEligibilityFilter.PersonalAndFreeCompany,
            _ => PaissaEligibilityFilter.Unknown,
        };
    }

    public static string DescribeEligibility(int? purchaseSystem)
        => GetEligibility(purchaseSystem) switch
        {
            PaissaEligibilityFilter.FreeCompany => "FC",
            PaissaEligibilityFilter.PersonalAndFreeCompany => "Personal & FC",
            PaissaEligibilityFilter.Personal => "Personal",
            _ => "Unknown",
        };
}
