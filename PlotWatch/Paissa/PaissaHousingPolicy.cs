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
    IReadOnlySet<PaissaPhaseFilter> Phases);

public sealed record PaissaHousingWorldData(
    PaissaWorld World,
    DateTimeOffset DownloadedAtUtc,
    IReadOnlyList<PaissaPlotListing> Listings,
    IReadOnlyList<string> Warnings);

public static class PaissaHousingPolicy
{
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
            ? $"PW uses the global housing-lottery schedule: {GetPhaseLabel(plot, asOfUtc)}. This does not confirm continued plot vacancy."
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
            return "Closure time is unknown.";
        var remaining = deadline - asOfUtc;
        return remaining > TimeSpan.Zero
            ? $"Closes in {LotterySchedule.FormatRemaining(remaining)}"
            : "Closure time has passed.";
    }

    public static bool Matches(PaissaPlotListing plot, PaissaHousingFilters filters, DateTimeOffset asOfUtc)
    {
        if (!filters.DistrictIds.Contains(plot.DistrictId))
            return false;
        if (!filters.Sizes.Contains(plot.Size))
            return false;
        if (!filters.Eligibility.Contains(GetEligibility(plot.PurchaseSystem)))
            return false;
        return filters.Phases.Contains(GetPhaseFilter(plot, asOfUtc));
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
