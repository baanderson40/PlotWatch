using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Text.ReadOnly;

namespace PlotWatch.Diagnostics;

/// <summary>Opt-in observation with a separately enabled, version-gated native probe. No game actions or bid storage.</summary>
public sealed unsafe class HousingCaptureDiagnostics : IDisposable
{
    public const string CustomSheet = "custom/001/CmnDefHousingSignboard_00135";
    private static readonly uint[] AddonRows = [7039, 7040, 7041, 7043, 7044, 7046, 7122, 7123, 7124, 7126, 7127, 7128, 7129, 8841, 8842, 8843, 8844, 8845, 8846, 8847, 8848, 8849];
    private readonly HousingProbeState state;
    private readonly IFramework framework;
    private readonly IPlayerState player;
    private readonly IDataManager data;
    private readonly IChatGui chat;
    private readonly IAddonLifecycle addons;
    private readonly HousingNativeProbe nativeProbe;
    private byte[] successTemplateBytes = [];
    private bool nativeRunning;
    private readonly Dictionary<string, string> snapshots = new();
    private HousingProbeTemplate[] templates = [];
    private HousingProbeContext? lastContext;
    private long nextScanTick;
    private bool scanWarningReported;
    private readonly HashSet<string> eventWarnings = new();
    private bool disposed;

    public HousingCaptureDiagnostics(HousingProbeState state, IFramework framework, IPlayerState player,
        IDataManager data, IChatGui chat, IAddonLifecycle addons, IGameInteropProvider interop, ISigScanner scanner)
    {
        this.state = state;
        this.framework = framework;
        this.player = player;
        this.data = data;
        this.chat = chat;
        this.addons = addons;
        nativeProbe = new HousingNativeProbe(interop, scanner, framework, SampleNativeContext);
        chat.LogMessage += OnLogMessage;
        chat.ChatMessage += OnChatMessage;
        addons.RegisterListener(AddonEvent.PostSetup, OnAddonEvent);
        addons.RegisterListener(AddonEvent.PreFinalize, OnAddonEvent);
    }

    public void Arm(HousingProbeOptions options)
    {
        RequireFrameworkThread();
        StopNative();
        UpdateContext();
        snapshots.Clear();
        scanWarningReported = false;
        eventWarnings.Clear();
        nextScanTick = 0;
        var armedTick = Environment.TickCount64;
        state.Arm(options, armedTick, DateTimeOffset.UtcNow);
        if (templates.Length == 0 || options.NativeSuccess) InspectTemplates();
        if (options.NativeSuccess)
        {
            var nativeStatus = nativeProbe.Arm(successTemplateBytes, armedTick);
            nativeRunning = nativeStatus.StartsWith("Armed:", StringComparison.Ordinal);
            state.SetNativeProbeStatus(nativeStatus, DateTimeOffset.UtcNow);
        }
        else state.SetNativeProbeStatus("Off; native success probe not requested. No native hook enabled.", DateTimeOffset.UtcNow);
    }

    public void Stop()
    {
        StopNative();
        state.Stop(DateTimeOffset.UtcNow);
    }

    private void StopNative()
    {
        if (!nativeRunning) return;
        nativeRunning = false;
        state.SetNativeProbeStatus(nativeProbe.Stop(), DateTimeOffset.UtcNow);
    }

    public void Clear()
    {
        StopNative();
        state.Clear(DateTimeOffset.UtcNow);
        snapshots.Clear();
    }

    public void Tick()
    {
        RequireFrameworkThread();
        UpdateContext();
        var tick = Environment.TickCount64;
        if (!state.IsActive(tick, DateTimeOffset.UtcNow)) { StopNative(); return; }
        if (nativeRunning)
            foreach (var observation in nativeProbe.Drain(SampleNativeContext())) Record("NativeSuccess", observation, HousingProbeLogLevel.Debug);
        if (tick < nextScanTick) return;
        nextScanTick = tick + 500;
        try
        {
            var manager = RaptureAtkUnitManager.Instance();
            if (manager == null) return;
            ref var list = ref manager->AtkUnitManager.AllLoadedUnitsList;
            var visible = new HashSet<string>();
            for (var index = 0; index < Math.Min((int)list.Count, 256); index++)
            {
                var addon = list.Entries[index].Value;
                if (addon == null || !addon->IsVisible) continue;
                var key = addon->NameString + ":" + addon->Id;
                visible.Add(key);
                InspectAddon(addon, "Snapshot");
            }
            foreach (var key in snapshots.Keys.Where(key => !visible.Contains(key)).ToArray())
            {
                Record("Addon", $"Closed/hidden: {key}; prior snapshot invalidated.");
                snapshots.Remove(key);
            }
        }
        catch (Exception)
        {
            if (!scanWarningReported)
            {
                scanWarningReported = true;
                Record("Addon", "Read-only UI scan unavailable; no native pointer or exception payload retained.", HousingProbeLogLevel.Warning);
            }
        }
    }

