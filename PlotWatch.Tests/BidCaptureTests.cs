using System.Text.Json;
using PlotWatch.Data;
using PlotWatch.Lottery;
using PlotWatch.Paissa;

internal static class BidCaptureTests
{
    private const string TimeMacro = "<settime(lnum5)><if(t_hour,<switch(t_hour,1,2,3,4,5,6,7,8,9,10,11,12,1,2,3,4,5,6,7,8,9,10,11)>,12)>:<sec(t_min)> <if([t_hour>=12],p.m.,a.m.)> <if([gnum77==3],<num(t_day)>/<num(t_mon)>/<num(t_year)>,<num(t_mon)>/<num(t_day)>/<num(t_year)>)>";
    // Complete message fixture reconstructed from exported localized macro; not an exported raw live message.
    private const string Macro = "You have submitted a lottery entry for plot <num(lnum3)>, ward <num(lnum2)>, <sheet(PlaceName,<sheet(TerritoryType,lnum1,5)>,0)>. Your lottery number is <num(lnum4)>. Results will be available from " + TimeMacro + ".";
    private const string Text = "You have submitted a lottery entry for plot 54, ward 16, The Goblet. Your lottery number is 7. Results will be available from 9:00 a.m. 11/6/2026.";
    private static readonly Dictionary<int, string> Districts = new() { [341] = "The Goblet", [339] = "Mist" };
    private static readonly DateTimeOffset Received = DateTimeOffset.Parse("2026-11-06T02:54:34Z");
    private static readonly TimeZoneInfo Central = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    private static readonly TrackedCharacter Character = new(100, "Capture Test", 73, "Home World");

    public static readonly (string Name, Action Test)[] Cases =
    [
        ("capture parser matches complete reconstructed live success fixture", Success),
        ("capture parser rejects partial quoted and unrelated messages", EntireMessage),
        ("capture parser validates address and ticket bounds", Bounds),
        ("capture parser respects game date parameter not inferred order", DateOrder),
        ("capture parser rejects conflicting timestamp and timezone", TimestampConflict),
        ("capture parser associates current results cycle after boundary", Boundary),
        ("capture parser uses timezone offset at boundary across DST", DaylightSaving),
        ("capture parser fails closed on unknown macros and ambiguous districts", FailClosed),
        ("capture parser follows localized literals without English fallback", LocalizedShape),
        ("capture records observed time separately and stays unknown type", Save),
        ("capture duplicate does not save or update historical cache", Duplicate),
        ("capture reconciles manual record preserving identity and outcome", ManualMerge),
        ("capture rejects conflicting ticket and ambiguous manual records", Conflicts),
        ("capture unknown cycle needs corroborating results timestamp", UnknownCycle),
        ("capture save failure retains receipt identity for explicit retry", FailedSave),
        ("capture respects exclusions suppressed removal and read-only recovery", DisabledTracking),
        ("capture pending delivery deduplicates and bounded queue preserves oldest", PendingBound),
        ("capture cache enrichment uses full identity and rejects duplicates", Enrichment),
        ("manual edits cannot retain cached provenance for changed size or address", ManualProvenance),
        ("capture schema roundtrip migration and config remain opt-in", Migration),
    ];

    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
    private static BidMessageMatch? Parse(string text = Text, string macro = Macro, int dateFormat = 2,
        DateTimeOffset? received = null, TimeZoneInfo? zone = null, Dictionary<int, string>? districts = null) =>
        BidMessageParser.Match(macro, text, districts ?? Districts, received ?? Received, zone ?? Central, dateFormat);

