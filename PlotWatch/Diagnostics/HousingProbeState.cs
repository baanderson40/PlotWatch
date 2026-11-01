using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PlotWatch.Diagnostics;

public enum HousingProbeLogLevel { Debug, Verbose, Warning }
public sealed record HousingProbeOptions(bool AllLogMessageIds = false, uint? NumericLogMessageId = null, bool AddonNames = true, bool NativeSuccess = false);
public sealed record HousingProbeContext(bool CharacterAvailable, int HomeWorld, int CurrentWorld, uint Territory, string Language);
public sealed record HousingProbeEntry(DateTimeOffset AtUtc, string Source, string Detail);
public sealed record HousingProbeTemplate(string Sheet, uint Row, int Column, string Language, string Macro, string Structure,
    string Prefix, IReadOnlyList<int> LocalNumbers, IReadOnlyList<string>? BranchPrefixes = null, IReadOnlyList<string>? StaticMessages = null);
public sealed record HousingProbeView(bool Armed, TimeSpan Remaining, string Status, HousingProbeContext Context,
    HousingProbeOptions Options, IReadOnlyList<HousingProbeEntry> Entries, IReadOnlyList<HousingProbeTemplate> Templates, int Dropped)
{
    public string NativeProbeStatus { get; init; } = "Off; no native hook requested.";
}

/// <summary>Bounded diagnostic history only. No dependency on bid storage or game actions.</summary>
public sealed class HousingProbeState(Action<HousingProbeLogLevel, string>? log = null)
{
    public const int Capacity = 200;
    public const int MaximumDetailLength = 4096;
    public const int MaximumEventsPerProbe = 400;
    private readonly object gate = new();
    private readonly Queue<HousingProbeEntry> entries = new();
    private HousingProbeTemplate[] templates = [];
    private HousingProbeContext context = new(false, 0, 0, 0, "Unknown");
    private HousingProbeOptions options = new();
    private ulong characterIdentity;
    private bool armed;
    private long endsAtTick;
    private TimeSpan remaining;
    private string status = "Idle; no capture has run.";
    private int dropped;
    private int recordedEvents;
    private int suppressedEvents;
    private string nativeProbeStatus = "Off; no native hook requested.";

    public HousingProbeView View
    {
        get { lock (gate) return new(armed, remaining, status, context, options, entries.ToArray(), templates.ToArray(), dropped) { NativeProbeStatus = nativeProbeStatus }; }
    }

    public void UpdateContext(HousingProbeContext next, ulong identity, long tick, DateTimeOffset now)
    {
        lock (gate)
        {
            CheckExpiry(tick, now);
            if (armed && (characterIdentity != identity || context.CharacterAvailable != next.CharacterAvailable))
                StopCore("Stopped: character changed or became unavailable.", now);
            else if (armed && context != next)
                AddCore(now, "Context", $"Context changed; prior addon snapshots invalidated. home-world={next.HomeWorld}; current-world={next.CurrentWorld}; territory={next.Territory}.");
            context = next;
            characterIdentity = identity; // Private invalidation token: never exported or logged.
        }
    }

    public void Arm(HousingProbeOptions settings, long tick, DateTimeOffset now)
    {
        lock (gate)
        {
            entries.Clear();
            dropped = 0;
            recordedEvents = suppressedEvents = 0;
            options = settings;
            armed = true;
            endsAtTick = tick + 15_000;
            remaining = TimeSpan.FromSeconds(15);
            status = "Armed for 15 seconds; read-only. No bid writes or game actions.";
            AddCore(now, "Probe", $"Armed. all-log-IDs={settings.AllLogMessageIds}; numeric-log-ID={settings.NumericLogMessageId?.ToString() ?? "none"}; addon-names={settings.AddonNames}; home-world={context.HomeWorld}; current-world={context.CurrentWorld}; territory={context.Territory}; language={context.Language}.", HousingProbeLogLevel.Debug);
        }
    }

    public bool IsActive(long tick, DateTimeOffset now)
    {
        lock (gate) { CheckExpiry(tick, now); return armed; }
    }

    public void Stop(DateTimeOffset now)
    {
        lock (gate) { if (armed) StopCore("Stopped by user.", now); }
    }

    public void Clear(DateTimeOffset now)
    {
        lock (gate)
        {
            armed = false;
            remaining = TimeSpan.Zero;
            entries.Clear();
            dropped = 0;
            recordedEvents = suppressedEvents = 0;
            status = "Idle; history cleared and probe stopped. Templates retained.";
            log?.Invoke(HousingProbeLogLevel.Debug, "Housing capture probe cleared and stopped.");
        }
    }

