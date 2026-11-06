using System.Security.Cryptography;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.Text;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Text.ReadOnly;

namespace PlotWatch.Data;

/// <summary>Passively reads the bounded result-dialog text. Never activates buttons or infers claims/refunds.</summary>
public sealed unsafe class BidResultCapture : IDisposable
{
    private static readonly uint[] ResultRows = [7122, 7124, 7128];
    private static readonly int[] DistrictIds = [339, 340, 341, 641, 979];
    private readonly IFramework framework;
    private readonly IPlayerState player;
    private readonly IDataManager data;
    private readonly IAddonLifecycle addons;
    private readonly BidTracker tracker;
    private readonly Func<bool> enabled;
    private IReadOnlyDictionary<uint, string> templates = new Dictionary<uint, string>();
    private string addressTemplate = "";
    private string addressTemplateHash = "";
    private Dictionary<int, string> districtNames = [];
    private string language = "";
    private bool disposed;

    public BidResultCapture(IFramework framework, IPlayerState player, IDataManager data,
        IAddonLifecycle addons, BidTracker tracker, Func<bool> enabled)
    {
        this.framework = framework;
        this.player = player;
        this.data = data;
        this.addons = addons;
        this.tracker = tracker;
        this.enabled = enabled;
        addons.RegisterListener(AddonEvent.PostSetup, OnAddonEvent);
    }

