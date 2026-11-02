using PlotWatch.Data;
using PlotWatch.Diagnostics;
using PlotWatch.Commands;
using PlotWatch.Windows;
using PlotWatch.Lottery;
using PlotWatch.Paissa;
using System.Net;

var tests = new (string Name, Action Test)[]
{
    ("configuration defaults are safe", ConfigurationDefaults),
    ("configuration normalization migrates and clamps values", ConfigurationNormalization),
    ("configuration normalization repairs invalid tab", InvalidDefaultTab),
    ("removed unknown housing filters migrate out of saved preferences", RemovedUnknownHousingFilters),
    ("configuration migration preserves settings and defaults to local time", TimeDisplayMigration),
    ("reset restores configuration defaults", ResetConfiguration),
    ("reset cancellation does not invoke destructive action", ResetCancellation),
    ("reset confirmation applies and saves defaults", ResetConfirmation),
    ("failed reset save stays pending and reports failure", ResetSaveFailure),
    ("command registration reports current ownership", CommandOwnership),
    ("command replacement is detected and never removed", ReplacedCommandIsPreserved),
    ("command alias conflict is not owned or removed", CommandConflictIsPreserved),
    ("dependency refresh reports its result", DependencyRefreshResult),
    ("diagnostic history is bounded and keeps newest entries", DiagnosticHistoryBound),
    ("clearing history removes retained entries", ClearDiagnosticHistory),
    ("per-action results retain findings independently", DiagnosticRunsRetainFindings),
    ("PaissaDB diagnostic failures preserve last successful data", PaissaDiagnosticsRetainLastGood),
    ("removed Sweeps default migrates without shifting tab values", RemovedSweepsTabMigration),
    ("lottery schedule classifies exact phase boundaries", LotteryBoundaries),
    ("lottery schedule handles pre-anchor dates", LotteryBeforeAnchor),
    ("lottery period cards select active and next occurrences", LotteryPeriodOccurrences),
    ("known October and November schedule dates match anchor", KnownLotteryDates),
    ("Central local display applies daylight-saving transition", CentralDaylightSaving),
    ("local timezone label refreshes its numeric offset", LocalZoneLabel),
    ("lottery countdown formats and recalculates after clock changes", LotteryCountdown),
    ("PaissaDB world list parses identifiers and data centers", PaissaWorldParsing),
    ("PaissaDB inspection reports fields, nulls, missing values, and samples", PaissaInspection),
    ("PaissaDB enum and timestamp decoding preserves unknown values", PaissaValueDecoding),
    ("empty API listing differs from failed request", PaissaEmptyVersusFailure),
    ("malformed API payload is reported as parse failure", PaissaMalformedResponse),
    ("PaissaDB parser keeps every plot and normalizes one-based address", PaissaFullListings),
    ("empty world listings are a valid zero-plot response", PaissaEmptyWorld),
    ("housing lottery phase follows global schedule despite missing or old API reports", PaissaPhaseFreshness),
    ("housing checkbox filters require a checked category match", PaissaHousingFiltersWork),
    ("housing eligibility display omits lottery entry flags", PaissaEligibilityLabels),
    ("housing freshness follows lottery phase age", PaissaCycleFreshness),
    ("housing phase transitions and filters share typed classification", HousingPhaseBoundaries),
    ("housing effective deadlines preserve API reports", HousingDeadlineBoundaries),
    ("housing countdown uses effective close and handles unknown time", HousingClosureCountdown),
    ("housing freshness changes at exact phase boundaries", HousingFreshnessBoundaries),
    ("housing sorting uses effective deadlines in both directions", HousingDeadlineSorting),
    ("housing phase sorting ignores cache download time", HousingPhaseSorting),
    ("housing sort keeps unknowns last and address ties stable", HousingSortingUnknowns),
    ("character selection changes once and preserves manual choices", HousingCharacterChanges),
    ("character selection waits for valid identity and world", HousingCharacterReadiness),
    ("character selection works before directory and across reload", HousingCharacterReload),
    ("housing preferences migrate obsolete bits and reset identity", HousingPreferencesMigration),
    ("housing global phase applies only to recognized lottery purchase flags", HousingPurchaseSystemPhase),
    ("housing phase-filter migration preserves selections and runs once", HousingPhaseFilterMigration),
    ("housing district selections replace defaults on config reload", HousingDistrictReload),
    ("PaissaDB client honors 429 retry-after cooldown", PaissaRetryAfter),
    ("PaissaDB cooldown wait can be cancelled", PaissaCooldownCancellation),
    ("PaissaDB requests use conservative spacing", PaissaRequestSpacing),
    ("PaissaDB cooldown extension delays requests already waiting", PaissaCooldownRace),
}.Concat(BidTests.Cases).Concat(HousingProbeTests.Cases).Concat(TravelTests.Cases).Concat(HousingNativeTests.Cases).Concat(PaissaStatusTests.Cases).Concat(WatchedPlotTests.Cases).Concat(TooltipPlacementTests.Cases).ToArray();

var failures = 0;
foreach (var (name, test) in tests)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

static void ConfigurationDefaults()
{
    var config = new Configuration();
    Equal(Configuration.CurrentVersion, config.Version);
    Equal((int)PlotWatchTab.Dashboard, config.DefaultTab);
    Equal(100f, config.TextScalePercent);
    Equal(true, config.ChatNotificationsEnabled);
    Equal(true, config.ToastNotificationsEnabled);
    Equal(false, config.SoundNotificationsEnabled);
}

static void ConfigurationNormalization()
{
    var config = new Configuration
    {
        Version = 1,
        TextScalePercent = 200f,
    };

    config.Normalize();

    Equal(Configuration.CurrentVersion, config.Version);
    Equal(150f, config.TextScalePercent);

    config.TextScalePercent = 50f;
    config.Normalize();
    Equal(75f, config.TextScalePercent);
}

static void InvalidDefaultTab()
{
    var config = new Configuration { DefaultTab = 999 };
    config.Normalize();
    Equal((int)PlotWatchTab.Dashboard, config.DefaultTab);
}

static void RemovedUnknownHousingFilters()
{
    var config = new Configuration
    {
        HousingDistrictIds = [339, -1],
        HousingSizeMask = 15,
        HousingEligibilityMask = 15,
    };
    config.Normalize();
    Equal(true, config.HousingDistrictIds.SequenceEqual(new[] { 339 }));
    Equal(7, config.HousingSizeMask);
    Equal(7, config.HousingEligibilityMask);
}

