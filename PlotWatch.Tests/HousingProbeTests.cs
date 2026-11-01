using PlotWatch.Diagnostics;

internal static class HousingProbeTests
{
    public static readonly (string Name, Action Test)[] Cases =
    [
        ("housing probe is opt-in and stops at exact monotonic deadline", Expiry),
        ("housing probe ignores wall-clock jumps for duration", ClockJump),
        ("housing probe stops on logout and same-world character switch", CharacterChange),
        ("housing probe records world/territory boundary without identity disclosure", ContextChange),
        ("housing probe stop blocks further observations", Stop),
        ("housing probe clear stops and preserves templates", Clear),
        ("housing probe history and verbose output are bounded", BoundedHistory),
        ("housing probe template analysis preserves nested local references", TemplateReferences),
        ("housing probe candidates withhold raw text and long identifiers", CandidatePrivacy),
        ("housing probe does not claim candidate matches are native identities", CandidateSemantics),
        ("housing probe matches conditional sheet literals without evaluating them", ConditionalTemplates),
        ("housing probe report includes evidence without private identity", ReportPrivacy),
        ("housing probe snapshot is stable after later observations", Snapshot),
        ("housing probe rejects label-only and incomplete addresses", CompleteAddresses),
        ("housing probe matches localized template address structure", LocalizedAddresses),
        ("housing probe chunks large snapshots without truncating late values", SnapshotParts),
        ("housing probe events-only report omits templates", EventsOnly),
        ("housing probe recognizes combined placard eligibility and status messages", CombinedPlacardMessages),
        ("housing probe inline matches require complete bounded static messages", InlineMessageBoundaries),
        ("housing probe inline matching does not broaden chat or outcome candidates", InlineScope),
    ];

    private static readonly DateTimeOffset Now = new(2026, 10, 30, 15, 0, 0, TimeSpan.Zero);
    private static HousingProbeContext Context() => new(true, 73, 73, 339, "English");
    private static HousingProbeState NewState(Action<HousingProbeLogLevel, string>? log = null)
    {
        var state = new HousingProbeState(log);
        state.UpdateContext(Context(), 998877665544332211, 1000, Now);
        return state;
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; got {actual}.");
    }

    private static void Expiry()
    {
        var state = NewState();
        Equal(false, state.Record("Event", "ignored", 1000, Now));
        state.Arm(new(), 1000, Now);
        Equal(true, state.IsActive(15999, Now.AddSeconds(100)));
        Equal(TimeSpan.FromMilliseconds(1), state.View.Remaining);
        Equal(false, state.Record("Event", "too late", 16000, Now));
        Equal(false, state.View.Armed);
        Equal(TimeSpan.Zero, state.View.Remaining);
        Equal(false, state.View.Entries.Any(entry => entry.Detail == "too late"));
        Equal(true, state.View.Status.StartsWith("Expired"));
    }

    private static void ClockJump()
    {
        var state = NewState();
        state.Arm(new(), 1000, Now);
        Equal(true, state.IsActive(2000, Now.AddDays(5)));
        Equal(TimeSpan.FromSeconds(14), state.View.Remaining);
        Equal(true, state.IsActive(3000, Now.AddDays(-5)));
        Equal(TimeSpan.FromSeconds(13), state.View.Remaining);
        Equal(false, state.IsActive(16000, Now.AddDays(-5)));
    }

    private static void CharacterChange()
    {
        var state = NewState();
        state.Arm(new(), 1000, Now);
        state.UpdateContext(Context(), 111222333444555666, 2000, Now);
        Equal(false, state.View.Armed);
        Equal(true, state.View.Status.Contains("character changed"));
        state.Arm(new(), 2000, Now);
        state.UpdateContext(Context() with { CharacterAvailable = false }, 0, 2100, Now);
        Equal(false, state.View.Armed);
    }

    private static void ContextChange()
    {
        var state = NewState();
        state.Arm(new(), 1000, Now);
        state.UpdateContext(Context() with { CurrentWorld = 99, Territory = 340 }, 998877665544332211, 2000, Now);
        Equal(true, state.View.Armed);
        Equal(true, state.View.Entries.Any(entry => entry.Source == "Context" && entry.Detail.Contains("invalidated")));
        Equal(false, state.BuildReport("test").Contains("998877665544332211"));
    }

    private static void Stop()
    {
        var state = NewState();
        state.Arm(new(), 1000, Now);
        state.Stop(Now);
        var count = state.View.Entries.Count;
        Equal(false, state.Record("Event", "ignored", 1100, Now));
        state.Stop(Now);
        Equal(count, state.View.Entries.Count);
        Equal(false, state.View.Armed);
    }