    private static void Success()
    {
        var parsed = Parse() ?? throw new Exception("No complete match.");
        Equal(341, parsed.DistrictId); Equal(16, parsed.Ward); Equal(54, parsed.Plot); Equal(7, parsed.Ticket);
        Equal(0L, parsed.CycleId); Equal(LotterySchedule.ApplicationAnchorUtc.AddDays(5), parsed.ResultsAtUtc);
    }
    private static void EntireMessage()
    {
        foreach (var text in new[] { "Quoted: " + Text, Text + " extra", Text[..^1], Text[1..],
            "You have submitted a lottery entry for a different plot.",
            "The status of your lottery entry can be confirmed at any time via the Estate tab.",
            "You may only submit one lottery entry per lottery period." }) Equal<BidMessageMatch?>(null, Parse(text));
    }
    private static void Bounds()
    {
        foreach (var text in new[] { Text.Replace("plot 54", "plot 0"), Text.Replace("plot 54", "plot 61"),
            Text.Replace("ward 16", "ward 0"), Text.Replace("ward 16", "ward 31"),
            Text.Replace("number is 7", "number is 0"), Text.Replace("number is 7", "number is -1"),
            Text.Replace("number is 7", "number is 9999999999"), Text.Replace("The Goblet", "Unknown District") })
            Equal<BidMessageMatch?>(null, Parse(text));
        Equal(60, Parse(Text.Replace("plot 54", "plot 60"))!.Plot);
    }
    private static void DateOrder()
    {
        Equal<BidMessageMatch?>(null, Parse(dateFormat: 3));
        Equal(7, Parse(Text.Replace("11/6/2026", "6/11/2026"), dateFormat: 3)!.Ticket);
        Equal<BidMessageMatch?>(null, Parse(dateFormat: -1));
        Equal<BidMessageMatch?>(null, Parse(dateFormat: 4));
    }
    private static void TimestampConflict()
    {
        foreach (var text in new[] { Text.Replace("9:00", "8:59"), Text.Replace("a.m.", "p.m."),
            Text.Replace("2026", "2027"), Text.Replace("11/6/2026", "6/11/2026") }) Equal<BidMessageMatch?>(null, Parse(text));
        Equal<BidMessageMatch?>(null, Parse(zone: TimeZoneInfo.Utc));
        Equal(7, Parse(Text.Replace("9:00 a.m.", "3:00 p.m."), zone: TimeZoneInfo.Utc)!.Ticket);
    }
    private static void Boundary()
    {
        var results = LotterySchedule.ApplicationAnchorUtc.AddDays(5);
        foreach (var receipt in new[] { results.AddSeconds(-1), results, results.AddMinutes(2) })
            Equal(0L, Parse(received: receipt)!.CycleId);
        Equal<BidMessageMatch?>(null, Parse(received: results.AddMinutes(2).AddTicks(1)));
        Equal<BidMessageMatch?>(null, Parse(received: LotterySchedule.ApplicationAnchorUtc.AddDays(9)));
    }
    private static void DaylightSaving()
    {
        // At receipt Nov 1 before US fallback, boundary Nov 6 is already standard time.
        Equal(7, Parse(received: LotterySchedule.ApplicationAnchorUtc.AddMinutes(1))!.Ticket);
        var positive = TimeZoneInfo.CreateCustomTimeZone("UTC+9", TimeSpan.FromHours(9), "UTC+9", "UTC+9");
        Equal(7, Parse(Text.Replace("9:00 a.m. 11/6", "12:00 a.m. 11/7"), zone: positive)!.Ticket);
        var start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 1);
        var end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 16, 0, 0), 11, 6);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1), start, end);
        var ambiguous = TimeZoneInfo.CreateCustomTimeZone("BoundaryOverlap", TimeSpan.Zero, "Test", "Standard", "Daylight", [rule]);
        Equal(true, ambiguous.IsAmbiguousTime(new DateTime(2026, 11, 6, 15, 0, 0)));
        Equal<BidMessageMatch?>(null, Parse(Text.Replace("9:00 a.m.", "3:00 p.m."), zone: ambiguous));
    }
    private static void FailClosed()
    {
        foreach (var macro in new[] { Macro.Replace("<sec(t_min)>", "<unknown(t_min)>"), Macro.Replace("lnum5", "lnum6"),
            Macro.Replace("<num(lnum4)>", "<num(lnum3)>"), Macro[..^5], Macro.Replace("<num(t_year)>", "2026") })
            Equal<BidMessageMatch?>(null, Parse(macro: macro));
        Equal<BidMessageMatch?>(null, Parse(districts: new() { [341] = "The Goblet", [339] = "The Goblet" }));
    }
    private static void LocalizedShape()
    {
        // Synthetic alternate-language literals test mechanism, not actual DE/FR/JP sheets.
        var macro = Macro.Replace("You have submitted a lottery entry for plot", "Eintrag für Grundstück");
        var text = Text.Replace("You have submitted a lottery entry for plot", "Eintrag für Grundstück");
        Equal(7, Parse(text, macro)!.Ticket);
        Equal<BidMessageMatch?>(null, Parse(Text, macro));
    }

    private static BidRecord Capture() => new()
    {
        Id = Guid.NewGuid(), CharacterContentId = Character.ContentId, WorldId = 65, WorldName = "Midgardsormr",
        DistrictId = 341, Ward = 16, Plot = 54, TicketNumber = 7, CycleId = 0,
        ResultsAt = new(LotterySchedule.ApplicationAnchorUtc.AddDays(5), BidFieldSource.GameObserved),
        DetailsSource = BidFieldSource.GameObserved, TicketSource = BidFieldSource.GameObserved,
        MessageEvidence = new(Received, "English", Central.Id, 2, new string('a', 64), Text),
    };
    private sealed class Store : IBidStore
    {
        public BidDocument Data = new();
        public int Saves;
        public bool Fail;
        public bool ReadOnly;
        public BidLoadResult Load() => new(Data, ReadOnly);
        public void Save(BidDocument data) { if (Fail) throw new IOException(); Data = data; Saves++; }
        public void RestoreBackup(BidDocument data) => Save(data);
    }
    private static void Save()
    {
        var store = new Store(); var tracker = new BidTracker(store); var bid = Capture();
        Equal(true, tracker.SubmitCapture(Character, bid)); Equal(1, store.Saves);
        Equal(73, tracker.Data.Characters.Single().HomeWorldId); Equal(65, tracker.Data.Bids.Single().WorldId);
        Equal(BidPurchaseType.Unknown, tracker.Data.Bids.Single().PurchaseType);
        Equal<BidTimestamp?>(null, tracker.Data.Bids.Single().EnteredAt);
        Equal(Received, tracker.Data.Bids.Single().MessageEvidence!.ObservedAtUtc);
        Equal<BidCacheEvidence?>(null, tracker.Data.Bids.Single().CacheEvidence);
    }
    private static void Duplicate()
    {
        var store = new Store(); var tracker = new BidTracker(store); var bid = Capture();
        Equal(true, tracker.SubmitCapture(Character, bid));
        Equal(true, tracker.SubmitCapture(Character, bid with { Id = Guid.NewGuid(), Price = 1, CacheEvidence = new(Received, null, null, 1) }));
        Equal(1, store.Saves); Equal(1, tracker.Data.Bids.Count); Equal<long?>(null, tracker.Data.Bids.Single().Price);
    }
    private static BidRecord Manual() => Capture() with
    {
        MessageEvidence = null, TicketNumber = null, TicketSource = null, DetailsSource = BidFieldSource.Manual,
        PurchaseType = BidPurchaseType.FreeCompany, Outcome = BidOutcome.Won, Size = 2,
        EnteredAt = new(Received.AddMinutes(-1), BidFieldSource.Manual), ResultsAt = null,
    };
    private static void ManualMerge()
    {
        var manual = Manual(); var store = new Store { Data = new() { Characters = [Character], Bids = [manual] } };
        var tracker = new BidTracker(store);
        var captured = Capture() with { Size = 0, SizeFromCache = true, Price = 3187500, CacheEvidence = new(Received, 123, 0, 3187500) };
        Equal(true, tracker.SubmitCapture(Character, captured));
        var bid = tracker.Data.Bids.Single();
        Equal(manual.Id, bid.Id); Equal(BidOutcome.Won, bid.Outcome); Equal(BidPurchaseType.FreeCompany, bid.PurchaseType);
        Equal(manual.EnteredAt, bid.EnteredAt); Equal(2, bid.Size); Equal(false, bid.SizeFromCache);
        Equal(3187500L, bid.Price); Equal(7, bid.TicketNumber); Equal(BidFieldSource.Manual, bid.DetailsSource);
    }
    private static void Conflicts()
    {
        foreach (var records in new[] { new[] { Manual() with { TicketNumber = 8 } },
            new[] { Manual(), Manual() with { PurchaseType = BidPurchaseType.Personal } },
            new[] { Manual() with { ResultsAt = new(Received, BidFieldSource.Manual) } } })
        {
            var store = new Store { Data = new() { Characters = [Character], Bids = records } }; var tracker = new BidTracker(store);
            Equal(false, tracker.SubmitCapture(Character, Capture())); Equal(0, store.Saves); Equal(1, tracker.PendingCaptures.Count);
        }
    }
    private static void UnknownCycle()
    {
        var manual = Manual() with { CycleId = null, EnteredAt = null };
        var store = new Store { Data = new() { Characters = [Character], Bids = [manual] } }; var tracker = new BidTracker(store);
        Equal(false, tracker.SubmitCapture(Character, Capture()));
        store = new Store { Data = store.Data with { Bids = [manual with { ResultsAt = Capture().ResultsAt }] } }; tracker = new BidTracker(store);
        Equal(true, tracker.SubmitCapture(Character, Capture())); Equal(0L, tracker.Data.Bids.Single().CycleId);
    }
    private static void FailedSave()
    {
        var store = new Store { Fail = true }; var tracker = new BidTracker(store); var bid = Capture();
        Equal(false, tracker.SubmitCapture(Character, bid)); Equal(0, tracker.Data.Bids.Count); Equal(0, tracker.Data.Characters.Count);
        Equal(1, tracker.PendingCaptures.Count); tracker.ObserveCharacter(Character with { ContentId = 101 });
        store.Fail = false; Equal(true, tracker.RetryCapture(bid.Id)); Equal(0, tracker.PendingCaptures.Count);
        Equal(Character.ContentId, tracker.Data.Bids.Single().CharacterContentId); Equal(65, tracker.Data.Bids.Single().WorldId);
        Equal(Received, tracker.Data.Bids.Single().MessageEvidence!.ObservedAtUtc);
    }
    private static void DisabledTracking()
    {
        var store = new Store { Data = new() { ExcludedCharacters = [100] } }; var tracker = new BidTracker(store);
        Equal(false, tracker.SubmitCapture(Character, Capture())); Equal(0, tracker.PendingCaptures.Count); Equal(0, store.Saves);
        store = new Store { ReadOnly = true }; tracker = new BidTracker(store);
        Equal(false, tracker.SubmitCapture(Character, Capture())); Equal(1, tracker.PendingCaptures.Count); Equal(0, store.Saves);
        store = new Store(); tracker = new BidTracker(store); tracker.ObserveCharacter(Character); tracker.RemoveCharacter(100, false);
        Equal(false, tracker.SubmitCapture(Character, Capture())); Equal(0, tracker.PendingCaptures.Count);
    }
    private static void PendingBound()
    {
        var store = new Store { Fail = true }; var tracker = new BidTracker(store); var bid = Capture();
        tracker.SubmitCapture(Character, bid); tracker.SubmitCapture(Character, bid with { Id = Guid.NewGuid() });
        Equal(1, tracker.PendingCaptures.Count);
        for (var ticket = 8; ticket <= 40; ticket++) tracker.SubmitCapture(Character, bid with { Id = Guid.NewGuid(), TicketNumber = ticket });
        Equal(32, tracker.PendingCaptures.Count); Equal(bid.Id, tracker.PendingCaptures[0].Bid.Id);
        tracker.DismissCapture(bid.Id); Equal(31, tracker.PendingCaptures.Count);
    }
    private static void Enrichment()
    {
        var listing = new PaissaPlotListing(65, 341, "Goblet", 15, 53, 16, 54, 0, 3187500, 2, null, null, null, null, 123, null, null);
        var cache = new PaissaHousingWorldData(new(65, "Midgardsormr", 1, "Aether"), Received.AddHours(-1), [listing], []);
        var captured = BidCaptureEnrichment.Apply(Capture(), cache);
        Equal(0, captured.Size); Equal(3187500L, captured.Price); Equal(cache.DownloadedAtUtc, captured.CacheEvidence!.FetchedAtUtc);
        Equal(123d, captured.CacheEvidence.ReportUpdatedAtUnix); Equal<string?>(null, BidPolicy.Validate(captured));
        foreach (var invalid in new[] { cache with { Listings = [listing, listing] }, cache with { World = cache.World with { Id = 73 } },
            cache with { Listings = [listing with { WardNumber = 15 }] }, cache with { Listings = [listing with { PlotNumber = 53 }] },
            cache with { Listings = [listing with { DistrictId = 339 }] }, cache with { Listings = [listing with { WorldId = 73 }] } })
            Equal<BidCacheEvidence?>(null, BidCaptureEnrichment.Apply(Capture(), invalid).CacheEvidence);
        var unknown = BidCaptureEnrichment.Apply(Capture(), cache with { Listings = [listing with { Size = 9, Price = -1 }] });
        Equal<int?>(null, unknown.Size); Equal<long?>(null, unknown.Price);
    }
    private static void Migration()
    {
        var root = Path.Combine(Path.GetTempPath(), "PlotWatch-capture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "bids.json");
        try
        {
            var legacy = JsonSerializer.Serialize(new BidDocument { Version = 1, Characters = [Character], Bids = [Manual()] });
            File.WriteAllText(path, legacy); var store = new BidFileStore(path); var loaded = store.Load();
            Equal(false, loaded.ReadOnly); Equal(BidDocument.CurrentVersion, loaded.Data.Version); Equal(legacy, File.ReadAllText(path));
            var tracker = new BidTracker(store); Equal(true, tracker.SubmitCapture(Character, Capture()));
            Equal(tracker.Data.Bids.Single(), new BidFileStore(path).Load().Data.Bids.Single());
            var config = new Configuration { Version = 11 }; config.Normalize(); Equal(false, config.AutomaticallyRecordBids);
            config.AutomaticallyRecordBids = true; config.Normalize(); Equal(true, config.AutomaticallyRecordBids);
            config.ResetToDefaults(); Equal(false, config.AutomaticallyRecordBids);
        }
        finally { foreach (var file in Directory.GetFiles(root)) File.Delete(file); Directory.Delete(root); }
    }

    private static void ManualProvenance()
    {
        var tracker = new BidTracker(new Store());
        var captured = Capture() with { Size = 0, SizeFromCache = true, Price = 3187500, CacheEvidence = new(Received, 123, 0, 3187500) };
        Equal(true, tracker.SubmitCapture(Character, captured));
        Equal(true, tracker.SaveBid(captured with { Size = 2, DetailsSource = BidFieldSource.Manual }));
        Equal(false, tracker.Data.Bids.Single().SizeFromCache); Equal(0, tracker.Data.Bids.Single().CacheEvidence!.Size);
        Equal(true, tracker.SaveBid(tracker.Data.Bids.Single() with { Plot = 55 }));
        var moved = tracker.Data.Bids.Single();
        Equal<BidCacheEvidence?>(null, moved.CacheEvidence); Equal<BidMessageEvidence?>(null, moved.MessageEvidence);
        Equal<long?>(null, moved.Price);
    }
}
