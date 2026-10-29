using PlotWatch.Lottery;

namespace PlotWatch.Paissa;

public enum PaissaPhaseFilter
{
    Any,
    Available,
    Results,
    Unavailable,
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
    {
        if (plot.LottoPhase is null)
            return "Unknown";

        var deadline = PaissaApiParser.UnixSecondsToUtc(plot.LottoPhaseUntil);
        if (plot.LottoPhase == 1)
        {
            if (deadline is not { } entryDeadline || entryDeadline > asOfUtc)
                return "Entry";

            return IsEntryInferredAsResults(plot, asOfUtc) ? "Results" : "Unknown";
        }
        if (plot.LottoPhase == 2)
            return "Results";
        if (plot.LottoPhase == 3)
            return "Unavailable";
        return $"Unknown ({plot.LottoPhase})";
    }

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
        if (IsEntryInferredAsResults(plot, asOfUtc))
            return "PaissaDB reported Entry; its deadline passed during this cycle’s Results period.";
        return $"PaissaDB reported {PaissaApiParser.DescribeLotteryPhase(plot.LottoPhase)}.";
    }

    public static DateTimeOffset? GetDisplayedPhaseDeadline(PaissaPlotListing plot, DateTimeOffset asOfUtc)
    {
        if (IsEntryInferredAsResults(plot, asOfUtc))
            return LotterySchedule.GetSnapshot(asOfUtc).Results.EndsAtUtc;
        return PaissaApiParser.UnixSecondsToUtc(plot.LottoPhaseUntil);
    }

    private static bool IsEntryInferredAsResults(PaissaPlotListing plot, DateTimeOffset asOfUtc)
    {
        if (plot.LottoPhase != 1 || PaissaApiParser.UnixSecondsToUtc(plot.LottoPhaseUntil) is not { } deadline || deadline > asOfUtc)
            return false;

        var nowSchedule = LotterySchedule.GetSnapshot(asOfUtc);
        return nowSchedule.CurrentPhase == LotteryPhase.Results &&
            nowSchedule.CycleId == LotterySchedule.GetSnapshot(deadline).CycleId;
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

    public static PaissaPhaseFilter GetPhaseFilter(PaissaPlotListing plot, DateTimeOffset asOfUtc)
    {
        var phase = GetPhaseLabel(plot, asOfUtc);
        if (phase.StartsWith("Available", StringComparison.Ordinal)) return PaissaPhaseFilter.Available;
        if (phase.StartsWith("Results", StringComparison.Ordinal)) return PaissaPhaseFilter.Results;
        if (phase == "Unavailable") return PaissaPhaseFilter.Unavailable;
        return PaissaPhaseFilter.Unknown;
    }
}