    private void UpdateContext()
    {
        var sampled = SampleNativeContext();
        var context = sampled.Public;
        if (lastContext != context) snapshots.Clear();
        lastContext = context;
        nativeProbe.UpdateContext(sampled);
        state.UpdateContext(context, sampled.CharacterIdentity, Environment.TickCount64, DateTimeOffset.UtcNow);
    }

    private HousingNativeContext SampleNativeContext()
    {
        HousingProbeContext context;
        ulong identity = 0;
        try
        {
            var available = player.IsLoaded && player.ContentId != 0;
            identity = available ? player.ContentId : 0;
            context = new(available, available ? (int)player.HomeWorld.RowId : 0,
                available ? (int)player.CurrentWorld.RowId : 0, CurrentTerritory(), data.Language.ToString());
        }
        catch (Exception) { context = new(false, 0, 0, 0, data.Language.ToString()); }
        return new(identity, context);
    }

    // Territory is supplied by the framework's player-context sampler, not read during rendering.
    public Func<uint> CurrentTerritory { private get; init; } = () => 0;

    public void InspectTemplates()
    {
        RequireFrameworkThread();
        successTemplateBytes = [];
        var found = new List<HousingProbeTemplate>();
        ReadTemplates(CustomSheet, Enumerable.Range(0, 11).Select(value => (uint)value), 1, found);
        ReadTemplates("Addon", AddonRows, 0, found);
        ReadTemplates("LogMessage", new uint[] { 3497, 6099 }, 5, found);
        templates = found.ToArray();
        state.SetTemplates(templates, DateTimeOffset.UtcNow);
        var entry = templates.FirstOrDefault(template => template.Sheet == CustomSheet && template.Row == 6);
        if (entry is null || !new[] { 1, 2, 3, 4, 5 }.All(number => entry.LocalNumbers.Contains(number)))
        {
            successTemplateBytes = [];
            state.TemplateFailure("Successful-entry template is missing expected lnum1–lnum5 references; re-inspect installed data", DateTimeOffset.UtcNow);
        }
    }

    private void ReadTemplates(string sheetName, IEnumerable<uint> rows, int column, List<HousingProbeTemplate> result)
    {
        try
        {
            var language = data.Language switch
            {
                Dalamud.Game.ClientLanguage.Japanese => Lumina.Data.Language.Japanese,
                Dalamud.Game.ClientLanguage.English => Lumina.Data.Language.English,
                Dalamud.Game.ClientLanguage.German => Lumina.Data.Language.German,
                Dalamud.Game.ClientLanguage.French => Lumina.Data.Language.French,
                _ => throw new NotSupportedException(),
            };
            var sheet = new Lumina.Excel.ExcelSheet<Lumina.Excel.RawRow>(data.Excel.GetRawSheet(sheetName, language));
            foreach (var id in rows)
            {
                try
                {
                    // LogMessage's raw column 0 is numeric; use its typed Text accessor.
                    var value = sheetName == "LogMessage"
                        ? data.GetExcelSheet<Lumina.Excel.Sheets.LogMessage>().GetRow(id).Text
                        : sheet.GetRow(id).ReadStringColumn(column);
                    var structure = new List<string>();
                    foreach (var payload in value)
                        structure.Add(payload.Type == ReadOnlySePayloadType.Macro ? $"Macro:{payload.MacroCode}" : payload.Type.ToString());
                    var macro = value.ToMacroString();
                    if (macro.Length > 16384) { state.TemplateFailure($"{sheetName}/{id}: template exceeds diagnostic bound", DateTimeOffset.UtcNow); continue; }
                    if (sheetName == CustomSheet && id == 6 && value.ByteLength is >= 16 and <= 1024)
                        successTemplateBytes = value.Data.ToArray();
                    var prefixes = new List<string>();
                    var staticMessages = new List<string>();
                    CollectBranchPrefixes(value, prefixes, staticMessages, 0);
                    result.Add(HousingProbeState.DescribeTemplate(sheetName, id, column, sheet.Language.ToString(), macro,
                        $"bytes={value.ByteLength}; payloads=[{string.Join(",", structure.Take(128))}]") with
                    { BranchPrefixes = prefixes.Distinct().ToArray(), StaticMessages = staticMessages.Distinct().ToArray() });
                }
                catch (Exception) { state.TemplateFailure($"{sheetName}/{id} column {column}", DateTimeOffset.UtcNow); }
            }
        }
        catch (Exception) { state.TemplateFailure(sheetName, DateTimeOffset.UtcNow); }
    }