    public bool Record(string source, string detail, long tick, DateTimeOffset now, HousingProbeLogLevel level = HousingProbeLogLevel.Verbose)
    {
        lock (gate)
        {
            CheckExpiry(tick, now);
            if (!armed) return false;
            AddCore(now, source, detail, level);
            return true;
        }
    }

    public void SetTemplates(IEnumerable<HousingProbeTemplate> values, DateTimeOffset now)
    {
        lock (gate)
        {
            templates = values.Take(40).ToArray();
            AddCore(now, "Templates", $"Inspected {templates.Length} template rows. Templates do not expose live local-number values.", HousingProbeLogLevel.Debug);
        }
    }

    public void TemplateFailure(string sheet, DateTimeOffset now)
    {
        lock (gate) AddCore(now, "Templates", $"Unavailable: {Clean(sheet)}. No language fallback or runtime parameter mapping assumed.", HousingProbeLogLevel.Warning);
    }

    public void SetNativeProbeStatus(string value, DateTimeOffset now)
    {
        lock (gate)
        {
            nativeProbeStatus = Clean(value);
            AddCore(now, "NativeProbe", nativeProbeStatus, HousingProbeLogLevel.Debug);
        }
    }

    private void CheckExpiry(long tick, DateTimeOffset now)
    {
        if (!armed) return;
        if (tick >= endsAtTick) StopCore("Expired after 15 seconds.", now);
        else remaining = TimeSpan.FromMilliseconds(endsAtTick - tick);
    }

    private void StopCore(string reason, DateTimeOffset now)
    {
        armed = false;
        remaining = TimeSpan.Zero;
        status = reason;
        AddCore(now, "Probe", $"{reason} Rate-limit-suppressed={suppressedEvents}.", HousingProbeLogLevel.Debug);
    }

    private void AddCore(DateTimeOffset now, string source, string detail, HousingProbeLogLevel level = HousingProbeLogLevel.Verbose)
    {
        if (level == HousingProbeLogLevel.Verbose && recordedEvents >= MaximumEventsPerProbe)
        {
            suppressedEvents++;
            return;
        }
        recordedEvents++;
        var safe = Clean(detail);
        if (entries.Count == Capacity) { entries.Dequeue(); dropped++; }
        entries.Enqueue(new(now, Clean(source), safe));
        log?.Invoke(level, $"Housing capture probe [{Clean(source)}]: {safe}");
    }

