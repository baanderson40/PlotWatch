using PlotWatch.Data;
using PlotWatch.Lottery;
using PlotWatch.Paissa;

internal static class PaissaStatusTests
{
    public static readonly (string Name, Action Test)[] Cases =
    [
        ("reported plot status stays separate from global period and entrants", SeparateStatus),
        ("reported status expiry uses exact boundary and preserves unverified timing", Expiry),
        ("reported status filters intersect period and outdated selections", Filters),
        ("reported status sorting uses stable new ID and keeps unknowns last", Sorting),
        ("status filter migration preserves preferences and runs once across reload", Migration),
        ("expired status prompts refresh without claiming current bidding availability", ExpiredRefreshGuidance),
    ];
    private static readonly DateTimeOffset Now = LotterySchedule.ApplicationAnchorUtc.AddHours(6);
    private static PaissaPlotListing Plot => new(65, 339, "Mist", 4, 10, 5, 11, 0, 3187500, 3,
        null, null, null, null, Now.ToUnixTimeSeconds(), null, null);
    private static PaissaHousingFilters All => new(new HashSet<int?> { 339 }, new HashSet<int?> { 0 },
        Enum.GetValues<PaissaEligibilityFilter>().ToHashSet(), Enum.GetValues<PaissaPhaseFilter>().ToHashSet(),
        Enum.GetValues<PaissaStatusFilter>().ToHashSet());
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}.");
    }

    private static void SeparateStatus()
    {
        foreach (var (phase, label) in new (int?, string)[]
        {
            (null, "Not reported"), (1, "Accepting entries"), (2, "Results"), (3, "Bidding unavailable"), (0, "Unrecognized"), (99, "Unrecognized"),
        })
        {
            var plot = Plot with { LottoPhase = phase, LottoPhaseUntil = Now.AddDays(8).ToUnixTimeSeconds() };
            var original = plot with { };
            Equal("Entry", PaissaHousingPolicy.GetPhaseLabel(plot, Now));
            Equal(label, PaissaHousingPolicy.GetReportedStatusLabel(plot.LottoPhase, plot.LottoPhaseUntil, Now));
            Equal<DateTimeOffset?>(LotterySchedule.ApplicationAnchorUtc.AddDays(5), PaissaHousingPolicy.GetDisplayedPhaseDeadline(plot, Now));
            Equal<long?>(null, plot.LottoEntries);
            Equal(original, plot);
            Equal<long?>(0, (plot with { LottoEntries = 0 }).LottoEntries);
        }
        // Latest Midgardsormr observation and Faerie's unavailable plot are both valid during Entry.
        var midgard = Plot with { LottoPhase = 1, LottoEntries = 0, LottoPhaseUntil = LotterySchedule.ApplicationAnchorUtc.AddDays(5).ToUnixTimeSeconds() };
        var faerie = midgard with { WorldId = 54, DistrictId = 341, DistrictName = "The Goblet", WardNumber = 25, PlotNumber = 54,
            LottoPhase = 3, LottoPhaseUntil = LotterySchedule.ApplicationAnchorUtc.AddDays(9).ToUnixTimeSeconds() };
        Equal("Accepting entries", PaissaHousingPolicy.GetReportedStatusLabel(midgard.LottoPhase));
        Equal("Bidding unavailable", PaissaHousingPolicy.GetReportedStatusLabel(faerie.LottoPhase, faerie.LottoPhaseUntil, Now));
        Equal("Entry", PaissaHousingPolicy.GetPhaseLabel(faerie, Now));
        // Global Results cannot overwrite a contradictory API report, either.
        Equal("Accepting entries", PaissaHousingPolicy.GetReportedStatusLabel(midgard.LottoPhase));
        Equal("Results", PaissaHousingPolicy.GetPhaseLabel(midgard, LotterySchedule.ApplicationAnchorUtc.AddDays(6)));
    }

    private static void Expiry()
    {
        var deadline = Now.ToUnixTimeSeconds();
        Equal(PaissaReportTiming.NotExpired, PaissaHousingPolicy.GetReportedStatusTiming(3, deadline, Now.AddTicks(-1)));
        Equal(PaissaReportTiming.Outdated, PaissaHousingPolicy.GetReportedStatusTiming(3, deadline, Now));
        Equal("Status outdated — refresh cache", PaissaHousingPolicy.GetReportedStatusLabel(3, deadline, Now));
        Equal("Status outdated — refresh cache", PaissaHousingPolicy.GetReportedStatusLabel(1, deadline, Now.AddDays(1)));
        Equal(PaissaReportTiming.Outdated, PaissaHousingPolicy.GetReportedStatusTiming(3, deadline, Now.ToOffset(TimeSpan.FromHours(-6))));
        foreach (var missing in new long?[] { null, long.MaxValue, long.MinValue })
        {
            Equal(PaissaReportTiming.Unverified, PaissaHousingPolicy.GetReportedStatusTiming(3, missing, Now));
            Equal("Bidding unavailable", PaissaHousingPolicy.GetReportedStatusLabel(3, missing, Now));
            Equal(true, PaissaHousingPolicy.GetReportedStatusExplanation(3, missing, Now).Contains("timing unverified"));
        }
        Equal("Not reported", PaissaHousingPolicy.GetReportedStatusLabel(null, deadline, Now));
        Equal(PaissaReportTiming.Unverified, PaissaHousingPolicy.GetReportedStatusTiming(null, deadline, Now));
    }

    private static void ExpiredRefreshGuidance()
    {
        var deadline = Now.ToUnixTimeSeconds();
        foreach (var phase in new[] { 1, 2, 3, 99 })
        {
            Equal("Status outdated — refresh cache", PaissaHousingPolicy.GetReportedStatusLabel(phase, deadline, Now));
            var explanation = PaissaHousingPolicy.GetReportedStatusExplanation(phase, deadline, Now);
            Equal(true, explanation.StartsWith($"Previous report: {PaissaHousingPolicy.GetReportedStatusLabel(phase)}."));
            Equal(true, explanation.Contains("current bidding status is unverified"));
            Equal(true, explanation.Contains("may still return an expired report"));
            Equal(true, explanation.Contains("not cache download age"));
        }
        Equal(true, PaissaHousingPolicy.GetStatusRefreshGuidance(Now, Now, false).Contains("available now"));
        Equal("Cache refresh available in 00:02:00.", PaissaHousingPolicy.GetStatusRefreshGuidance(Now, Now.AddMinutes(2), false));
        Equal("Housing cache refresh in progress.", PaissaHousingPolicy.GetStatusRefreshGuidance(Now, Now.AddMinutes(2), true));
        Equal("Bidding unavailable", PaissaHousingPolicy.GetReportedStatusLabel(3, Now.AddDays(1).ToUnixTimeSeconds(), Now));
        Equal("Not reported", PaissaHousingPolicy.GetReportedStatusLabel(null, deadline, Now));
    }

    private static void Filters()
    {
        var plot = Plot with { LottoPhase = 3, LottoPhaseUntil = Now.AddDays(1).ToUnixTimeSeconds() };
        Equal(true, PaissaHousingPolicy.Matches(plot, All, Now));
        Equal(false, PaissaHousingPolicy.Matches(plot, All with { Statuses = new HashSet<PaissaStatusFilter>() }, Now));
        Equal(false, PaissaHousingPolicy.Matches(plot, All with { Phases = new HashSet<PaissaPhaseFilter> { PaissaPhaseFilter.Results } }, Now));
        foreach (var status in Enum.GetValues<PaissaStatusFilter>())
            Equal(status == PaissaStatusFilter.Unavailable,
                PaissaHousingPolicy.Matches(plot, All with { Statuses = new HashSet<PaissaStatusFilter> { status } }, Now));
        plot = plot with { LottoPhaseUntil = Now.ToUnixTimeSeconds() };
        Equal(true, PaissaHousingPolicy.Matches(plot, All, Now));
        Equal(false, PaissaHousingPolicy.Matches(plot, All with { IncludeOutdatedReports = false }, Now));
        Equal(true, PaissaHousingPolicy.Matches(plot with { LottoPhaseUntil = null }, All with { IncludeOutdatedReports = false }, Now));
        var unknownOnly = All with { Statuses = new HashSet<PaissaStatusFilter> { PaissaStatusFilter.Unknown } };
        Equal(true, PaissaHousingPolicy.Matches(plot with { LottoPhase = null }, unknownOnly, Now));
        Equal(true, PaissaHousingPolicy.Matches(plot with { LottoPhase = 99 }, unknownOnly, Now));
    }

    private static void Sorting()
    {
        var snapshot = new PaissaHousingWorldData(new(65, "Midgardsormr", 1, "Aether"), Now, [], []);
        var rows = new int?[] { null, 3, 2, 1, 99 }.Select((phase, index) =>
            (Snapshot: snapshot, Plot: Plot with { PlotNumber = index + 1, LottoPhase = phase,
                LottoPhaseUntil = Now.AddDays(1).ToUnixTimeSeconds() })).ToArray();
        Equal("4,3,2,1,5", string.Join(",", PaissaHousingSorting.Sort(rows, 11, true, Now).Select(row => row.Plot.PlotNumber)));
        Equal("2,3,4,1,5", string.Join(",", PaissaHousingSorting.Sort(rows, 11, false, Now).Select(row => row.Plot.PlotNumber)));
        foreach (var column in new[] { 7, 9 })
            Equal("1,2,3,4,5", string.Join(",", PaissaHousingSorting.Sort(rows, column, true, Now).Select(row => row.Plot.PlotNumber)));
    }

    private static void Migration()
    {
        foreach (var oldVersion in new[] { 7, 8, 9 })
        {
            var config = new Configuration { Version = oldVersion, HousingWorldIds = [65], HousingDistrictIds = [339],
                HousingSizeMask = 1, HousingEligibilityMask = 2, HousingPhaseMask = 2,
                HousingStatusMask = 0, HousingIncludeOutdatedReports = false, DisplayLotteryTimesInUtc = true };
            config.Normalize();
            Equal(oldVersion < 9 ? 1 : 2, config.HousingPhaseMask);
            Equal(15, config.HousingStatusMask);
            Equal(true, config.HousingIncludeOutdatedReports);
            Equal("65", string.Join(",", config.HousingWorldIds));
            Equal("339", string.Join(",", config.HousingDistrictIds));
            Equal(1, config.HousingSizeMask);
            Equal(2, config.HousingEligibilityMask);
            Equal(true, config.DisplayLotteryTimesInUtc);
            config.HousingStatusMask = 5;
            config.HousingIncludeOutdatedReports = false;
            config = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(Newtonsoft.Json.JsonConvert.SerializeObject(config))!;
            config.Normalize();
            Equal(5, config.HousingStatusMask);
            Equal(false, config.HousingIncludeOutdatedReports);
            config.ResetToDefaults();
            Equal(15, config.HousingStatusMask);
            Equal(true, config.HousingIncludeOutdatedReports);
        }
        var empty = new Configuration { HousingStatusMask = 0 };
        empty.Normalize();
        Equal(0, empty.HousingStatusMask);
    }
}