static void TimeDisplayMigration()
{
    var config = new Configuration
    {
        Version = 2,
        TextScalePercent = 116f,
        DisplayLotteryTimesInUtc = false,
        ChatNotificationsEnabled = false,
    };

    config.Normalize();

    Equal(Configuration.CurrentVersion, config.Version);
    Equal(116f, config.TextScalePercent);
    Equal(false, config.DisplayLotteryTimesInUtc);
    Equal(false, config.ChatNotificationsEnabled);

    var nextRefresh = DateTimeOffset.UtcNow.AddHours(1);
    config.HousingNextAutomaticRefreshUtc = nextRefresh;
    config.Normalize();
    Equal(nextRefresh, config.HousingNextAutomaticRefreshUtc);
}

static void ResetConfiguration()
{
    var config = new Configuration
    {
        DefaultTab = (int)PlotWatchTab.Sweeps,
        TextScalePercent = 143f,
        ChatNotificationsEnabled = false,
        ToastNotificationsEnabled = false,
        SoundNotificationsEnabled = true,
    };

    config.ResetToDefaults();

    Equal(Configuration.CurrentVersion, config.Version);
    Equal((int)PlotWatchTab.Dashboard, config.DefaultTab);
    Equal(100f, config.TextScalePercent);
    Equal(true, config.ChatNotificationsEnabled);
    Equal(true, config.ToastNotificationsEnabled);
    Equal(false, config.SoundNotificationsEnabled);
    Equal(default(DateTimeOffset), config.HousingNextAutomaticRefreshUtc);
}

static void ResetCancellation()
{
    var config = new Configuration { TextScalePercent = 125f };
    Configuration? persisted = null;
    var reset = new ConfigurationResetConfirmation(() =>
    {
        config.ResetToDefaults();
        persisted = new Configuration
        {
            Version = config.Version,
            DefaultTab = config.DefaultTab,
            TextScalePercent = config.TextScalePercent,
            ChatNotificationsEnabled = config.ChatNotificationsEnabled,
            ToastNotificationsEnabled = config.ToastNotificationsEnabled,
            SoundNotificationsEnabled = config.SoundNotificationsEnabled,
        };
        return null;
    });

    reset.Request();
    reset.Cancel();

    Equal(125f, config.TextScalePercent);
    Equal<Configuration?>(null, persisted);
    Equal(false, reset.IsPending);
}

static void ResetConfirmation()
{
    var config = new Configuration { TextScalePercent = 125f };
    Configuration? persisted = null;
    var reset = new ConfigurationResetConfirmation(() =>
    {
        config.ResetToDefaults();
        persisted = new Configuration
        {
            Version = config.Version,
            DefaultTab = config.DefaultTab,
            TextScalePercent = config.TextScalePercent,
            ChatNotificationsEnabled = config.ChatNotificationsEnabled,
            ToastNotificationsEnabled = config.ToastNotificationsEnabled,
            SoundNotificationsEnabled = config.SoundNotificationsEnabled,
        };
        return null;
    });

    reset.Request();
    Equal(true, reset.IsPending);
    var result = reset.Confirm();

    Equal(true, result.Succeeded);
    Equal(Configuration.CurrentVersion, config.Version);
    Equal(100f, config.TextScalePercent);
    Equal(100f, persisted!.TextScalePercent);
    Equal((int)PlotWatchTab.Dashboard, persisted.DefaultTab);
    Equal(false, reset.IsPending);
}

static void ResetSaveFailure()
{
    var reset = new ConfigurationResetConfirmation(() => throw new IOException("disk unavailable"));
    reset.Request();
    var result = reset.Confirm();

    Equal(false, result.Succeeded);
    Equal(true, reset.IsPending);
    Equal("disk unavailable", reset.LastError);
}

static void CommandOwnership()
{
    var registry = new FakeCommandRegistry();
    var registration = CreateRegistration(registry, "/plotwatch", new object());
    Equal(true, registration.Register());
    Equal("Registered by PlotWatch", registration.GetStatus());

    registration.Dispose();
    Equal(false, registry.Contains("/plotwatch"));
}

static void ReplacedCommandIsPreserved()
{
    var registry = new FakeCommandRegistry();
    var ours = new object();
    var registration = CreateRegistration(registry, "/plotwatch", ours);
    registration.Register();
    registry.Replace("/plotwatch", new object());

    Equal("Replaced by another handler", registration.GetStatus());
    registration.Dispose();

    Equal(true, registry.Contains("/plotwatch"));
}

static void CommandConflictIsPreserved()
{
    var registry = new FakeCommandRegistry();
    registry.Replace("/pw", new object());
    var registration = CreateRegistration(registry, "/pw", new object());

    Equal(false, registration.Register());
    Equal("Conflict (not owned by PlotWatch)", registration.GetStatus());
    registration.Dispose();

    Equal(true, registry.Contains("/pw"));
}

static void DependencyRefreshResult()
{
    var calls = 0;
    var diagnostics = new PluginDiagnostics("Loaded", () =>
    {
        calls++;
        return false;
    }, "test-version", "test-build");

    Equal(false, diagnostics.RefreshDependencies());
    Equal(1, calls);
    Equal("test-version", diagnostics.Version);
    Equal("test-build", diagnostics.BuildId);
}

static void DiagnosticHistoryBound()
{
    var diagnostics = NewDiagnostics();
    for (var i = 0; i < 105; i++)
        diagnostics.Record(DiagnosticSeverity.Debug, $"result-{i}");

    Equal(100, diagnostics.History.Count);
    Equal("result-5", diagnostics.History[0].Message);
    Equal("result-104", diagnostics.History[^1].Message);
    Equal(true, diagnostics.LastCheckLocal.HasValue);
}

static void ClearDiagnosticHistory()
{
    var diagnostics = NewDiagnostics();
    diagnostics.Record(DiagnosticSeverity.Warning, "warning");
    diagnostics.ClearHistory();
    Equal(0, diagnostics.History.Count);
}

static void DiagnosticRunsRetainFindings()
{
    var diagnostics = NewDiagnostics();
    diagnostics.BeginRun("Configuration inspection");
    diagnostics.Record(DiagnosticSeverity.Debug, "schema valid");
    diagnostics.Record(DiagnosticSeverity.Warning, "save blocked");
    diagnostics.CompleteRun("Warning");

    diagnostics.BeginRun("Dependency check");
    diagnostics.Record(DiagnosticSeverity.Debug, "Lifestream detected");
    diagnostics.CompleteRun("Completed");

    Equal(2, diagnostics.Runs.Count);
    Equal("Warning", diagnostics.Runs[0].Status);
    Equal(2, diagnostics.Runs[0].Findings.Count);
    Equal("schema valid", diagnostics.Runs[0].Findings[0].Message);
    Equal("save blocked", diagnostics.Runs[0].Findings[1].Message);
    Equal("Lifestream detected", diagnostics.Runs[1].Findings.Single().Message);
}

