using PlotWatch.Lottery;

namespace PlotWatch.Diagnostics;

public static class HousingNativeCompatibility
{
    public const string GameVersion = "2026.10.16.0000.0000";
    public const string ExecutableSha256 = "46A52AB1DBE80B9A8D6C4D4531BF66693904200C5135FACACD053EF28A5DE14A";
    public static bool Allows(string version, string hash, bool layoutsMatch)
        => layoutsMatch && version == GameVersion && string.Equals(hash, ExecutableSha256, StringComparison.OrdinalIgnoreCase);

    public static bool MatchesSuccessTemplate(ReadOnlySpan<byte> input, ReadOnlySpan<byte> template)
        => template.Length is >= 16 and <= 1024 && input.SequenceEqual(template);
}

// Identity is a private invalidation token. Never serialize this record or call ToString in reports.
public sealed record HousingNativeContext(ulong CharacterIdentity, HousingProbeContext Public);
public sealed record HousingNativeParameter(int Index, int Type, int? Integer, bool SelfContained);
public sealed record HousingNativeObservation(long Probe, long Epoch, DateTimeOffset AtUtc,
    HousingNativeContext Context, uint EventId1, uint EventId2, short Scene,
    HousingNativeParameter[] Parameters, string? ReadFailure = null);

/// <summary>Managed-only bounded buffer/validation. No hooks, game calls, or bid storage.</summary>
public sealed class HousingNativeEvidence
{
    public const int Capacity = 32;
    public const int MaximumObservations = 64;
    public const uint HousingEventId = 721031; // CustomTalk: CmnDefHousingSignboard_00135
    private readonly Queue<HousingNativeObservation> pending = new();
    private readonly HashSet<string> seen = [];
    private HousingNativeContext? context;
    private long epoch;
    private long probe;
    private int captured;
    public int Dropped { get; private set; }
    public int Invalidated { get; private set; }
    public int Duplicates { get; private set; }
    public bool Active { get; private set; }

    public void Arm(HousingNativeContext next)
    {
        probe++;
        epoch++;
        context = next;
        pending.Clear();
        seen.Clear();
        captured = Dropped = Invalidated = Duplicates = 0;
        Active = true;
    }

    public void UpdateContext(HousingNativeContext next)
    {
        if (context == next) return;
        epoch++;
        context = next;
        Invalidated += pending.Count;
        pending.Clear();
        seen.Clear();
    }

    public void Capture(HousingNativeContext atObservation, DateTimeOffset now, uint event1, uint event2, short scene,
        HousingNativeParameter[] parameters, string? failure = null)
    {
        if (!Active) return;
        UpdateContext(atObservation);
        if (captured++ >= MaximumObservations || pending.Count >= Capacity) { Dropped++; return; }
        pending.Enqueue(new(probe, epoch, now, atObservation, event1, event2, scene,
            parameters.Take(16).ToArray(), failure));
    }

    public IReadOnlyList<string> Drain(HousingNativeContext current)
    {
        UpdateContext(current);
        var result = new List<string>();
        while (pending.TryDequeue(out var observation))
        {
            if (!Active || observation.Probe != probe || observation.Epoch != epoch || observation.Context != current)
            { Invalidated++; continue; }
            var detail = Describe(observation);
            // Observation time is not part of the duplicate key; no identity token leaves this class.
            if (!seen.Add(detail)) { Duplicates++; continue; }
            result.Add($"observed-UTC={observation.AtUtc:O}; probe={probe}; epoch={epoch}; {detail}");
        }
        return result;
    }

    public void Stop()
    {
        Active = false;
        Invalidated += pending.Count;
        pending.Clear();
        seen.Clear();
        context = null;
    }

    public static string Describe(HousingNativeObservation observation)
    {
        var context = observation.Context.Public;
        var parameters = observation.Parameters;
        var raw = string.Join(",", parameters.Select(parameter => $"index{parameter.Index}:type={parameter.Type}:" +
            (parameter.Type == 0 && parameter.SelfContained && parameter.Integer is { } integer
                ? $"int32={integer}" : "value=withheld")));
        var prefix = "boundary=EventFramework.QueueFormatStringCallback; identity-evidence=exact-localized-template-bytes; " +
            "template-reference=custom/001/CmnDefHousingSignboard_00135/6; NOT native sheet/row identity; " +
            $"world={context.CurrentWorld}; home-world={context.HomeWorld}; territory={context.Territory}; language={context.Language}; " +
            $"event-IDs=[{observation.EventId1},{observation.EventId2}]; scene={observation.Scene}; parameters=[{raw}]; ";
        string? failure = observation.ReadFailure is null ? null : "native parameter read unavailable or bounds rejected";
        if (failure is null && (!context.CharacterAvailable || observation.Context.CharacterIdentity == 0 || context.CurrentWorld <= 0))
            failure = "character/world context unavailable";
        if (failure is null && ((observation.EventId1 != HousingEventId && observation.EventId2 != HousingEventId) || observation.Scene != 1))
            failure = "expected housing success-scene context not observed";
        if (failure is null && (parameters.Length != 5 || parameters.Where((parameter, index) =>
                parameter.Index != index || parameter.Type != 0 || !parameter.SelfContained || parameter.Integer is null).Any()))
            failure = "expected exactly five self-contained integer arguments at indices 0–4";
        if (failure is not null) return prefix + $"interpretation=withheld; reason={failure}; no bid writes";

        var values = parameters.Select(parameter => parameter.Integer!.Value).ToArray();
        if (values[0] is not (339 or 340 or 341 or 641 or 979) || (uint)values[0] != context.Territory ||
            values[1] is < 1 or > 30 || values[2] is < 1 or > 60 || values[3] <= 0)
            return prefix + "interpretation=withheld; reason=invalid/mismatched territory, ward, plot, or ticket; no bid writes";
        DateTimeOffset results;
        try { results = DateTimeOffset.FromUnixTimeSeconds(values[4]); }
        catch (ArgumentOutOfRangeException) { return prefix + "interpretation=withheld; reason=invalid Unix timestamp; no bid writes"; }
        if (results <= observation.AtUtc || results - observation.AtUtc > TimeSpan.FromDays(9))
            return prefix + "interpretation=withheld; reason=next-phase time outside plausible future cycle; no bid writes";
        var schedule = LotterySchedule.GetSnapshot(observation.AtUtc);
        return prefix + $"candidate-fields: district={values[0]}; ward={values[1]}; plot={values[2]}; ticket={values[3]}; results-UTC={results:O}; " +
            $"global-calculated-entry-close-UTC={schedule.Application.EndsAtUtc:O}; matches-global-entry-close={results == schedule.Application.EndsAtUtc}; " +
            "mapping=script/static-analysis candidate, runtime verification still required; purchase-type=unverified; no bid writes";
    }
}