    private bool CanReadEvent() => !disposed && framework.IsInFrameworkUpdateThread &&
        state.IsActive(Environment.TickCount64, DateTimeOffset.UtcNow);

    private static void CollectBranchPrefixes(ReadOnlySeString text, List<string> prefixes, List<string> staticMessages, int depth)
    {
        if (depth > 6 || prefixes.Count >= 64) return;
        if (text.All(payload => payload.Type == ReadOnlySePayloadType.Text))
        {
            var complete = text.ExtractText().Trim();
            if (complete.Length is >= 8 and <= 512 && staticMessages.Count < 64) staticMessages.Add(complete);
        }
        var prefix = text.ToMacroString().Split('<')[0].Trim();
        if (prefix.Length is >= 3 and <= 512) prefixes.Add(prefix);
        foreach (var payload in text)
        {
            if (payload.Type == ReadOnlySePayloadType.Text)
            {
                var literal = Encoding.UTF8.GetString(payload.Body.Span).Trim();
                if (literal.Length is >= 3 and <= 512 && prefixes.Count < 64) prefixes.Add(literal);
                continue;
            }
            if (payload.Type != ReadOnlySePayloadType.Macro) continue;
            foreach (var expression in payload)
                if (expression.TryGetString(out var branch)) CollectBranchPrefixes(branch, prefixes, staticMessages, depth + 1);
        }
    }

    private void OnLogMessage(ILogMessage message)
    {
        if (!CanReadEvent()) return;
        try
        {
            var settings = state.View.Options;
            var numeric = settings.NumericLogMessageId == message.LogMessageId;
            if (!numeric && !settings.AllLogMessageIds && message.LogMessageId is not (3497 or 6099)) return;
            var parameters = new List<string>();
            for (var index = 0; index < Math.Min(message.ParameterCount, 32); index++)
                parameters.Add(numeric && message.TryGetIntParameter(index, out var value)
                    ? $"{index}:int={value.ToString(CultureInfo.InvariantCulture)}" : $"{index}:withheld");
            // No string parameters, entity objects, addresses, or formatting/evaluation calls.
            Record("LogMessage", $"LogMessage-sheet-ID={message.LogMessageId}; param-count={message.ParameterCount}; " +
                $"parameters=[{string.Join(",", parameters)}]; {(numeric ? "selected-ID numeric inspection; meanings unverified" : "metadata only")}; NOT custom-sheet identity.");
        }
        catch (Exception) { Record("LogMessage", "Event could not be inspected; no native data retained.", HousingProbeLogLevel.Warning); }
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (!CanReadEvent()) return;
        // Ignore player chat/echo entirely, including messages quoting a housing confirmation.
        if (message.LogKind is not (XivChatType.SystemMessage or XivChatType.SystemError or XivChatType.Notice or XivChatType.Urgent)) return;
        try
        {
            var candidate = HousingProbeState.DescribeCandidate(message.OriginalMessage.ExtractText(), templates.Where(template => template.Sheet == CustomSheet).ToArray());
            if (candidate is not null) Record("Chat", $"channel={message.LogKind}; {candidate}");
        }
        catch (Exception) { Record("Chat", "Candidate inspection unavailable; raw chat withheld.", HousingProbeLogLevel.Warning); }
    }

    private void OnAddonEvent(AddonEvent kind, AddonArgs args)
    {
        if (!CanReadEvent()) return;
        if (state.View.Options.AddonNames) Record("Lifecycle", $"{kind}; addon={HousingProbeState.Clean(args.AddonName)}; names only, no values from unrelated addons.");
        if (kind == AddonEvent.PreFinalize)
        {
            foreach (var key in snapshots.Keys.Where(key => key.StartsWith(args.AddonName + ":", StringComparison.Ordinal)).ToArray()) snapshots.Remove(key);
            return;
        }
        try { InspectAddon(args.GetAddonPointer<AtkUnitBase>(), "PostSetup"); }
        catch (Exception) { Record("Addon", $"PostSetup inspection unavailable for {HousingProbeState.Clean(args.AddonName)}.", HousingProbeLogLevel.Warning); }
    }