static void PaissaDiagnosticsRetainLastGood()
{
    var diagnostics = NewDiagnostics();
    var world = new PaissaWorld(63, "Gilgamesh", 4, "Aether");
    var successTime = DateTimeOffset.UtcNow;
    diagnostics.SetPaissaWorlds([world], "HTTP 200", successTime);
    diagnostics.SetPaissaWorlds(null, "HTTP 503; cached result retained");
    Equal(1, diagnostics.PaissaWorlds.Count);
    Equal("Gilgamesh", diagnostics.PaissaWorlds[0].Name);
    Equal("HTTP 503; cached result retained", diagnostics.PaissaWorldsStatus);
    Equal(successTime, diagnostics.PaissaWorldsLastSuccessUtc);
}

static void RemovedSweepsTabMigration()
{
    var config = new Configuration { DefaultTab = (int)PlotWatchTab.Sweeps };
    config.Normalize();
    Equal((int)PlotWatchTab.Dashboard, config.DefaultTab);
    Equal(5, (int)PlotWatchTab.Settings);
}

static void LotteryBoundaries()
{
    var anchor = LotterySchedule.ApplicationAnchorUtc;
    Equal(LotteryPhase.Application, LotterySchedule.GetSnapshot(anchor).CurrentPhase);
    Equal(LotteryPhase.Application, LotterySchedule.GetSnapshot(anchor + LotterySchedule.ApplicationLength - TimeSpan.FromTicks(1)).CurrentPhase);
    Equal(LotteryPhase.Results, LotterySchedule.GetSnapshot(anchor + LotterySchedule.ApplicationLength).CurrentPhase);
    Equal(LotteryPhase.Results, LotterySchedule.GetSnapshot(anchor + LotterySchedule.CycleLength - TimeSpan.FromTicks(1)).CurrentPhase);
    Equal(LotteryPhase.Application, LotterySchedule.GetSnapshot(anchor + LotterySchedule.CycleLength).CurrentPhase);
    Equal(1L, LotterySchedule.GetSnapshot(anchor + LotterySchedule.CycleLength).CycleId);
}

static void LotteryBeforeAnchor()
{
    var snapshot = LotterySchedule.GetSnapshot(LotterySchedule.ApplicationAnchorUtc - TimeSpan.FromDays(1));
    Equal(-1L, snapshot.CycleId);
    Equal(LotteryPhase.Results, snapshot.CurrentPhase);
    Equal(LotterySchedule.ApplicationAnchorUtc, snapshot.Application.StartsAtUtc);
    Equal(LotterySchedule.ApplicationAnchorUtc - TimeSpan.FromDays(4), snapshot.Results.StartsAtUtc);
}

static void LotteryPeriodOccurrences()
{
    var snapshot = LotterySchedule.GetSnapshot(new DateTimeOffset(2026, 10, 29, 15, 0, 0, TimeSpan.Zero));
    Equal(LotteryPhase.Results, snapshot.CurrentPhase);
    Equal(true, snapshot.Results.IsActive);
    Equal(false, snapshot.Application.IsActive);
    Equal(new DateTimeOffset(2026, 10, 28, 15, 0, 0, TimeSpan.Zero), snapshot.Results.StartsAtUtc);
    Equal(new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.Zero), snapshot.Results.EndsAtUtc);
    Equal(new DateTimeOffset(2026, 11, 6, 15, 0, 0, TimeSpan.Zero), snapshot.Results.NextAtUtc);
    Equal(new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.Zero), snapshot.Application.StartsAtUtc);
    Equal(new DateTimeOffset(2026, 11, 6, 15, 0, 0, TimeSpan.Zero), snapshot.Application.EndsAtUtc);
    Equal(new DateTimeOffset(2026, 11, 10, 15, 0, 0, TimeSpan.Zero), snapshot.Application.NextAtUtc);
}

static void KnownLotteryDates()
{
    var results = LotterySchedule.GetSnapshot(new DateTimeOffset(2026, 10, 28, 15, 0, 0, TimeSpan.Zero));
    Equal(LotteryPhase.Results, results.CurrentPhase);
    Equal(new DateTimeOffset(2026, 10, 28, 15, 0, 0, TimeSpan.Zero), results.Results.StartsAtUtc);
    Equal(new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.Zero), results.Results.EndsAtUtc);
    Equal(new DateTimeOffset(2026, 11, 6, 15, 0, 0, TimeSpan.Zero), results.Results.NextAtUtc);

    var application = LotterySchedule.GetSnapshot(new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.Zero));
    Equal(LotteryPhase.Application, application.CurrentPhase);
    Equal(new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.Zero), application.Application.StartsAtUtc);
    Equal(new DateTimeOffset(2026, 11, 6, 15, 0, 0, TimeSpan.Zero), application.Application.EndsAtUtc);
    Equal(new DateTimeOffset(2026, 11, 10, 15, 0, 0, TimeSpan.Zero), application.Application.NextAtUtc);
}

static void CentralDaylightSaving()
{
    var central = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
    var before = LotterySchedule.FormatTimestamp(
        new DateTimeOffset(2026, 10, 28, 15, 0, 0, TimeSpan.Zero), false, central);
    var after = LotterySchedule.FormatTimestamp(
        new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.Zero), false, central);

    Equal(true, before.Contains("10:00:00 AM UTC-05:00", StringComparison.Ordinal));
    Equal(true, after.Contains("9:00:00 AM UTC-06:00", StringComparison.Ordinal));
}

static void LocalZoneLabel()
{
    var central = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
    Equal("Local time (UTC−05:00)", LotterySchedule.GetDisplayZoneLabel(
        false, central, new DateTimeOffset(2026, 10, 28, 15, 0, 0, TimeSpan.Zero)));
    Equal("Local time (UTC−06:00)", LotterySchedule.GetDisplayZoneLabel(
        false, central, new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.Zero)));
}

static void LotteryCountdown()
{
    Equal("00:00:01", LotterySchedule.FormatRemaining(TimeSpan.FromTicks(1)));
    Equal("05:04:03", LotterySchedule.FormatRemaining(TimeSpan.FromHours(5).Add(TimeSpan.FromMinutes(4)).Add(TimeSpan.FromSeconds(3))));
    Equal("2d 03:04:05", LotterySchedule.FormatRemaining(TimeSpan.FromDays(2).Add(TimeSpan.FromHours(3)).Add(TimeSpan.FromMinutes(4)).Add(TimeSpan.FromSeconds(5))));
    Equal("00:00:00", LotterySchedule.FormatRemaining(TimeSpan.FromSeconds(-1)));

    var first = LotterySchedule.GetSnapshot(new DateTimeOffset(2026, 11, 1, 14, 59, 59, TimeSpan.Zero));
    var jumped = LotterySchedule.GetSnapshot(new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.Zero));
    Equal(TimeSpan.FromSeconds(1), first.Remaining);
    Equal(LotteryPhase.Application, jumped.CurrentPhase);
    Equal(LotterySchedule.ApplicationLength, jumped.Remaining);
}

