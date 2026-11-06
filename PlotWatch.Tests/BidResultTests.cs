using PlotWatch.Data;
using PlotWatch.Lottery;
using PlotWatch.Diagnostics;

internal static class BidResultTests
{
    private const string TimeMacro = "<settime({0})><if(t_hour,<switch(t_hour,1,2,3,4,5,6,7,8,9,10,11,12,1,2,3,4,5,6,7,8,9,10,11)>,12)>:<sec(t_min)> <if([t_hour>=12],p.m.,a.m.)> <if([gnum77==3],<num(t_day)>/<num(t_mon)>/<num(t_year)>,<num(t_mon)>/<num(t_day)>/<num(t_year)>)>.";
    private static readonly string LossMacro = "The winning number is...<num(lnum1)>. May you have better luck in the future!<br><br>Accept a full refund of your deposit of <kilo(lnum2,\\,)> gil?<br>※Refund available until " + string.Format(TimeMacro, "lnum3");
    private static readonly string WinMacro = "The winning number is...<num(lnum1)>!<br><br>Congratulations! You are the winner of this lottery. Finalize your purchase and claim your plot of land?<br>※Plot available until " + string.Format(TimeMacro, "lnum2");
    private static readonly string SoleMacro = "Congratulations! As the sole entrant of this lottery, you have won by default.<br>※Plot available until " + string.Format(TimeMacro, "lnum2");
    private static readonly IReadOnlyDictionary<uint, string> Templates = new Dictionary<uint, string>
        { [7122] = WinMacro, [7124] = LossMacro, [7128] = SoleMacro };
    private static readonly DateTimeOffset Observed = DateTimeOffset.Parse("2026-11-06T16:26:13Z");
    private const ulong CharacterId = 100;
    private const int WorldId = 54;
    private static readonly TimeZoneInfo Central = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    private static readonly string AddressMacro = "Plot <num(lnum3)>, <ordinal(lnum2)> Ward, <sheet(PlaceName,lnum1,0)> (<switch(lnum4,Small,Medium,Large)>)";
    private static readonly IReadOnlyDictionary<int, string> DistrictNames = new Dictionary<int, string> { [341] = "The Goblet" };

