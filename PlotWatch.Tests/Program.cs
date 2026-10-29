using PlotWatch.Data;
using PlotWatch.Diagnostics;
using PlotWatch.Commands;
using PlotWatch.Windows;
using PlotWatch.Lottery;

var tests = new (string Name, Action Test)[]
{
    ("configuration defaults are safe", ConfigurationDefaults),
    ("configuration normalization migrates and clamps values", ConfigurationNormalization),
    ("configuration normalization repairs invalid tab", InvalidDefaultTab),
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
    ("lottery schedule classifies exact phase boundaries", LotteryBoundaries),
    ("lottery schedule handles pre-anchor dates", LotteryBeforeAnchor),
    ("lottery period cards select active and next occurrences", LotteryPeriodOccurrences),
    ("known October and November schedule dates match anchor", KnownLotteryDates),
    ("Central local display applies daylight-saving transition", CentralDaylightSaving),
    ("local timezone label refreshes its numeric offset", LocalZoneLabel),
    ("lottery countdown formats and recalculates after clock changes", LotteryCountdown),
};

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