static void PaissaWorldParsing()
{
    const string json = """[{"id":63,"name":"Gilgamesh","datacenter_id":4,"datacenter_name":"Aether"}]""";
    var worlds = PaissaApiParser.ParseWorlds(json);
    Equal(1, worlds.Count);
    Equal(63, worlds[0].Id);
    Equal("Gilgamesh", worlds[0].Name);
    Equal("Aether", worlds[0].DataCenterName);
}

static void PaissaInspection()
{
    const string json = """{"id":63,"name":"Gilgamesh","num_open_plots":1,"oldest_plot_time":1793152763.4,"districts":[{"id":339,"name":"Mist","num_open_plots":1,"oldest_plot_time":1793152763.4,"open_plots":[{"world_id":63,"district_id":339,"ward_number":4,"plot_number":18,"size":2,"price":47500000,"last_updated_time":1793295771.4,"first_seen_time":1793273173.8,"est_time_open_min":0,"est_time_open_max":0,"purchase_system":5,"lotto_entries":null,"lotto_phase":2,"lotto_phase_until":1793545200},{"ward_number":1,"plot_number":2,"price":100}]}]}""";
    var report = PaissaApiParser.InspectWorld(json, 1);
    Equal("Gilgamesh", report.Name);
    Equal(1, report.Districts.Count);
    Equal(2, report.Districts[0].ActualPlotCount);
    Equal(1, report.Samples.Count);
    Equal(4, report.Samples[0].WardNumber);
    Equal(18, report.Samples[0].PlotNumber);
    Equal(2, report.Samples[0].Size);
    Equal(null, report.Samples[0].LottoEntries);
    Equal(2, report.Listings.Count);
    Equal(5, report.Listings[0].WardNumber);
    Equal(19, report.Listings[0].PlotNumber);

    var entries = report.Fields.Single(field => field.Name == "lotto_entries");
    Equal(1, entries.PresentCount);
    Equal(1, entries.NullCount);
    Equal(1, entries.MissingCount);
    var size = report.Fields.Single(field => field.Name == "size");
    Equal(1, size.PresentCount);
    Equal(1, size.MissingCount);
    var phaseUntil = report.Fields.Single(field => field.Name == "lotto_phase_until");
    Equal(1, phaseUntil.MissingCount);
}

static void PaissaFullListings()
{
    var plots = string.Join(",", Enumerable.Range(0, 12).Select(index =>
        $"{{\"world_id\":63,\"district_id\":339,\"ward_number\":0,\"plot_number\":{index},\"size\":0,\"price\":100,\"last_updated_time\":1793295771,\"first_seen_time\":1793273173,\"purchase_system\":1}}"));
    var json = $"{{\"id\":63,\"name\":\"Gilgamesh\",\"num_open_plots\":12,\"oldest_plot_time\":1793273173,\"districts\":[{{\"id\":339,\"name\":\"Mist\",\"num_open_plots\":12,\"oldest_plot_time\":1793273173,\"open_plots\":[{plots}]}}]}}";
    var inspection = PaissaApiParser.InspectWorld(json);
    Equal(12, inspection.Listings.Count);
    Equal(8, inspection.Samples.Count);
    Equal(12, inspection.Districts[0].ActualPlotCount);
    Equal(0, inspection.Warnings.Count);
}

static void PaissaEmptyWorld()
{
    const string json = """{"id":63,"name":"Gilgamesh","num_open_plots":0,"oldest_plot_time":0,"districts":[]}""";
    var inspection = PaissaApiParser.InspectWorld(json);
    Equal(0, inspection.Listings.Count);
    Equal(0, inspection.Warnings.Count);
}

static void PaissaPhaseFreshness()
{
    var entryDeadline = new DateTimeOffset(2026, 11, 6, 15, 0, 0, TimeSpan.Zero);
    var now = entryDeadline.AddDays(1);
    var reportedEntry = new PaissaPlotListing(63, 339, "Mist", 0, 0, 1, 1, 0, 100, 1, 2, 1,
        entryDeadline.ToUnixTimeSeconds(), null, null, null, null);
    Equal("Results", PaissaHousingPolicy.GetPhaseLabel(reportedEntry, now));
    Equal(new DateTimeOffset(2026, 11, 10, 15, 0, 0, TimeSpan.Zero),
        PaissaHousingPolicy.GetDisplayedPhaseDeadline(reportedEntry, now));
    Equal("Results", PaissaHousingPolicy.GetPhaseLabel(reportedEntry with { LottoPhase = 2 }, now));
    Equal("Results", PaissaHousingPolicy.GetPhaseLabel(reportedEntry with { LottoPhaseUntil = now.AddDays(1).ToUnixTimeSeconds() }, now));
    Equal("Results", PaissaHousingPolicy.GetPhaseLabel(reportedEntry with { LottoPhaseUntil = entryDeadline.AddDays(-9).ToUnixTimeSeconds() }, now));
    Equal("Results", PaissaHousingPolicy.GetPhaseLabel(reportedEntry with { LottoPhase = null }, now));
    Equal(PaissaPhaseFilter.Results, PaissaHousingPolicy.GetPhaseFilter(reportedEntry, now));
    // Midgardsormr / Mist W5 P11: sweep reports a lottery vacancy, but lottery fields are null.
    var wardFivePlotEleven = reportedEntry with
    {
        WorldId = 65, WardNumber = 5, PlotNumber = 11, PurchaseSystem = 3,
        LottoPhase = null, LottoPhaseUntil = null, LottoEntries = null,
    };
    var entryNow = LotterySchedule.ApplicationAnchorUtc.AddHours(3);
    Equal("Entry", PaissaHousingPolicy.GetPhaseLabel(wardFivePlotEleven, entryNow));
    Equal<DateTimeOffset?>(LotterySchedule.ApplicationAnchorUtc.AddDays(5), PaissaHousingPolicy.GetDisplayedPhaseDeadline(wardFivePlotEleven, entryNow));
    Equal<long?>(null, wardFivePlotEleven.LottoEntries);
}

static void PaissaHousingFiltersWork()
{
    var now = DateTimeOffset.FromUnixTimeSeconds(1_793_400_000);
    var plot = new PaissaPlotListing(63, 339, "Mist", 0, 0, 1, 1, 1, 2_000_000, 5, null, 2,
        1_793_500_000, null, null, null, null);
    var all = new PaissaHousingFilters(
        new HashSet<int?> { 339 }, new HashSet<int?> { 0, 1, 2, null },
        Enum.GetValues<PaissaEligibilityFilter>().ToHashSet(),
        Enum.GetValues<PaissaPhaseFilter>().ToHashSet());
    Equal(true, PaissaHousingPolicy.Matches(plot, all, now));
    Equal(true, PaissaHousingPolicy.Matches(plot, all with
    {
        Eligibility = new HashSet<PaissaEligibilityFilter> { PaissaEligibilityFilter.Personal },
    }, now));
    Equal(false, PaissaHousingPolicy.Matches(plot, all with
    {
        Eligibility = new HashSet<PaissaEligibilityFilter> { PaissaEligibilityFilter.FreeCompany },
    }, now));
    Equal(true, PaissaHousingPolicy.Matches(plot with { LottoEntries = null }, all, now));
    Equal(false, PaissaHousingPolicy.Matches(plot, all with { Sizes = new HashSet<int?>() }, now));
}