    private static HousingProbeTemplate Template(string macro = "You have submitted a lottery entry for plot <num(lnum3)>, ward <num(lnum2)>, <sheet(TerritoryType,lnum1,5)>. Ticket <num(lnum4)>. Results <time(lnum5)>.")
        => HousingProbeState.DescribeTemplate("custom/001/CmnDefHousingSignboard_00135", 6, 1, "English", macro, "Macro:Num,Macro:Sheet");

    private static void Clear()
    {
        var state = NewState();
        state.SetTemplates([Template()], Now);
        state.Arm(new(), 1000, Now);
        state.Record("Event", "before clear", 1001, Now);
        state.Clear(Now);
        Equal(false, state.View.Armed);
        Equal(0, state.View.Entries.Count);
        Equal(1, state.View.Templates.Count);
        Equal(false, state.Record("Event", "after clear", 1002, Now));
    }

    private static void BoundedHistory()
    {
        var logs = new List<(HousingProbeLogLevel Level, string Text)>();
        var state = NewState((level, text) => logs.Add((level, text)));
        state.Arm(new(), 1000, Now);
        for (var index = 0; index < 1000; index++) state.Record("Event", "event=" + index, 1001, Now);
        Equal(HousingProbeState.Capacity, state.View.Entries.Count);
        Equal(true, state.View.Dropped > 0);
        Equal(true, logs.Count <= HousingProbeState.MaximumEventsPerProbe + 1);
        state.Stop(Now);
        Equal(true, state.View.Entries[^1].Detail.Contains("Rate-limit-suppressed="));
        state.Arm(new(), 2000, Now);
        state.Record("Event", new string('x', 8000) + "\n", 2001, Now);
        Equal(true, state.View.Entries[^1].Detail.Contains("[truncated]"));
        Equal(false, state.View.Entries[^1].Detail.Contains('\n'));
    }

    private static void TemplateReferences()
    {
        var template = Template();
        Equal("1,2,3,4,5", string.Join(",", template.LocalNumbers));
        Equal("You have submitted a lottery entry for plot", template.Prefix);
        var conditional = Template("Type of Entry: <switch(lnum1,Free Company Representative,Private Buyer)>");
        Equal("1", string.Join(",", conditional.LocalNumbers));
        Equal(true, conditional.Macro.Contains("Private Buyer"));
        Equal("", Template("<switch(lnum1,first,second)>").Prefix);
    }

    private static void CandidatePrivacy()
    {
        var text = "You have submitted a lottery entry for plot 12, ward 3, Private Character Name 998877665544332211. Ticket 41.";
        var candidate = HousingProbeState.DescribeCandidate(text, [Template()])!;
        Equal(true, candidate.Contains("12,3"));
        Equal(true, candidate.Contains("[long-number-redacted]"));
        Equal(false, candidate.Contains("Private Character Name"));
        Equal(false, candidate.Contains("998877665544332211"));
        var type = HousingProbeState.DescribeCandidate("Type of Entry: Private Buyer", [Template("Type of Entry: <switch(lnum1,Free Company Representative,Private Buyer)>")])!;
        Equal(true, type.Contains("static-evidence=[Private Buyer]"));
    }

    private static void CandidateSemantics()
    {
        Equal<string?>(null, HousingProbeState.DescribeCandidate("Hello, unrelated chat.", [Template()]));
        Equal<string?>(null, HousingProbeState.DescribeCandidate("", [Template()]));
        Equal<string?>(null, HousingProbeState.DescribeCandidate(new string('x', 5000), [Template()]));
        var candidate = HousingProbeState.DescribeCandidate("You have submitted a lottery entry for plot 1, ward 2, Mist.", [Template()])!;
        Equal(true, candidate.Contains("NOT a native message ID"));
        Equal(true, candidate.Contains("parameter mapping"));
    }

    private static void ReportPrivacy()
    {
        var state = NewState();
        state.SetTemplates([Template()], Now);
        state.Arm(new(true, 6099, true), 1000, Now);
        state.Record("LogMessage", "LogMessage-sheet-ID=6099; param-count=0; metadata only", 1001, Now);
        var report = state.BuildReport("build-test");
        Equal(true, report.Contains("build-test"));
        Equal(true, report.Contains("READ-ONLY"));
        Equal(true, report.Contains("lnums=[1,2,3,4,5]"));
        Equal(true, report.Contains("numeric-log-ID=6099"));
        Equal(false, report.Contains("998877665544332211"));
        Equal(true, report.Contains("LogMessage-sheet-ID=6099"));
    }