    private void InspectAddon(AtkUnitBase* addon, string origin)
    {
        if (addon == null || !addon->IsVisible) return;
        var name = addon->NameString;
        // Never inspect chat-log nodes: users may quote sheet text there.
        if (name.StartsWith("ChatLog", StringComparison.OrdinalIgnoreCase)) return;
        var candidates = new List<string>();
        var valueStrings = new Dictionary<int, string>();
        var inspectedText = new List<(string Location, string Text)>();
        var relevantTemplates = templates.Where(template => template.Sheet == "Addon" || template.Sheet == CustomSheet).ToArray();
        var inspectedValues = Math.Min((int)addon->AtkValuesCount, HousingProbeFormatting.MaximumAtkValues);
        if (addon->AtkValues != null)
            for (var index = 0; index < inspectedValues; index++)
            {
                var value = addon->AtkValues[index];
                if ((value.Type & AtkValueType.TypeMask) is not (AtkValueType.String or AtkValueType.ConstString) || value.String.Value == null) continue;
                var text = ReadBoundedText(value.String.Value);
                if (text is null) continue;
                inspectedText.Add(($"AtkValue[{index}]", text));
                var candidate = DescribeAddonText(name, text, relevantTemplates);
                if (candidate is not null) { candidates.Add($"AtkValue[{index}]: {candidate}"); valueStrings[index] = candidate; }
            }
        var nodeScan = ReadAddonTextNodes(addon);
        foreach (var (location, text) in nodeScan.Text)
        {
            inspectedText.Add((location, text));
            var candidate = DescribeAddonText(name, text, relevantTemplates);
            if (candidate is not null) candidates.Add($"{location}: {candidate}");
        }
        var knownHousingName = name.Contains("Housing", StringComparison.OrdinalIgnoreCase);
        if (!knownHousingName && candidates.Count == 0) return;
        // Full localized address matching is enabled only after separate housing evidence.
        var addressTemplates = templates.Where(template => template.Sheet == "Addon" && template.Row == 8847).ToArray();
        foreach (var (location, text) in inspectedText)
        {
            var address = name == "HousingSignBoard"
                ? HousingProbeFormatting.DescribePlacardAddress(text, addressTemplates)
                : HousingProbeState.DescribeCandidate(text, addressTemplates, allowShortPrefixes: true);
            if (address is not null) candidates.Add($"{location}: {address}");
        }
        var values = new List<string>();
        if (addon->AtkValues != null)
            for (var index = 0; index < inspectedValues; index++)
            {
                var value = addon->AtkValues[index];
                if ((value.Type & AtkValueType.TypeMask) is AtkValueType.Undefined or AtkValueType.Null) continue;
                var description = (value.Type & AtkValueType.TypeMask) switch
                {
                    AtkValueType.Int => value.Int.ToString(CultureInfo.InvariantCulture),
                    AtkValueType.UInt => value.UInt.ToString(CultureInfo.InvariantCulture),
                    AtkValueType.Float => value.Float.ToString(CultureInfo.InvariantCulture),
                    AtkValueType.Bool => value.Bool.ToString(),
                    _ => valueStrings.ContainsKey(index) ? "template-evidence-below" : "withheld",
                };
                values.Add($"{index}:{value.Type}={description}");
            }
        var key = name + ":" + addon->Id;
        var summary = $"AtkValues-total={addon->AtkValuesCount}; inspected={inspectedValues}; value-limit-hit={addon->AtkValuesCount > inspectedValues}; " +
            $"defined-values={values.Count}; nodes-visited={nodeScan.Visited}; node/depth-limit-hit={nodeScan.Limited}; " +
            "Undefined/Null omitted; numeric meanings unverified; arbitrary strings/64-bit values/pointers withheld.";
        if (name == "HousingSignBoard")
        {
            var schedule = PlotWatch.Lottery.LotterySchedule.GetSnapshot(DateTimeOffset.UtcNow);
            summary += $" Separate global calculation: phase={schedule.CurrentPhase}; phase-close-UTC={schedule.CurrentPhaseEndsAtUtc:O}; not decoded from placard display.";
        }
        var details = new[] { summary }.Concat(values.Select(value => "value[" + value + "]"))
            .Concat(candidates.Distinct().Select(candidate => "evidence[" + candidate + "]")).ToArray();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("; ", details))));
        if (snapshots.TryGetValue(key, out var previous) && previous == fingerprint) return;
        snapshots[key] = fingerprint;
        foreach (var part in HousingProbeFormatting.SnapshotParts($"addon={key}; {origin}", details))
            Record("Addon", part);
    }

    private static string? DescribeAddonText(string name, string text, IReadOnlyList<HousingProbeTemplate> relevantTemplates)
    {
        var candidate = HousingProbeState.DescribeCandidate(text, relevantTemplates);
        if (name != "HousingSignBoard") return candidate;
        var rendered = HousingPlacardEvidence.Describe(text, relevantTemplates);
        return rendered is null ? candidate : candidate is null ? rendered : $"{candidate}; {rendered}";
    }

    private sealed record NodeScan(IReadOnlyList<(string Location, string Text)> Text, int Visited, bool Limited);

    private static NodeScan ReadAddonTextNodes(AtkUnitBase* addon)
    {
        var text = new List<(string Location, string Text)>();
        var discovered = new HashSet<nint>();
        var work = new Stack<(nint Address, int Depth, string Path)>();
        var limited = false;
        var visited = 0;

        void QueueNode(AtkResNode* node, int depth, string path)
        {
            if (node == null || discovered.Contains((nint)node)) return;
            if (depth > HousingProbeFormatting.MaximumDepth || discovered.Count >= HousingProbeFormatting.MaximumNodes)
            { limited = true; return; }
            discovered.Add((nint)node); // Frame-local cycle guard; addresses never exported or retained.
            work.Push(((nint)node, depth, path));
        }

        void QueueManager(AtkUldManager* manager, int depth, string path)
        {
            if (manager == null || manager->NodeList == null) return;
            var count = Math.Min((int)manager->NodeListCount, HousingProbeFormatting.MaximumNodes);
            if (count != manager->NodeListCount) limited = true;
            for (var index = 0; index < count; index++) QueueNode(manager->NodeList[index], depth, $"{path}[{index}]");
        }

        QueueManager(&addon->UldManager, 0, "root");
        while (work.TryPop(out var item))
        {
            var node = (AtkResNode*)item.Address;
            visited++;
            QueueNode(node->NextSiblingNode, item.Depth, item.Path + "/next");
            if (!node->IsVisible()) continue;
            QueueNode(node->ChildNode, item.Depth + 1, item.Path + "/child");
            if (node->Type == NodeType.Text)
            {
                var textNode = (AtkTextNode*)node;
                if (textNode->NodeText.BufUsed <= 4096)
                {
                    var decoded = ExtractBoundedText(textNode->NodeText.AsSpan());
                    if (decoded is not null) text.Add(($"TextNode[path={item.Path},id={node->NodeId}]", decoded));
                }
            }
            else if ((int)node->Type >= 1000)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component != null) QueueManager(&component->UldManager, item.Depth + 1, item.Path + "/component");
            }
        }
        return new(text, visited, limited);
    }

    private static string? ReadBoundedText(byte* pointer)
    {
        var length = 0;
        while (length < 4096 && pointer[length] != 0) length++;
        if (length == 4096) return null;
        return ExtractBoundedText(new ReadOnlySpan<byte>(pointer, length));
    }

    private static string? ExtractBoundedText(ReadOnlySpan<byte> bytes)
        => bytes.Length > 4096 ? null : new ReadOnlySeString(bytes).ExtractText();

    private void Record(string source, string detail, HousingProbeLogLevel level = HousingProbeLogLevel.Verbose)
    {
        if (level == HousingProbeLogLevel.Warning && !eventWarnings.Add(source)) return;
        state.Record(source, detail, Environment.TickCount64, DateTimeOffset.UtcNow, level);
    }

    private void RequireFrameworkThread()
    {
        if (!framework.IsInFrameworkUpdateThread) throw new InvalidOperationException("Housing diagnostics require the framework thread.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        chat.LogMessage -= OnLogMessage;
        chat.ChatMessage -= OnChatMessage;
        addons.UnregisterListener(OnAddonEvent);
        Stop();
        nativeProbe.Dispose();
        snapshots.Clear();
    }
}