    public static readonly (string Name, Action Test)[] Cases =
    [
        ("result parser matches complete losing refund dialog and extracts winning number", ParseLoss),
        ("result parser matches win and sole entrant without inventing winner number", ParseWin),
        ("result parser rejects truncated quoted unrelated and tampered dialogs", ParseReject),
        ("result parser evaluates only installed language template shape", Localized),
        ("placard address parser extracts localized full address and size", AddressParse),
        ("placard address parser rejects partial invalid ambiguous and wrong district text", AddressReject),
        ("result capture uniquely records loss without claiming refund", UpdateLoss),
        ("result capture records winner but never claims plot", UpdateWin),
        ("result capture sole entry does not invent winning number", UpdateSole),
        ("result capture preserves explicit outcomes and rejects ambiguity", PreserveAndAmbiguous),
        ("result capture rejects other cycles characters worlds and unknown ticket", NoMatch),
        ("result capture save failure leaves record untouched", SaveFailure),
        ("missing result bid creates review candidate without saving", RecoveryCandidate),
        ("recovery candidate preserves correct ticket semantics and reviewed save", RecoverySave),
        ("recovery candidates deduplicate and remain bounded", RecoveryBounds),
        ("unverified result deadline keeps cycle unknown for review", RecoveryUnknownCycle),
    ];

    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }

    private static string LossText(int winner = 5) => $"The winning number is...{winner}. May you have better luck in the future!\n\nAccept a full refund of your deposit of 3,000,000 gil?\n※Refund available until 8:59 a.m. 2/8/2027.";
    private static string WinText(int winner = 7) => $"The winning number is...{winner}!\n\nCongratulations! You are the winner of this lottery. Finalize your purchase and claim your plot of land?\n※Plot available until 8:59 a.m. 11/10/2026.";
    private static string SoleText() => "Congratulations! As the sole entrant of this lottery, you have won by default.\n※Plot available until 8:59 a.m. 11/10/2026.";
    private static BidResultMatch? Parse(string text) => BidResultParser.Match(text, Templates, Central, 0);

    private static void ParseLoss()
    { var result = Parse(LossText()) ?? throw new Exception("No result match."); Equal(7124U, result.TemplateRow); Equal(5, result.WinningNumber); Equal(0L, result.CycleId); }
    private static void ParseWin()
    {
        var win = Parse(WinText()) ?? throw new Exception("No winner match."); Equal(7122U, win.TemplateRow); Equal(7, win.WinningNumber); Equal(0L, win.CycleId);
        var sole = Parse(SoleText()) ?? throw new Exception("No sole-entry match."); Equal(7128U, sole.TemplateRow); Equal<int?>(null, sole.WinningNumber); Equal(0L, sole.CycleId);
    }
    private static void ParseReject()
    {
        foreach (var text in new[] { "Quote: " + LossText(), LossText() + " extra", LossText()[..^1],
            LossText().Replace("May you have better luck in the future!", "Good luck!"),
            "There was one participant in this lottery.", "The results period has ended. Refund accepted." })
            Equal<BidResultMatch?>(null, Parse(text));
        Equal<BidResultMatch?>(null, BidResultParser.Match(LossText(), new Dictionary<uint, string> { [7124] = LossMacro + "<unknown(x)>" }));
    }
    private static void Localized()
    {
        var macro = LossMacro.Replace("May you have better luck in the future!", "Viel Glück beim nächsten Mal!");
        Equal(5, BidResultParser.Match(LossText().Replace("May you have better luck in the future!", "Viel Glück beim nächsten Mal!"),
            new Dictionary<uint, string> { [7124] = macro })!.WinningNumber);
        Equal<BidResultMatch?>(null, BidResultParser.Match(LossText(), new Dictionary<uint, string> { [7124] = macro }));
    }
    private static void AddressParse()
    {
        var parsed = BidAddressParser.Match("Plot 2, 26th Ward, The Goblet (Small)", AddressMacro, DistrictNames)
            ?? throw new Exception("Complete address did not parse.");
        Equal(341, parsed.DistrictId); Equal(26, parsed.Ward); Equal(2, parsed.Plot); Equal(0, parsed.Size);
        var noSize = BidAddressParser.Match("Plot 60, 30th Ward, The Goblet", AddressMacro, DistrictNames)
            ?? throw new Exception("Address without optional size did not parse.");
        Equal<int?>(null, noSize.Size);
        var translated = AddressMacro.Replace("Plot", "Parcelle").Replace("Ward", "secteur").Replace("The", "Le").Replace("Goblet", "Lavandière")
            .Replace("Small", "petite").Replace("Medium", "moyenne").Replace("Large", "grande");
        Equal(2, BidAddressParser.Match("Parcelle 2, 26th secteur, Le Lavandière (petite)", translated,
            new Dictionary<int, string> { [341] = "Le Lavandière" })!.Plot);
    }
    private static void AddressReject()
    {
        foreach (var text in new[] { "Plot 2, 26th Ward", "Plot 0, 26th Ward, The Goblet", "Plot 61, 26th Ward, The Goblet",
            "Plot 2, 31st Ward, The Goblet", "Plot 2, 26th Ward, The Mist", "Quote: Plot 2, 26th Ward, The Goblet" })
            Equal<BidAddressMatch?>(null, BidAddressParser.Match(text, AddressMacro, DistrictNames));
        Equal<BidAddressMatch?>(null, BidAddressParser.Match("Plot 2, 26th Ward, The Goblet",
            AddressMacro, new Dictionary<int, string> { [339] = "The Goblet", [341] = "The Goblet" }));
    }

    private sealed class Store(BidDocument initial, bool fail = false) : IBidStore
    {
        public BidDocument Data = initial;
        public int Saves;
        public BidLoadResult Load() => new(Data);
        public void Save(BidDocument next) { if (fail) throw new IOException(); Data = next; Saves++; }
        public void RestoreBackup(BidDocument data) => Save(data);
    }
    private static BidRecord Bid(ulong character = CharacterId, int world = WorldId, long cycle = 0, int? ticket = 7,
        BidOutcome outcome = BidOutcome.Pending, BidFieldSource source = BidFieldSource.GameObserved, int plot = 54) => new()
    {
        Id = Guid.NewGuid(), CharacterContentId = character, WorldId = world, WorldName = "World", DistrictId = 341,
        Ward = 16, Plot = plot, PurchaseType = BidPurchaseType.Unknown, CycleId = cycle, TicketNumber = ticket,
        Outcome = outcome, OutcomeSource = source, DetailsSource = BidFieldSource.GameObserved,
        TicketSource = BidFieldSource.GameObserved,
    };
    private static TrackedCharacter Character(ulong id = CharacterId) => new(id, "Result Test", 54, "World");
    private static BidTracker Tracker(Store store) => new(store);
    private static BidResultEvidence Evidence(BidResultMatch match, string text) => new(Observed, "English",
        new string('b', 64), match.TemplateRow, text);
    private static BidAddressEvidence Address(int plot = 54, int ward = 16, int district = 341) => new(Observed,
        "English", new string('c', 64), district, ward, plot, 0, $"Plot {plot}, {ward}th Ward, The Goblet (Small)");
    private static BidResultMatch Match(string text) => Parse(text) ?? throw new Exception("Result dialog did not match.");
    private static bool Observe(BidTracker tracker, BidResultMatch match, string text, int plot = 54) =>
        tracker.ObserveResult(Character(), WorldId, "World", match, Evidence(match, text), Address(plot));

    private static void UpdateLoss()
    {
        var bid = Bid(); var store = new Store(new() { Characters = [Character()], Bids = [bid] }); var tracker = Tracker(store);
        var text = LossText(); var match = Match(text);
        Equal(true, Observe(tracker, match, text));
        var saved = tracker.Data.Bids.Single(); Equal(bid.Id, saved.Id); Equal(BidOutcome.Lost, saved.Outcome);
        Equal(BidFieldSource.GameObserved, saved.OutcomeSource); Equal(5, saved.WinningNumber); Equal(7124U, saved.ResultEvidence!.TemplateRow);
        Equal(1, store.Saves); Equal(false, BidPolicy.IsActive(saved));
    }
    private static void UpdateWin()
    {
        var bid = Bid(); var store = new Store(new() { Characters = [Character()], Bids = [bid] }); var tracker = Tracker(store);
        var text = WinText(); var match = Match(text);
        Equal(true, Observe(tracker, match, text));
        Equal(BidOutcome.Won, tracker.Data.Bids.Single().Outcome); Equal(7, tracker.Data.Bids.Single().WinningNumber);
        Equal(BidOutcome.Won, tracker.Data.Bids.Single().Outcome); Equal(null, tracker.Data.Bids.Single().ClaimDeadline);
    }
    private static void UpdateSole()
    {
        var bid = Bid(ticket: 7); var store = new Store(new() { Characters = [Character()], Bids = [bid] }); var tracker = Tracker(store);
        var text = SoleText(); var match = Match(text);
        Equal(true, Observe(tracker, match, text));
        Equal(BidOutcome.Won, tracker.Data.Bids.Single().Outcome); Equal<int?>(null, tracker.Data.Bids.Single().WinningNumber);
        Equal<int?>(null, tracker.Data.Bids.Single().TicketNumber is null ? null : tracker.Data.Bids.Single().WinningNumber);
    }
    private static void PreserveAndAmbiguous()
    {
        foreach (var outcome in new[] { BidOutcome.Won, BidOutcome.Lost, BidOutcome.Claimed })
        {
            var store = new Store(new() { Characters = [Character()], Bids = [Bid(outcome: outcome, source: BidFieldSource.Manual)] });
            var tracker = Tracker(store); var text = LossText(); var match = Match(text);
            Equal(false, Observe(tracker, match, text));
            Equal(outcome, tracker.Data.Bids.Single().Outcome); Equal(0, store.Saves);
        }
        var first = Bid(plot: 54); var second = Bid(plot: 55);
        var store2 = new Store(new() { Characters = [Character()], Bids = [first, second] }); var tracker2 = Tracker(store2);
        var loss = LossText(); var parsed = Match(loss);
        Equal(true, Observe(tracker2, parsed, loss));
        Equal(1, store2.Saves); Equal(BidOutcome.Lost, tracker2.Data.Bids.Single(bid => bid.Plot == 54).Outcome);
        Equal(BidOutcome.Pending, tracker2.Data.Bids.Single(bid => bid.Plot == 55).Outcome);
        // Same address and cycle with competing records remains ambiguous.
        var competing = new Store(new() { Characters = [Character()], Bids = [first, first with { Id = Guid.NewGuid(), TicketNumber = 5 }] });
        var competingTracker = Tracker(competing);
        Equal(false, Observe(competingTracker, parsed, loss));
        Equal(0, competing.Saves);
        var excluded = new Store(new() { Characters = [Character()], Bids = [Bid()], ExcludedCharacters = [CharacterId] });
        var excludedTracker = Tracker(excluded);
        Equal(false, Observe(excludedTracker, parsed, loss));
        Equal(0, excluded.Saves);
    }
    private static void NoMatch()
    {
        foreach (var bid in new[] { Bid(cycle: -1), Bid(character: 101), Bid(world: 73) })
        {
            var store = new Store(new() { Characters = [Character(), Character(101)], Bids = [bid] }); var tracker = Tracker(store);
            var text = LossText(); var parsed = Match(text);
            Equal(false, Observe(tracker, parsed, text)); Equal(0, store.Saves);
        }
        var winner = WinText(8); var match = Match(winner);
        var storeWrong = new Store(new() { Characters = [Character()], Bids = [Bid(ticket: 7)] }); var trackerWrong = Tracker(storeWrong);
        Equal(false, Observe(trackerWrong, match, winner));
        var noTicket = new Store(new() { Characters = [Character()], Bids = [Bid(ticket: null)] });
        Equal(false, Observe(Tracker(noTicket), match, winner));
    }
    private static void SaveFailure()
    {
        var bid = Bid(); var store = new Store(new() { Characters = [Character()], Bids = [bid] }, fail: true); var tracker = Tracker(store);
        var text = LossText(); var match = Match(text);
        Equal(false, Observe(tracker, match, text));
        Equal(BidOutcome.Pending, tracker.Data.Bids.Single().Outcome); Equal(null, tracker.Data.Bids.Single().ResultEvidence);
    }

    private static void RecoveryCandidate()
    {
        var store = new Store(new() { Characters = [Character()] }); var tracker = Tracker(store);
        var text = LossText(); var match = Match(text);
        Equal(false, Observe(tracker, match, text)); Equal(0, store.Saves); Equal(1, tracker.PendingResultRecoveries.Count);
        var candidate = tracker.PendingResultRecoveries.Single();
        Equal(BidOutcome.Lost, candidate.Bid.Outcome); Equal<int?>(null, candidate.Bid.TicketNumber);
        Equal(5, candidate.Bid.WinningNumber); Equal(341, candidate.Bid.DistrictId); Equal(54, candidate.Bid.Plot);
        Equal(0L, candidate.Bid.CycleId); Equal(true, candidate.Reason.StartsWith("No uniquely matching", StringComparison.Ordinal));
    }
    private static void RecoverySave()
    {
        var store = new Store(new() { Characters = [Character()] }); var tracker = Tracker(store);
        var winText = WinText(4); Observe(tracker, Match(winText), winText, plot: 2);
        var win = tracker.PendingResultRecoveries.Single();
        Equal(4, win.Bid.TicketNumber); Equal(4, win.Bid.WinningNumber); Equal(BidOutcome.Won, win.Bid.Outcome);
        Equal(null, win.Bid.ClaimDeadline); Equal(2, win.Bid.Plot); Equal(0L, win.Bid.CycleId);
        Equal(true, tracker.SaveBid(win.Bid)); Equal(1, store.Saves); Equal(0, tracker.PendingResultRecoveries.Count);
        var lostText = LossText(); Observe(tracker, Match(lostText), lostText, plot: 3);
        var loss = tracker.PendingResultRecoveries.Single();
        Equal<int?>(null, loss.Bid.TicketNumber); Equal(5, loss.Bid.WinningNumber); Equal(BidOutcome.Lost, loss.Bid.Outcome);
        Equal(true, tracker.SaveBid(loss.Bid)); Equal(2, store.Saves); Equal(2, tracker.Data.Bids.Count);
    }
    private static void RecoveryBounds()
    {
        var store = new Store(new() { Characters = [Character()] }); var tracker = Tracker(store);
        var text = LossText(); var match = Match(text);
        Observe(tracker, match, text); Observe(tracker, match, text);
        Equal(1, tracker.PendingResultRecoveries.Count); Equal(0, store.Saves);
        for (var index = 1; index < 40; index++)
        {
            var plot = index + 1;
            var evidence = Address(plot: plot);
            var lossEvidence = Evidence(match, text);
            tracker.ObserveResult(Character(), WorldId, "World", match with { WinningNumber = 5 }, lossEvidence, evidence);
        }
        Equal(32, tracker.PendingResultRecoveries.Count); Equal(54, tracker.PendingResultRecoveries[0].Bid.Plot);
    }
    private static void RecoveryUnknownCycle()
    {
        var store = new Store(new() { Characters = [Character()] }); var tracker = Tracker(store);
        // Unrecognized timestamp form cannot map to a schedule boundary, but complete result + address
        // remains reviewable and must not be assigned cycle from observation time.
        var text = LossText().Replace("2/8/2027", "8/2/2027");
        var match = BidResultParser.Match(text, Templates, Central, 0) ?? throw new Exception("Complete dialog should remain recognizable.");
        Equal<long?>(null, match.CycleId);
        tracker.ObserveResult(Character(), WorldId, "World", match, Evidence(match, text), Address());
        Equal<long?>(null, tracker.PendingResultRecoveries.Single().Bid.CycleId); Equal(0, store.Saves);
    }
}