    private void OnAddonEvent(AddonEvent kind, AddonArgs args)
    {
        if (disposed || !enabled() || kind != AddonEvent.PostSetup || !framework.IsInFrameworkUpdateThread ||
            args.AddonName is not ("SelectYesno" or "SelectYesNoTextScroll")) return;
        try
        {
            EnsureTemplates();
            var addon = args.GetAddonPointer<AtkUnitBase>();
            if (addon == null || addon->AtkValues == null || addon->AtkValuesCount is 0 or > 64) return;
            var matches = new List<(BidResultMatch Match, string Text)>();
            for (var index = 0; index < (int)addon->AtkValuesCount; index++)
            {
                var value = addon->AtkValues[index];
                if ((value.Type & AtkValueType.TypeMask) is not (AtkValueType.String or AtkValueType.ConstString) || value.String.Value == null) continue;
                var text = ReadText(value.String.Value);
                if (text is null) continue;
                var parsed = BidResultParser.Match(text, templates, TimeZoneInfo.Local, ReadDateFormat());
                if (parsed is not null) matches.Add((parsed, text));
            }
            // Same dialog text commonly appears both in AtkValues and a text node; identical duplicate
            // representations are one observation, but competing template matches fail closed.
            var distinct = matches.DistinctBy(item => (item.Match, item.Text)).ToArray();
            if (distinct.Length != 1) return;
            if (!player.IsLoaded || player.ContentId == 0 || player.CurrentWorld.RowId == 0 || player.HomeWorld.RowId == 0)
            { tracker.ReportCaptureFailure("Lottery result dialog observed, but current character/world unavailable; no outcome changed."); return; }
            var placard = FindPlacardAddress();
            if (placard is null)
            { tracker.ReportCaptureFailure("Result dialog matched, but no complete visible placard address was available; no bid changed."); return; }
            var currentWorld = player.CurrentWorld.Value;
            var character = new TrackedCharacter(player.ContentId, player.CharacterName,
                (int)player.HomeWorld.RowId, player.HomeWorld.Value.Name.ToString());
            var (match, rendered) = distinct[0];
            var observed = DateTimeOffset.UtcNow;
            var macro = templates[match.TemplateRow];
            var evidence = new BidResultEvidence(observed, language,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(macro))), match.TemplateRow, rendered);
            var addressEvidence = new BidAddressEvidence(observed, language, addressTemplateHash,
                placard.DistrictId, placard.Ward, placard.Plot, placard.Size, placard.RenderedText);
            tracker.ObserveResult(character, (int)currentWorld.RowId, currentWorld.Name.ToString(), match, evidence, addressEvidence);
        }
        catch (Exception)
        {
            tracker.ReportCaptureFailure("Lottery result dialog could not be safely matched; no outcome changed.");
        }
    }

    private void EnsureTemplates()
    {
        if (templates.Count == ResultRows.Length && language == data.Language.ToString() && addressTemplate.Length > 0) return;
        if (data.Language is not (Dalamud.Game.ClientLanguage.Japanese or Dalamud.Game.ClientLanguage.English or
            Dalamud.Game.ClientLanguage.German or Dalamud.Game.ClientLanguage.French)) throw new NotSupportedException();
        var sheet = data.GetExcelSheet<Lumina.Excel.Sheets.Addon>();
        var loaded = ResultRows.ToDictionary(row => row, row => sheet.GetRow(row).Text.ToMacroString());
        var addressMacro = sheet.GetRow(8847).Text.ToMacroString();
        if (loaded.Values.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("Lottery result templates unavailable.");
        var languageCode = data.Language switch
        {
            Dalamud.Game.ClientLanguage.Japanese => Lumina.Data.Language.Japanese,
            Dalamud.Game.ClientLanguage.English => Lumina.Data.Language.English,
            Dalamud.Game.ClientLanguage.German => Lumina.Data.Language.German,
            Dalamud.Game.ClientLanguage.French => Lumina.Data.Language.French,
            _ => throw new NotSupportedException(),
        };
        var territorySheet = new Lumina.Excel.ExcelSheet<Lumina.Excel.RawRow>(data.Excel.GetRawSheet("TerritoryType", languageCode));
        var placeSheet = new Lumina.Excel.ExcelSheet<Lumina.Excel.RawRow>(data.Excel.GetRawSheet("PlaceName", languageCode));
        var resolvedNames = new Dictionary<int, string>();
        foreach (var id in DistrictIds)
        {
            var placeId = territorySheet.GetRow((uint)id).ReadColumn(5) switch
            {
                byte value => value, ushort value => value, uint value => value,
                int value when value > 0 => (uint)value, short value when value > 0 => (uint)value,
                _ => throw new NotSupportedException(),
            };
            resolvedNames[id] = placeSheet.GetRow(placeId).ReadStringColumn(0).ExtractText();
        }
        templates = loaded;
        addressTemplate = addressMacro;
        addressTemplateHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(addressMacro)));
        districtNames = resolvedNames;
        language = data.Language.ToString();
    }

    private BidAddressMatch? FindPlacardAddress()
    {
        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null) return null;
        ref var list = ref manager->AtkUnitManager.AllLoadedUnitsList;
        var results = new List<BidAddressMatch>();
        for (var index = 0; index < Math.Min((int)list.Count, 256); index++)
        {
            var addon = list.Entries[index].Value;
            if (addon == null || !addon->IsVisible || addon->NameString != "HousingSignBoard") continue;
            foreach (var text in ReadAddonTexts(addon))
            {
                var parsed = BidAddressParser.Match(text, addressTemplate, districtNames);
                if (parsed is not null) results.Add(parsed);
            }
        }
        var unique = results.DistinctBy(item => (item.DistrictId, item.Ward, item.Plot, item.Size, item.RenderedText)).ToArray();
        return unique.Length == 1 ? unique[0] : null;
    }

    private static int? ReadDateFormat()
    {
        var module = RaptureTextModule.Instance();
        if (module == null) return null;
        ref var parameters = ref module->TextModule.MacroDecoder.GlobalParameters;
        if (parameters.Count <= 76 || parameters.Count > 256) return null;
        var parameter = parameters[76];
        return parameter.Type == TextParameterType.Integer && parameter.IntValue is >= 0 and <= 3
            ? parameter.IntValue : null;
    }

    private static IReadOnlyList<string> ReadAddonTexts(AtkUnitBase* addon)
    {
        var texts = new List<string>();
        if (addon->AtkValues != null)
            for (var index = 0; index < Math.Min((int)addon->AtkValuesCount, 128); index++)
            {
                var value = addon->AtkValues[index];
                if ((value.Type & AtkValueType.TypeMask) is AtkValueType.String or AtkValueType.ConstString && value.String.Value != null &&
                    ReadText(value.String.Value) is { } text) texts.Add(text);
            }

        var work = new Stack<(nint Address, int Depth)>();
        var seen = new HashSet<nint>();
        var root = &addon->UldManager;
        if (root->NodeList == null) return texts;
        for (var index = 0; index < Math.Min((int)root->NodeListCount, 1024); index++)
            if (root->NodeList[index] != null) work.Push(((nint)root->NodeList[index], 0));
        while (work.TryPop(out var current) && seen.Count < 1024)
        {
            if (current.Address == 0 || current.Depth > 8 || !seen.Add(current.Address)) continue;
            var node = (AtkResNode*)current.Address;
            if (node->NextSiblingNode != null) work.Push(((nint)node->NextSiblingNode, current.Depth));
            if (!node->IsVisible()) continue;
            if (node->ChildNode != null) work.Push(((nint)node->ChildNode, current.Depth + 1));
            if (node->Type == NodeType.Text)
            {
                var textNode = (AtkTextNode*)node;
                if (textNode->NodeText.BufUsed is > 0 and <= 512 &&
                    new ReadOnlySeString(textNode->NodeText.AsSpan()).ExtractText() is { Length: > 0 } text) texts.Add(text);
            }
            else if ((int)node->Type >= 1000)
            {
                var component = ((AtkComponentNode*)node)->Component;
                var child = component == null ? null : &component->UldManager;
                if (child != null && child->NodeList != null)
                    for (var index = 0; index < Math.Min((int)child->NodeListCount, 1024); index++)
                        if (child->NodeList[index] != null) work.Push(((nint)child->NodeList[index], current.Depth + 1));
            }
        }
        return texts;
    }

    private static string? ReadText(byte* pointer)
    {
        var length = 0;
        while (length < 4096 && pointer[length] != 0) length++;
        if (length == 4096) return null;
        return new ReadOnlySeString(new ReadOnlySpan<byte>(pointer, length)).ExtractText();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        addons.UnregisterListener(OnAddonEvent);
    }
}