static void PaissaEligibilityLabels()
{
    Equal("FC", PaissaHousingPolicy.DescribeEligibility(3));
    Equal("Personal", PaissaHousingPolicy.DescribeEligibility(5));
    Equal("Personal & FC", PaissaHousingPolicy.DescribeEligibility(7));
    Equal("Unknown", PaissaHousingPolicy.DescribeEligibility(null));
}

static void PaissaCycleFreshness()
{
    var now = new DateTimeOffset(2026, 11, 7, 15, 0, 0, TimeSpan.Zero);
    var updated = now.AddHours(-1).ToUnixTimeMilliseconds() / 1000d;
    var plot = new PaissaPlotListing(63, 339, "Mist", 0, 0, 1, 1, 0, 100, 1, 0, 3,
        null, null, updated, null, null);
    Equal(PaissaSourceFreshness.Current, PaissaHousingPolicy.GetSourceFreshness(plot, now));
    Equal(PaissaSourceFreshness.Current, PaissaHousingPolicy.GetSourceFreshness(plot with
    {
        LastUpdatedTime = new DateTimeOffset(2026, 11, 6, 16, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
    }, now));
    Equal(PaissaSourceFreshness.Stale, PaissaHousingPolicy.GetSourceFreshness(plot with
    {
        LastUpdatedTime = new DateTimeOffset(2026, 11, 1, 14, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
    }, now));
    Equal(PaissaSourceFreshness.Unknown, PaissaHousingPolicy.GetSourceFreshness(plot with { LastUpdatedTime = null }, now));
    Equal(PaissaSourceFreshness.Unknown, PaissaHousingPolicy.GetSourceFreshness(plot with
    {
        LastUpdatedTime = now.AddMinutes(6).ToUnixTimeSeconds(),
    }, now));
}

static PaissaPlotListing HousingTestPlot()
    => new(73, 339, "Mist", 0, 0, 1, 1, 0, 100, 3, 2, 1,
        LotterySchedule.ApplicationAnchorUtc.AddDays(5).ToUnixTimeSeconds(), null,
        LotterySchedule.ApplicationAnchorUtc.ToUnixTimeSeconds(), null, null);

static void HousingPhaseBoundaries()
{
    var plot = HousingTestPlot();
    var deadline = LotterySchedule.ApplicationAnchorUtc.AddDays(5);
    var resultsEnd = deadline.AddDays(4);
    Equal(PaissaPhaseFilter.Entry, PaissaHousingPolicy.GetPhaseFilter(plot, deadline.AddTicks(-1)));
    Equal(PaissaPhaseFilter.Results, PaissaHousingPolicy.GetPhaseFilter(plot, deadline));
    Equal(PaissaPhaseFilter.Results, PaissaHousingPolicy.GetPhaseFilter(plot, resultsEnd.AddTicks(-1)));
    Equal(PaissaPhaseFilter.Entry, PaissaHousingPolicy.GetPhaseFilter(plot, resultsEnd));
    Equal(PaissaPhaseFilter.Results, PaissaHousingPolicy.GetPhaseFilter(plot, deadline.AddDays(9)));

    var cases = new (PaissaPlotListing Plot, PaissaPhaseFilter Phase, string Label)[]
    {
        (plot with { LottoPhaseUntil = null }, PaissaPhaseFilter.Results, "Results"),
        (plot with { LottoPhaseUntil = long.MaxValue }, PaissaPhaseFilter.Results, "Results"),
        (plot, PaissaPhaseFilter.Results, "Results"),
        (plot with { LottoPhase = 2 }, PaissaPhaseFilter.Results, "Results"),
        (plot with { LottoPhase = 3 }, PaissaPhaseFilter.Results, "Results"),
        (plot with { LottoPhase = null }, PaissaPhaseFilter.Results, "Results"),
        (plot with { LottoPhase = 99 }, PaissaPhaseFilter.Results, "Results"),
    };
    foreach (var (listing, phase, label) in cases)
    {
        Equal(phase, PaissaHousingPolicy.GetPhaseFilter(listing, deadline));
        Equal(label, PaissaHousingPolicy.GetPhaseLabel(listing, deadline));
        foreach (var filterPhase in Enum.GetValues<PaissaPhaseFilter>())
        {
            var filters = new PaissaHousingFilters(new HashSet<int?> { 339 }, new HashSet<int?> { 0 },
                new HashSet<PaissaEligibilityFilter> { PaissaEligibilityFilter.FreeCompany },
                new HashSet<PaissaPhaseFilter> { filterPhase });
            Equal(phase == filterPhase, PaissaHousingPolicy.Matches(listing, filters, deadline));
        }
    }
    Equal(PaissaPhaseFilter.Entry, PaissaHousingPolicy.GetPhaseFilter(plot with { LottoPhase = 2 }, resultsEnd));
    Equal(PaissaPhaseFilter.Results, PaissaHousingPolicy.GetPhaseFilter(plot, LotterySchedule.ApplicationAnchorUtc.AddTicks(-1)));
    Equal(PaissaPhaseFilter.Entry, PaissaHousingPolicy.GetPhaseFilter(plot, LotterySchedule.ApplicationAnchorUtc));
}

static void HousingDeadlineBoundaries()
{
    var plot = HousingTestPlot();
    var original = plot.LottoPhaseUntil;
    var entryEnd = LotterySchedule.ApplicationAnchorUtc.AddDays(5);
    var resultsEnd = entryEnd.AddDays(4);
    Equal<DateTimeOffset?>(entryEnd, PaissaHousingPolicy.GetDisplayedPhaseDeadline(plot, entryEnd.AddTicks(-1)));
    Equal<DateTimeOffset?>(resultsEnd, PaissaHousingPolicy.GetDisplayedPhaseDeadline(plot, entryEnd));
    Equal<DateTimeOffset?>(resultsEnd, PaissaHousingPolicy.GetDisplayedPhaseDeadline(plot, resultsEnd.AddTicks(-1)));
    Equal<DateTimeOffset?>(entryEnd.AddDays(9), PaissaHousingPolicy.GetDisplayedPhaseDeadline(plot, resultsEnd));
    Equal<DateTimeOffset?>(resultsEnd.AddDays(9), PaissaHousingPolicy.GetDisplayedPhaseDeadline(plot, entryEnd.AddDays(9)));
    Equal<DateTimeOffset?>(resultsEnd, PaissaHousingPolicy.GetDisplayedPhaseDeadline(plot with { LottoPhase = 2 }, entryEnd));
    Equal<DateTimeOffset?>(resultsEnd, PaissaHousingPolicy.GetDisplayedPhaseDeadline(plot with { LottoPhaseUntil = null }, entryEnd));
    Equal<DateTimeOffset?>(resultsEnd, PaissaHousingPolicy.GetDisplayedPhaseDeadline(plot with { LottoPhaseUntil = long.MaxValue }, entryEnd));
    Equal<DateTimeOffset?>(null, PaissaHousingPolicy.GetDisplayedPhaseDeadline(plot with { PurchaseSystem = null }, entryEnd));
    Equal(original, plot.LottoPhaseUntil);
    Equal(1, plot.LottoPhase);
}

static void HousingClosureCountdown()
{
    var plot = HousingTestPlot();
    var entryEnd = LotterySchedule.ApplicationAnchorUtc.AddDays(5);
    Equal("Period ends in 4d 00:00:00", PaissaHousingPolicy.GetClosureCountdown(plot, entryEnd));
    Equal("Period ends in 00:00:01", PaissaHousingPolicy.GetClosureCountdown(plot, entryEnd.AddTicks(-1)));
    Equal("Period ends in 5d 00:00:00", PaissaHousingPolicy.GetClosureCountdown(plot, entryEnd.AddDays(4)));
    Equal("Period ends in 4d 00:00:00", PaissaHousingPolicy.GetClosureCountdown(plot with { LottoPhaseUntil = null }, entryEnd));
    Equal("Period ends in 4d 00:00:00", PaissaHousingPolicy.GetClosureCountdown(plot with { LottoPhaseUntil = long.MinValue }, entryEnd));
    Equal("Period end is unknown.", PaissaHousingPolicy.GetClosureCountdown(plot with { PurchaseSystem = 6 }, entryEnd));
    Equal("Period ends in 4d 00:00:00", PaissaHousingPolicy.GetClosureCountdown(plot, entryEnd.ToOffset(TimeSpan.FromHours(-6))));
}

static void HousingFreshnessBoundaries()
{
    var anchor = LotterySchedule.ApplicationAnchorUtc;
    var plot = HousingTestPlot();
    foreach (var now in new[] { anchor, anchor.AddDays(5), anchor.AddDays(9), anchor.AddDays(-4) })
    {
        var snapshot = LotterySchedule.GetSnapshot(now);
        var previousPhaseStart = snapshot.CurrentPhase == LotteryPhase.Results
            ? now - LotterySchedule.ApplicationLength
            : now - LotterySchedule.ResultsLength;
        Equal(PaissaSourceFreshness.Current, PaissaHousingPolicy.GetSourceFreshness(plot with
        {
            LastUpdatedTime = previousPhaseStart.ToUnixTimeSeconds(),
        }, now));
        Equal(PaissaSourceFreshness.Stale, PaissaHousingPolicy.GetSourceFreshness(plot with
        {
            LastUpdatedTime = previousPhaseStart.AddSeconds(-1).ToUnixTimeSeconds(),
        }, now));
        Equal(PaissaSourceFreshness.Current, PaissaHousingPolicy.GetSourceFreshness(plot with { LastUpdatedTime = now.ToUnixTimeSeconds() }, now));
    }
    foreach (var invalid in new double?[] { null, double.NaN, double.PositiveInfinity, double.MaxValue, anchor.AddMinutes(6).ToUnixTimeSeconds() })
        Equal(PaissaSourceFreshness.Unknown, PaissaHousingPolicy.GetSourceFreshness(plot with { LastUpdatedTime = invalid }, anchor));
}

static PaissaHousingWorldData HousingTestSnapshot(DateTimeOffset downloadedAt)
    => new(new PaissaWorld(73, "Adamantoise", 4, "Aether"), downloadedAt, [], []);

static void HousingDeadlineSorting()
{
    var now = LotterySchedule.ApplicationAnchorUtc.AddDays(6);
    var snapshot = HousingTestSnapshot(now.AddDays(-3));
    var inferred = HousingTestPlot() with { PlotNumber = 2 };
    var earlier = inferred with { PlotNumber = 3, LottoPhase = 2, LottoPhaseUntil = now.AddDays(1).ToUnixTimeSeconds() };
    var sameClose = inferred with { PlotNumber = 1, LottoPhase = 2, LottoPhaseUntil = LotterySchedule.ApplicationAnchorUtc.AddDays(9).ToUnixTimeSeconds() };
    var unknown = inferred with { PlotNumber = 4, PurchaseSystem = null };
    var rows = new[] { (snapshot, inferred), (snapshot, earlier), (snapshot, unknown), (snapshot, sameClose) };
    Equal("1,2,3,4", string.Join(",", PaissaHousingSorting.Sort(rows, 9, true, now).Select(row => row.Plot.PlotNumber)));
    Equal("1,2,3,4", string.Join(",", PaissaHousingSorting.Sort(rows, 9, false, now).Select(row => row.Plot.PlotNumber)));
}

static void HousingPhaseSorting()
{
    var now = LotterySchedule.ApplicationAnchorUtc.AddDays(6);
    var snapshot = HousingTestSnapshot(LotterySchedule.ApplicationAnchorUtc.AddDays(1));
    var inferred = HousingTestPlot() with { PlotNumber = 2 };
    var entry = inferred with { PlotNumber = 1, LottoPhaseUntil = now.AddDays(1).ToUnixTimeSeconds() };
    var unavailable = inferred with { PlotNumber = 3, LottoPhase = 3 };
    var unknown = inferred with { PlotNumber = 4, PurchaseSystem = null };
    var rows = new[] { (snapshot, inferred), (snapshot, entry), (snapshot, unknown), (snapshot, unavailable) };
    Equal("1,2,3,4", string.Join(",", PaissaHousingSorting.Sort(rows, 7, true, now).Select(row => row.Plot.PlotNumber)));
    Equal("1,2,3,4", string.Join(",", PaissaHousingSorting.Sort(rows, 7, false, now).Select(row => row.Plot.PlotNumber)));
    var refreshed = rows.Select(row => (snapshot with { DownloadedAtUtc = now.AddDays(2) }, row.Item2));
    Equal("1,2,3,4", string.Join(",", PaissaHousingSorting.Sort(refreshed, 7, false, now).Select(row => row.Plot.PlotNumber)));
    Equal(PaissaSourceFreshness.Current, PaissaHousingPolicy.GetSourceFreshness(inferred, now));
}

static void HousingSortingUnknowns()
{
    var now = LotterySchedule.ApplicationAnchorUtc;
    var snapshot = HousingTestSnapshot(now);
    var plot = HousingTestPlot();
    var rows = new[]
    {
        (snapshot, plot with { PlotNumber = 4, LottoEntries = null }),
        (snapshot, plot with { PlotNumber = 3, LottoEntries = 2 }),
        (snapshot, plot with { PlotNumber = 1, LottoEntries = 2 }),
        (snapshot, plot with { PlotNumber = 2, LottoEntries = 1 }),
    };
    Equal("2,1,3,4", string.Join(",", PaissaHousingSorting.Sort(rows, 8, true, now).Select(row => row.Plot.PlotNumber)));
    Equal("1,3,2,4", string.Join(",", PaissaHousingSorting.Sort(rows, 8, false, now).Select(row => row.Plot.PlotNumber)));
}

static void HousingCharacterChanges()
{
    var config = new Configuration();
    var worlds = new HashSet<int> { 99 };
    var saves = 0;
    if (HousingCharacterSelection.Apply(config, worlds, 73, 100)) saves++;
    Equal("73", string.Join(",", worlds));
    worlds.Clear();
    worlds.UnionWith([74, 75]);
    config.HousingWorldIds = worlds.Order().ToList();
    for (var index = 0; index < 10; index++)
        if (HousingCharacterSelection.Apply(config, worlds, 73, 100)) saves++;
    Equal("74,75", string.Join(",", worlds.Order()));
    Equal(1, saves);
    Equal(true, HousingCharacterSelection.Apply(config, worlds, 73, 101));
    Equal("73", string.Join(",", worlds));
    Equal(true, HousingCharacterSelection.Apply(config, worlds, 80, 102));
    Equal("80", string.Join(",", worlds));
    Equal("80", string.Join(",", config.HousingWorldIds));
}

static void HousingCharacterReadiness()
{
    var config = new Configuration { LastHousingCharacterContentId = 100, HousingWorldIds = [73] };
    var worlds = new HashSet<int> { 73 };
    foreach (var (world, character) in new (int?, ulong?)[] { (null, null), (0, 101), (73, 0), (null, 101) })
        Equal(false, HousingCharacterSelection.Apply(config, worlds, world, character));
    Equal(100UL, config.LastHousingCharacterContentId);
    Equal("73", string.Join(",", worlds));
    Equal(false, HousingCharacterSelection.Apply(config, worlds, 73, 100));
    Equal(true, HousingCharacterSelection.Apply(config, worlds, 80, 101));
}

static void HousingCharacterReload()
{
    var config = new Configuration();
    var worlds = new HashSet<int>();
    // No directory dependency: home ID is selected and persisted before directory arrives.
    Equal(true, HousingCharacterSelection.Apply(config, worlds, 73, 100));
    Equal("73", string.Join(",", worlds));
    worlds.Clear();
    worlds.Add(80);
    config.HousingWorldIds = [80];
    var reloaded = System.Text.Json.JsonSerializer.Deserialize<Configuration>(System.Text.Json.JsonSerializer.Serialize(config))!;
    var restoredWorlds = reloaded.HousingWorldIds.ToHashSet();
    Equal(false, HousingCharacterSelection.Apply(reloaded, restoredWorlds, 73, 100));
    Equal("80", string.Join(",", restoredWorlds));
    Equal(true, HousingCharacterSelection.Apply(reloaded, restoredWorlds, 73, 101));
    Equal("73", string.Join(",", restoredWorlds));
}

static void HousingPurchaseSystemPhase()
{
    var entry = LotterySchedule.ApplicationAnchorUtc.AddHours(3);
    var results = LotterySchedule.ApplicationAnchorUtc.AddDays(5);
    foreach (int? flags in new int?[] { null, -1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 99 })
    {
        var lottery = flags is 1 or 3 or 5 or 7;
        var unknown = flags is null or < 0 or > 7;
        foreach (var rawPhase in new int?[] { null, 0, 1, 2, 3, 99 })
        {
            var plot = HousingTestPlot() with
            {
                PurchaseSystem = flags, LottoPhase = rawPhase,
                LottoPhaseUntil = entry.AddDays(-9).ToUnixTimeSeconds(), LottoEntries = null,
            };
            var original = plot with { };
            Equal(lottery ? "Entry" : unknown ? "Unknown" : "Not applicable",
                PaissaHousingPolicy.GetPhaseLabel(plot, entry));
            Equal(lottery ? "Results" : unknown ? "Unknown" : "Not applicable",
                PaissaHousingPolicy.GetPhaseLabel(plot, results));
            Equal<DateTimeOffset?>(lottery ? results : null,
                PaissaHousingPolicy.GetDisplayedPhaseDeadline(plot, entry));
            Equal<DateTimeOffset?>(lottery ? results.AddDays(4) : null,
                PaissaHousingPolicy.GetDisplayedPhaseDeadline(plot, results));
            Equal(lottery ? PaissaPhaseFilter.Entry : PaissaPhaseFilter.Unknown,
                PaissaHousingPolicy.GetPhaseFilter(plot, entry));
            Equal(original, plot);
            Equal<long?>(null, plot.LottoEntries);
            var counted = plot with { LottoEntries = 17 };
            PaissaHousingPolicy.GetPhaseLabel(counted, results.AddDays(4));
            Equal<long?>(17, counted.LottoEntries);
        }
    }
}

static void HousingPhaseFilterMigration()
{
    var cases = new (int Old, int Expected)[]
    {
        (0, 0), (1, 0), (2, 1), (4, 2), (8, 3), (16, 4),
        (2 | 4, 3), (2 | 16, 5), (4 | 16, 6), (8 | 16, 7), (15, 3), (31, 7),
    };
    foreach (var (old, expected) in cases)
    {
        var config = new Configuration { Version = 8, HousingPhaseMask = old, DisplayLotteryTimesInUtc = true };
        config.Normalize();
        Equal(expected, config.HousingPhaseMask);
        Equal(Configuration.CurrentVersion, config.Version);
        config.Normalize();
        Equal(expected, config.HousingPhaseMask);
        Equal(true, config.DisplayLotteryTimesInUtc);
    }
    for (var mask = 0; mask <= 7; mask++)
    {
        var config = new Configuration { HousingPhaseMask = mask };
        config.Normalize();
        Equal(mask, config.HousingPhaseMask);
    }
}

static void HousingDistrictReload()
{
    foreach (var districts in new List<int>[] { [], [339], [340, 979], [339, 340, 341, 641, 979] })
    {
        var config = new Configuration { HousingDistrictIds = districts };
        for (var reload = 0; reload < 3; reload++)
        {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(config);
            config = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(json)!;
            config.Normalize();
            Equal(string.Join(",", districts), string.Join(",", config.HousingDistrictIds));
        }
    }
    var missing = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>("{}")!;
    missing.Normalize();
    Equal("339,340,341,641,979", string.Join(",", missing.HousingDistrictIds));
    var nullList = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>("{\"HousingDistrictIds\":null}")!;
    nullList.Normalize();
    Equal("339,340,341,641,979", string.Join(",", nullList.HousingDistrictIds));
}

static void HousingPreferencesMigration()
{
    var config = new Configuration { Version = 7, HousingPhaseMask = 31, LastHousingCharacterContentId = 100 };
    config.Normalize();
    Equal(7, config.HousingPhaseMask);
    config.HousingPhaseMask = 2 | 16;
    config.Normalize();
    Equal(2, config.HousingPhaseMask);
    Equal(100UL, config.LastHousingCharacterContentId);
    var oldConfig = System.Text.Json.JsonSerializer.Deserialize<Configuration>("{\"Version\":7,\"HousingWorldIds\":[73]}")!;
    oldConfig.Normalize();
    Equal(0UL, oldConfig.LastHousingCharacterContentId);
    Equal("73", string.Join(",", oldConfig.HousingWorldIds));
    config.ResetToDefaults();
    Equal(0UL, config.LastHousingCharacterContentId);
    Equal(7, config.HousingPhaseMask);
    Equal(0, config.HousingWorldIds.Count);
}

static void PaissaRetryAfter()
{
    var handler = new RetryAfterHandler(TimeSpan.FromSeconds(30));
    using var client = new PaissaApiClient(handler);
    var response = client.GetWorldsAsync().GetAwaiter().GetResult();
    Equal(false, response.IsSuccess);
    Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    Equal(true, response.RetryAfterUtc > DateTimeOffset.UtcNow.AddSeconds(20));
    Equal(true, client.CooldownUntilUtc > DateTimeOffset.UtcNow.AddSeconds(20));
}

static void PaissaCooldownCancellation()
{
    var handler = new RetryAfterHandler(TimeSpan.FromSeconds(5));
    using var client = new PaissaApiClient(handler);
    client.GetWorldsAsync().GetAwaiter().GetResult();
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
    var started = DateTimeOffset.UtcNow;
    var result = client.GetWorldsAsync(cancellation.Token).GetAwaiter().GetResult();
    Equal(false, result.IsSuccess);
    Equal(true, result.Error!.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
    Equal(true, DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(2));
}

static void PaissaRequestSpacing()
{
    Equal(10, (int)PaissaApiClient.DefaultMinimumRequestInterval.TotalSeconds);
    var handler = new TimestampHandler();
    using var client = new PaissaApiClient(handler, TimeSpan.FromMilliseconds(80));
    client.GetWorldsAsync().GetAwaiter().GetResult();
    client.GetWorldsAsync().GetAwaiter().GetResult();
    Equal(2, handler.RequestTimes.Count);
    Equal(true, handler.RequestTimes[1] - handler.RequestTimes[0] >= TimeSpan.FromMilliseconds(60));
}

static void PaissaCooldownRace()
{
    var handler = new CooldownRaceHandler(TimeSpan.FromMilliseconds(350));
    using var client = new PaissaApiClient(handler, TimeSpan.FromMilliseconds(100));
    Task.WhenAll(client.GetWorldsAsync(), client.GetWorldsAsync()).GetAwaiter().GetResult();
    Equal(2, handler.RequestTimes.Count);
    Equal(true, handler.RequestTimes[1] - handler.RequestTimes[0] >= TimeSpan.FromMilliseconds(300));
}

static void PaissaValueDecoding()
{
    Equal("Lottery + Personal", PaissaApiParser.DescribePurchaseSystem(5));
    Equal("Unrecognized (99)", PaissaApiParser.DescribePurchaseSystem(99));
    Equal("Available", PaissaApiParser.DescribeLotteryPhase(1));
    Equal("Unknown / not reported", PaissaApiParser.DescribeLotteryPhase(null));
    Equal(1_793_295_771_400L, PaissaApiParser.UnixSecondsToUtc(1_793_295_771.4)!.Value.ToUnixTimeMilliseconds());
    Equal(null, PaissaApiParser.UnixSecondsToUtc(double.NaN));
}

static void PaissaEmptyVersusFailure()
{
    using var emptyClient = new PaissaApiClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("[]"),
    }));
    var empty = emptyClient.GetWorldsAsync().GetAwaiter().GetResult();
    Equal(true, empty.IsSuccess);
    Equal(0, empty.Data!.Count);

    using var failedClient = new PaissaApiClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
    {
        ReasonPhrase = "Unavailable",
        Content = new StringContent("maintenance"),
    }));
    var failed = failedClient.GetWorldsAsync().GetAwaiter().GetResult();
    Equal(false, failed.IsSuccess);
    Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
    Equal(null, failed.Data);
    Equal(true, failed.Error!.Contains("503", StringComparison.Ordinal));
}

