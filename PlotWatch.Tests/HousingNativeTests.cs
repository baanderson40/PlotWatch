using System.Text;
using PlotWatch.Diagnostics;
using PlotWatch.Lottery;

internal static class HousingNativeTests
{
    public static readonly (string Name, Action Test)[] Cases =
    [
        ("native housing probe requires exact version hash and layouts", Compatibility),
        ("native housing probe matches exact template bytes not instructions or errors", TemplateIdentity),
        ("native housing evidence decodes only typed bounded success-scene candidates", Candidate),
        ("native housing evidence rejects missing mistyped indirect and shifted arguments", Arguments),
        ("native housing evidence rejects invalid addresses times and scene context", InvalidCandidates),
        ("native housing buffer invalidates character world territory and probe changes", ContextChanges),
        ("native housing buffer bounds duplicates pending storage and event count", Bounds),
        ("native housing report withholds identities pointers strings and failure payloads", Privacy),
    ];
    private static readonly DateTimeOffset Now = LotterySchedule.ApplicationAnchorUtc.AddHours(5);
    private static HousingNativeContext Context => new(998877665544332211, new(true, 65, 65, 339, "English"));
    private static HousingNativeParameter[] Parameters => new[] { 339, 5, 11, 7, (int)LotterySchedule.ApplicationAnchorUtc.AddDays(5).ToUnixTimeSeconds() }
        .Select((value, index) => new HousingNativeParameter(index, 0, value, true)).ToArray();
    private static HousingNativeObservation Observation => new(1, 1, Now, Context,
        HousingNativeEvidence.HousingEventId, 0, 1, Parameters);
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}.");
    }
    private static void Rejected(HousingNativeObservation observation)
    {
        var text = HousingNativeEvidence.Describe(observation);
        Equal(true, text.Contains("interpretation=withheld"));
        Equal(false, text.Contains("candidate-fields:"));
    }

    private static void Compatibility()
    {
        var version = HousingNativeCompatibility.GameVersion;
        var hash = HousingNativeCompatibility.ExecutableSha256;
        Equal(true, HousingNativeCompatibility.Allows(version, hash, true));
        Equal(false, HousingNativeCompatibility.Allows("2026.10.06.0000.0000", hash, true));
        Equal(false, HousingNativeCompatibility.Allows(version, "wrong", true));
        Equal(false, HousingNativeCompatibility.Allows(version, hash, false));
    }

    private static void TemplateIdentity()
    {
        var success = Encoding.UTF8.GetBytes("localized-success-template-with-payloads\u0002\u0010\u0003");
        Equal(true, HousingNativeCompatibility.MatchesSuccessTemplate(success.ToArray(), success));
        foreach (var other in new[] { "instruction-template", "existing-entry-guide", "world-travel-error", "rendered-success-text" })
            Equal(false, HousingNativeCompatibility.MatchesSuccessTemplate(Encoding.UTF8.GetBytes(other), success));
        Equal(false, HousingNativeCompatibility.MatchesSuccessTemplate(success[..^1], success));
        Equal(false, HousingNativeCompatibility.MatchesSuccessTemplate([], []));
        Equal(false, HousingNativeCompatibility.MatchesSuccessTemplate(new byte[1025], new byte[1025]));
    }

    private static void Candidate()
    {
        var detail = HousingNativeEvidence.Describe(Observation);
        Equal(true, detail.Contains("district=339; ward=5; plot=11; ticket=7"));
        Equal(true, detail.Contains("results-UTC=2026-11-06T15:00:00"));
        Equal(true, detail.Contains("matches-global-entry-close=True"));
        Equal(true, detail.Contains("NOT native sheet/row identity"));
        Equal(true, detail.Contains("runtime verification still required"));
        Equal(true, detail.Contains("purchase-type=unverified"));
        Equal(true, detail.Contains("no bid writes"));
        // A plausible different time is preserved, not silently replaced with the schedule.
        var values = Parameters;
        values[4] = values[4] with { Integer = values[4].Integer + 60 };
        Equal(true, HousingNativeEvidence.Describe(Observation with { Parameters = values }).Contains("matches-global-entry-close=False"));
    }

    private static void Arguments()
    {
        Rejected(Observation with { Parameters = [] });
        Rejected(Observation with { Parameters = Parameters[..4] });
        Rejected(Observation with { Parameters = Parameters.Append(new(5, 0, 99, true)).ToArray() });
        foreach (var replacement in new[]
        {
            new HousingNativeParameter(3, 1, null, true), new HousingNativeParameter(3, 2, null, true),
            new HousingNativeParameter(3, -1, null, false), new HousingNativeParameter(3, 0, 7, false),
            new HousingNativeParameter(3, 0, null, true), new HousingNativeParameter(4, 0, 7, true),
        })
        {
            var values = Parameters;
            values[3] = replacement;
            Rejected(Observation with { Parameters = values });
        }
        Rejected(Observation with { ReadFailure = "failed read" });
    }

    private static void InvalidCandidates()
    {
        foreach (var (index, invalid) in new[] { (0, 0), (0, 340), (1, 0), (1, 31), (2, 0), (2, 61), (3, 0), (3, -1), (4, int.MinValue), (4, int.MaxValue) })
        {
            var values = Parameters;
            values[index] = values[index] with { Integer = invalid };
            Rejected(Observation with { Parameters = values });
        }
        Rejected(Observation with { EventId1 = 0, EventId2 = 0 });
        Rejected(Observation with { Scene = 0 });
        Rejected(Observation with { Context = Context with { CharacterIdentity = 0 } });
        Rejected(Observation with { Context = Context with { Public = Context.Public with { CurrentWorld = 0 } } });
        Rejected(Observation with { Context = Context with { Public = Context.Public with { CharacterAvailable = false } } });
    }

    private static void Capture(HousingNativeEvidence state, HousingNativeContext? context = null)
        => state.Capture(context ?? Context, Now, HousingNativeEvidence.HousingEventId, 0, 1, Parameters);

    private static void ContextChanges()
    {
        var state = new HousingNativeEvidence();
        foreach (var changed in new[]
        {
            Context with { CharacterIdentity = 123 },
            Context with { Public = Context.Public with { CurrentWorld = 73 } },
            Context with { Public = Context.Public with { Territory = 340 } },
            Context with { Public = Context.Public with { CharacterAvailable = false } },
        })
        {
            state.Arm(Context);
            Capture(state);
            state.UpdateContext(changed);
            // Even switching away and back must invalidate pending observations.
            Equal(0, state.Drain(Context).Count);
            Equal(1, state.Invalidated);
        }
        state.Arm(Context);
        Capture(state);
        state.Stop();
        Capture(state);
        Equal(0, state.Drain(Context).Count);
        state.Arm(Context);
        Equal(0, state.Drain(Context).Count);
        Capture(state);
        Equal(1, state.Drain(Context).Count);
    }

    private static void Bounds()
    {
        var state = new HousingNativeEvidence();
        state.Arm(Context);
        for (var index = 0; index < 100; index++) Capture(state);
        Equal(1, state.Drain(Context).Count);
        Equal(HousingNativeEvidence.Capacity - 1, state.Duplicates);
        Equal(100 - HousingNativeEvidence.Capacity, state.Dropped);
        state.Arm(Context);
        for (var index = 0; index < HousingNativeEvidence.MaximumObservations; index++)
        {
            Capture(state);
            state.Drain(Context);
        }
        Capture(state);
        Equal(1, state.Dropped);
    }

    private static void Privacy()
    {
        var text = HousingNativeEvidence.Describe(Observation with { ReadFailure = "Secret Player 998877665544332211 pointer=0xDEADBEEF" });
        Equal(false, text.Contains("998877665544332211"));
        Equal(false, text.Contains("Secret Player"));
        Equal(false, text.Contains("DEADBEEF"));
        var values = Parameters;
        values[0] = values[0] with { Type = 2, Integer = 123456789 };
        Equal(false, HousingNativeEvidence.Describe(Observation with { Parameters = values }).Contains("123456789"));
        var buffer = new HousingNativeEvidence();
        buffer.Arm(Context);
        Capture(buffer);
        Equal(false, buffer.Drain(Context).Single().Contains("998877665544332211"));
    }
}