    private static void ConditionalTemplates()
    {
        var deadline = Template("<switch(lnum2,Accepting entries until <time(lnum1)>,Results period ends <time(lnum1)>)>") with
        {
            BranchPrefixes = new[] { "Accepting entries until", "Results period ends" },
        };
        var candidate = HousingProbeState.DescribeCandidate("Results period ends 10:00 a.m. 11/1/2026.", [deadline])!;
        Equal(true, candidate.Contains("matched-literals=[Results period ends]"));
        Equal(true, candidate.Contains("numeric-text-tokens=[10,00,11,1,2026]"));
        var address = AddressTemplate();
        Equal<string?>(null, HousingProbeState.DescribeCandidate("Plot 1, 2nd Ward, Mist", [address]));
        Equal(true, HousingProbeState.DescribeCandidate("Plot 1, 2nd Ward, Mist", [address], allowShortPrefixes: true) is not null);
        Equal(true, HousingProbeState.DescribeCandidate("Plot 1, 2nd Ward, Mist (Small)", [address], allowShortPrefixes: true)!.Contains("literal-fragments-present=[Plot | Small]"));
    }

    private static void Snapshot()
    {
        var state = NewState();
        state.Arm(new(), 1000, Now);
        var earlier = state.View;
        state.Record("Event", "later", 1001, Now);
        Equal(1, earlier.Entries.Count);
        Equal(2, state.View.Entries.Count);
        Equal(false, earlier.Options.AllLogMessageIds);
    }

    private static HousingProbeTemplate AddressTemplate()
        => HousingProbeState.DescribeTemplate("Addon", 8847, 0, "English",
            "Plot <num(lnum3)>, <ordinal(lnum2)> Ward, <sheet(PlaceName,lnum1,0)> (<switch(lnum4,Small,Medium,Large)>)", "address")
            with { BranchPrefixes = new[] { "Plot", "Small", "Medium", "Large" } };

    private static void CompleteAddresses()
    {
        var template = AddressTemplate();
        foreach (var label in new[] { "Plot Details", "Plot Size", "Plot", "Plot 19", "Plot 19, 5th Ward", "Plot 0, 5th Ward, Mist", "Plot 61, 5th Ward, Mist", "Plot 19, 31st Ward, Mist", "Plot 19, 5th Ward, Mist trailing invalid !!!" })
            Equal<string?>(null, HousingProbeState.DescribeCandidate(label, [template], allowShortPrefixes: true));
        foreach (var address in new[] { "Plot 19, 5th Ward, Mist", "Plot 19, 5th Ward, Mist (Small)", "Plot 60, 30th Ward, The Lavender Beds (Large)" })
            Equal(true, HousingProbeState.DescribeCandidate(address, [template], allowShortPrefixes: true) is not null);
        Equal(false, HousingProbeFormatting.IsCompleteAddress("Plot 19, 5th Ward, Mist", "<unknown(lnum1)>"));
    }

    private static void LocalizedAddresses()
    {
        var german = "Grundstück <num(lnum3)>, <ordinal(lnum2)> Bezirk, <sheet(PlaceName,lnum1,0)> (<switch(lnum4,Klein,Mittel,Groß)>)";
        Equal(true, HousingProbeFormatting.IsCompleteAddress("Grundstück 19, 5. Bezirk, Dorf des Nebels (Klein)", german));
        Equal(false, HousingProbeFormatting.IsCompleteAddress("Grundstücksgröße", german));
        var japanese = "<num(lnum2)>区 <num(lnum3)>番地 <sheet(PlaceName,lnum1,0)>（<switch(lnum4,小型,中型,大型)>）";
        Equal(true, HousingProbeFormatting.IsCompleteAddress("5区 19番地 ミスト・ヴィレッジ（小型）", japanese));
    }

    private static void SnapshotParts()
    {
        var details = Enumerable.Range(0, 313).Select(index => $"value[{index}:Int={index}]")
            .Append("nested deadline evidence " + new string('x', 9000)).ToArray();
        var body = string.Join("; ", details);
        const string header = "addon=ContentsInfo:122";
        var parts = HousingProbeFormatting.SnapshotParts(header, details);
        Equal(true, parts.Count > 1);
        Equal(true, parts.All(part => part.Length < HousingProbeState.MaximumDetailLength));
        var rebuilt = string.Concat(parts.Select((part, index) => part[($"{header}; part={index + 1}/{parts.Count}; ").Length..]));
        Equal(body, rebuilt);
        Equal(true, rebuilt.Contains("value[312:Int=312]"));
        Equal(true, HousingProbeFormatting.MaximumAtkValues >= 313);
    }