static void PaissaMalformedResponse()
{
    using var client = new PaissaApiClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("{not json"),
    }));
    var result = client.GetWorldsAsync().GetAwaiter().GetResult();
    Equal(false, result.IsSuccess);
    Equal(HttpStatusCode.OK, result.StatusCode);
    Equal(true, result.Error!.StartsWith("Response parse failed:", StringComparison.Ordinal));
}

static PluginDiagnostics NewDiagnostics() => new("Loaded", () => true, "test-version", "test-build");

static OwnedCommandRegistration CreateRegistration(FakeCommandRegistry registry, string name, object handler)
    => new(name,
        () => registry.TryAdd(name, handler),
        () => registry.IsHandler(name, handler),
        () => registry.Contains(name),
        () => registry.Remove(name));

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

sealed class FakeCommandRegistry
{
    private readonly Dictionary<string, object> handlers = new(StringComparer.OrdinalIgnoreCase);

    public bool TryAdd(string command, object handler)
    {
        if (handlers.ContainsKey(command))
            return false;

        handlers.Add(command, handler);
        return true;
    }

    public bool Contains(string command) => handlers.ContainsKey(command);

    public bool IsHandler(string command, object expected)
        => handlers.TryGetValue(command, out var current) && ReferenceEquals(current, expected);

    public void Replace(string command, object handler) => handlers[command] = handler;

    public void Remove(string command) => handlers.Remove(command);
}

sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(respond(request));
}

sealed class RetryAfterHandler(TimeSpan retryAfter) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("rate limited"),
        };
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
        return Task.FromResult(response);
    }
}

sealed class TimestampHandler : HttpMessageHandler
{
    public List<DateTimeOffset> RequestTimes { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestTimes.Add(DateTimeOffset.UtcNow);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
    }
}

sealed class CooldownRaceHandler(TimeSpan retryAfter) : HttpMessageHandler
{
    private int requestCount;
    public List<DateTimeOffset> RequestTimes { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestTimes.Add(DateTimeOffset.UtcNow);
        var number = Interlocked.Increment(ref requestCount);
        var response = number == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("rate limited") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        if (number == 1)
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
        return Task.FromResult(response);
    }
}