    public static string Clean(string text)
    {
        var safe = new string(text.Take(MaximumDetailLength).Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        return text.Length > MaximumDetailLength ? safe + " [truncated]" : safe;
    }

    public static HousingProbeTemplate DescribeTemplate(string sheet, uint row, int column, string language, string macro, string structure)
    {
        var locals = Regex.Matches(macro, @"\blnum(\d+)\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))
            .Select(match => int.TryParse(match.Groups[1].Value, out var number) ? number : 0)
            .Where(number => number > 0).Distinct().Order().ToArray();
        var prefix = macro.Split('<')[0].Trim();
        return new(sheet, row, column, language, macro, structure, prefix, locals);
    }

    /// <summary>Candidate correlation only; never asserts native message/sheet identity.</summary>
    public static string? DescribeCandidate(string text, IReadOnlyList<HousingProbeTemplate> templates, bool allowShortPrefixes = false)
    {
        var normalized = text.Trim();
        if (normalized.Length is 0 or > MaximumDetailLength) return null;
        var matches = templates.Where(template => template.Sheet == "Addon" && template.Row == 8847
            ? allowShortPrefixes && HousingProbeFormatting.IsCompleteAddress(normalized, template.Macro)
            :
            StaticMessageMatches(normalized, template).Any() ||
            (template.Prefix.Length >= (allowShortPrefixes ? 3 : 8) && normalized.StartsWith(template.Prefix, StringComparison.Ordinal)) ||
            (template.BranchPrefixes?.Any(prefix => prefix.Length >= 8 && normalized.StartsWith(prefix, StringComparison.Ordinal)) ?? false) ||
            (normalized.Length >= 8 && template.Macro.Contains(normalized, StringComparison.Ordinal))).Take(4).ToArray();
        if (matches.Length == 0) return null;
        // Only static sheet text and short numeric tokens leave this method; never player names or raw chat.
        var numbers = Regex.Matches(normalized, @"\d+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))
            .Take(24).Select(match => match.Length <= 9 ? match.Value : "[long-number-redacted]");
        var staticSuffixes = matches.Select(template => !string.IsNullOrEmpty(template.Prefix) && normalized.StartsWith(template.Prefix, StringComparison.Ordinal)
                ? normalized[template.Prefix.Length..].Trim() : normalized)
            .Where(suffix => suffix.Length >= 4 && matches.Any(template => template.Macro.Contains(suffix, StringComparison.Ordinal)))
            .Distinct().Take(4);
        var literalMatches = matches.SelectMany(template => new[] { template.Prefix }.Concat(template.BranchPrefixes ?? []))
            .Where(prefix => prefix.Length >= (allowShortPrefixes ? 3 : 8) && normalized.StartsWith(prefix, StringComparison.Ordinal))
            .Concat(matches.SelectMany(template => StaticMessageMatches(normalized, template)))
            .Distinct().Take(4);
        var fragments = matches.SelectMany(template => template.BranchPrefixes ?? [])
            .Where(fragment => fragment.Length >= 3 && normalized.Contains(fragment, StringComparison.Ordinal))
            .Distinct().Take(8);
        return $"template-candidates=[{string.Join(", ", matches.Select(template => $"{template.Sheet}/{template.Row}"))}]; " +
            $"matched-literals=[{string.Join(" | ", literalMatches)}]; static-evidence=[{string.Join(" | ", staticSuffixes)}]; " +
            $"literal-fragments-present=[{string.Join(" | ", fragments)}]; numeric-text-tokens=[{string.Join(",", numbers)}]; " +
            "candidate only: NOT a native message ID or verified parameter mapping; raw text withheld";
    }

    private static IEnumerable<string> StaticMessageMatches(string text, HousingProbeTemplate template)
    {
        // Only complete, payload-free messages from verified plot-eligibility/status rows
        // can match within a combined node. Do not broaden chat or outcome detection.
        if (template.Sheet != "Addon" || template.Row is not (7040 or 7041 or 7043)) yield break;
        foreach (var message in template.StaticMessages ?? [])
        {
            if (message.Length < 8) continue;
            var offset = 0;
            while ((offset = text.IndexOf(message, offset, StringComparison.Ordinal)) >= 0)
            {
                var end = offset + message.Length;
                if ((offset == 0 || !char.IsLetterOrDigit(text[offset - 1])) &&
                    (end == text.Length || !char.IsLetterOrDigit(text[end])))
                {
                    yield return message;
                    break;
                }
                offset++;
            }
        }
    }

    public string BuildReport(string buildId, bool includeTemplates = true)
    {
        var view = View;
        var result = new StringBuilder();
        result.AppendLine($"PlotWatch Housing Capture diagnostics; build={Clean(buildId)}");
        result.AppendLine("READ-ONLY: no bid records created/changed; no game actions. Template matches are candidates, not native message identity.");
        result.AppendLine($"status={view.Status}; remaining={view.Remaining.TotalSeconds:F1}s; character-available={view.Context.CharacterAvailable}; home-world={view.Context.HomeWorld}; current-world={view.Context.CurrentWorld}; territory={view.Context.Territory}; language={view.Context.Language}");
        result.AppendLine($"all-log-IDs={view.Options.AllLogMessageIds}; numeric-log-ID={view.Options.NumericLogMessageId?.ToString() ?? "none"}; dropped-oldest={view.Dropped}");
        result.AppendLine($"native-success-opt-in={view.Options.NativeSuccess}; native-probe={view.NativeProbeStatus}");
        foreach (var template in includeTemplates ? view.Templates : [])
        {
            result.AppendLine($"TEMPLATE {template.Sheet}/{template.Row} column={template.Column} language={template.Language}; lnums=[{string.Join(",", template.LocalNumbers)}]; {template.Structure}");
            result.AppendLine($"Literal branch prefixes: [{string.Join(" | ", template.BranchPrefixes ?? [])}]");
            result.AppendLine($"Complete static messages: [{string.Join(" | ", template.StaticMessages ?? [])}]");
            result.AppendLine(template.Macro);
        }
        foreach (var entry in view.Entries)
            result.AppendLine($"{entry.AtUtc.ToString("O", CultureInfo.InvariantCulture)} [{entry.Source}] {entry.Detail}");
        return result.ToString();
    }
}