    private static void EventsOnly()
    {
        var state = NewState();
        state.SetTemplates([Template()], Now);
        state.Arm(new(), 1000, Now);
        state.Record("Addon", "late value evidence", 1001, Now);
        var report = state.BuildReport("test", includeTemplates: false);
        Equal(false, report.Contains("TEMPLATE "));
        Equal(true, report.Contains("late value evidence"));
        Equal(true, report.Contains("READ-ONLY"));
    }

    private static HousingProbeTemplate[] EligibilityTemplates()
        => new[]
        {
            HousingProbeState.DescribeTemplate("Addon", 7040, 0, "English",
                "<switch(lnum1,Available to free companies.,Unavailable to free companies.)>", "switch") with
            {
                BranchPrefixes = new[] { "Available to free companies.", "Unavailable to free companies." },
                StaticMessages = new[] { "Available to free companies.", "Unavailable to free companies." },
            },
            HousingProbeState.DescribeTemplate("Addon", 7041, 0, "English",
                "<switch(lnum1,Available to private buyers.,Unavailable to private buyers.)>", "switch") with
            {
                BranchPrefixes = new[] { "Available to private buyers.", "Unavailable to private buyers." },
                StaticMessages = new[] { "Available to private buyers.", "Unavailable to private buyers." },
            },
            HousingProbeState.DescribeTemplate("Addon", 7043, 0, "English",
                "<switch(lnum1,Selling on a first-come-first-served basis.,Selling via lottery. (Current participants: <num(lnum2)>),Currently unavailable for purchase.)>", "switch") with
            {
                BranchPrefixes = new[] { "Selling on a first-come-first-served basis.", "Selling via lottery. (Current participants:", "Currently unavailable for purchase." },
                StaticMessages = new[] { "Selling on a first-come-first-served basis.", "Currently unavailable for purchase." },
            },
        };

    private static void CombinedPlacardMessages()
    {
        var screenshot = "Available to free companies. Available to private buyers.\nCurrently unavailable for purchase.";
        foreach (var text in new[] { screenshot, screenshot.Replace('\n', ' '), screenshot.Replace(". ", ".\n") })
        {
            var candidate = HousingProbeState.DescribeCandidate(text, EligibilityTemplates())!;
            foreach (var id in new[] { 7040, 7041, 7043 }) Equal(true, candidate.Contains($"Addon/{id}"));
            Equal(true, candidate.Contains("Currently unavailable for purchase."));
            Equal(true, candidate.Contains("Available to private buyers."));
            Equal(false, candidate.Contains("Unavailable to private buyers."));
            Equal(true, candidate.Contains("NOT a native message ID"));
        }
    }

    private static void InlineMessageBoundaries()
    {
        var template = EligibilityTemplates()[1];
        Equal<string?>(null, HousingProbeState.DescribeCandidate("prefixAvailable to private buyers.suffix", [template]));
        Equal<string?>(null, HousingProbeState.DescribeCandidate("Other sentence. Available to private buy", [template]));
        Equal(true, HousingProbeState.DescribeCandidate("Other sentence. Unavailable to private buyers.", [template])!.Contains("Unavailable to private buyers."));
        var repeated = HousingProbeState.DescribeCandidate("Note. Available to private buyers. Available to private buyers.", [template])!;
        Equal(true, repeated.Contains("matched-literals=[Available to private buyers.]"));
    }

    private static void InlineScope()
    {
        var chat = Template() with { StaticMessages = new[] { "You have submitted a lottery entry" } };
        Equal<string?>(null, HousingProbeState.DescribeCandidate("Quoted: You have submitted a lottery entry", [chat]));
        var outcome = HousingProbeState.DescribeTemplate("Addon", 7122, 0, "English", "Congratulations! You are the winner.", "text") with
        { StaticMessages = new[] { "Congratulations! You are the winner." } };
        Equal<string?>(null, HousingProbeState.DescribeCandidate("Quoted: Congratulations! You are the winner.", [outcome]));
        Equal<string?>(null, HousingProbeState.DescribeCandidate("Plot Details", [AddressTemplate()], allowShortPrefixes: true));
    }
}
