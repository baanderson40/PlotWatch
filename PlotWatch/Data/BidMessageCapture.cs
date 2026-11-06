using System.Security.Cryptography;
using System.Text;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.Text;
using PlotWatch.Paissa;

namespace PlotWatch.Data;

/// <summary>Passive system-message adapter. Independent of diagnostic arming; never invokes game actions.</summary>
public sealed class BidMessageCapture : IDisposable
{
    private readonly IFramework framework;
    private readonly IPlayerState player;
    private readonly IDataManager data;
    private readonly IChatGui chat;
    private readonly BidTracker tracker;
    private readonly Func<bool> enabled;
    private readonly Func<int, PaissaHousingWorldData?> cache;
    private string? template;
    private string templateHash = "";
    private string language = "";
    private Dictionary<int, string> districtNames = [];
    private bool warned;

    public BidMessageCapture(IFramework framework, IPlayerState player, IDataManager data, IChatGui chat,
        BidTracker tracker, Func<bool> enabled, Func<int, PaissaHousingWorldData?> cache)
    {
        this.framework = framework;
        this.player = player;
        this.data = data;
        this.chat = chat;
        this.tracker = tracker;
        this.enabled = enabled;
        this.cache = cache;
        chat.ChatMessage += OnChatMessage;
    }

    public void Dispose() => chat.ChatMessage -= OnChatMessage;

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (!enabled() || message.LogKind != XivChatType.SystemMessage) return;
        if (!framework.IsInFrameworkUpdateThread)
        {
            WarnOnce("Auto-record skipped off-thread system message; character/world could not be sampled safely. Review bids manually.");
            return;
        }
        try
        {
            EnsureTemplate();
            var text = message.OriginalMessage.ExtractText();
            if (text.Length is 0 or > 4096) return;
            // Only a localized template's own initial literal is a candidate; never numeric-token heuristics.
            var firstMacro = template!.IndexOf('<');
            var prefix = firstMacro < 0 ? template : template[..firstMacro];
            if (prefix.Length > 0 && !text.StartsWith(prefix, StringComparison.Ordinal)) return;
            var observed = DateTimeOffset.UtcNow;
            if (!player.IsLoaded || player.ContentId == 0 || player.CurrentWorld.RowId == 0 || player.HomeWorld.RowId == 0)
            { tracker.ReportCaptureFailure("Auto-record candidate rejected: current character/world unavailable. Add bid manually."); return; }
            // Copy identity and CURRENT world at receipt. No home-world substitution, even on retry.
            var character = new TrackedCharacter(player.ContentId, player.CharacterName,
                (int)player.HomeWorld.RowId, player.HomeWorld.Value.Name.ToString());
            var worldId = (int)player.CurrentWorld.RowId;
            var worldName = player.CurrentWorld.Value.Name.ToString();
            var dateFormat = ReadDateFormat();
            if (dateFormat is null)
            { tracker.ReportCaptureFailure("Auto-record candidate rejected: game date-format parameter unavailable. Add bid manually."); return; }
            var zone = TimeZoneInfo.Local;
            var parsed = BidMessageParser.Match(template, text, districtNames, observed, zone, dateFormat.Value);
            if (parsed is null)
            { tracker.ReportCaptureFailure("Auto-record candidate rejected: complete localized message/date/cycle could not be verified. Add bid manually."); return; }
            var bid = new BidRecord
            {
                Id = Guid.NewGuid(), CharacterContentId = character.ContentId, WorldId = worldId, WorldName = worldName,
                DistrictId = parsed.DistrictId, Ward = parsed.Ward, Plot = parsed.Plot, TicketNumber = parsed.Ticket,
                CycleId = parsed.CycleId, ResultsAt = new(parsed.ResultsAtUtc, BidFieldSource.GameObserved),
                DetailsSource = BidFieldSource.GameObserved, TicketSource = BidFieldSource.GameObserved,
                // Receipt is NOT the exact server entry time; outcomes and type are not inferred.
                MessageEvidence = new(observed, language, zone.Id, dateFormat.Value, templateHash, text),
            };
            try
            {
                bid = BidCaptureEnrichment.Apply(bid, cache(worldId));
            }
            catch (Exception) { /* Optional enrichment must never prevent recording the verified entry. */ }
            tracker.SubmitCapture(character, bid);
        }
        catch (Exception)
        {
            WarnOnce("Auto-record unavailable: localized template or game context could not be read safely. Review bids manually; no unrelated chat retained.");
        }
    }

    private void WarnOnce(string warning)
    {
        if (warned) return;
        warned = true;
        tracker.ReportCaptureFailure(warning);
    }

    private void EnsureTemplate()
    {
        if (template is not null && language == data.Language.ToString()) return;
        var clientLanguage = data.Language switch
        {
            Dalamud.Game.ClientLanguage.English => Lumina.Data.Language.English,
            Dalamud.Game.ClientLanguage.Japanese => Lumina.Data.Language.Japanese,
            Dalamud.Game.ClientLanguage.German => Lumina.Data.Language.German,
            Dalamud.Game.ClientLanguage.French => Lumina.Data.Language.French,
            _ => throw new NotSupportedException(),
        };
        var sheet = new Lumina.Excel.ExcelSheet<Lumina.Excel.RawRow>(data.Excel.GetRawSheet(
            "custom/001/CmnDefHousingSignboard_00135", clientLanguage));
        var macro = sheet.GetRow(6).ReadStringColumn(1).ToMacroString();
        var territories = new Lumina.Excel.ExcelSheet<Lumina.Excel.RawRow>(data.Excel.GetRawSheet("TerritoryType", clientLanguage));
        var places = new Lumina.Excel.ExcelSheet<Lumina.Excel.RawRow>(data.Excel.GetRawSheet("PlaceName", clientLanguage));
        var names = new Dictionary<int, string>();
        foreach (var id in new[] { 339, 340, 341, 641, 979 })
        {
            // ReadColumn honors the sheet's actual integer width; row references need not be UInt32.
            var placeId = territories.GetRow((uint)id).ReadColumn(5) switch
            {
                byte value => value, ushort value => value, uint value => value,
                int value when value > 0 => (uint)value, short value when value > 0 => (uint)value,
                _ => throw new NotSupportedException(),
            };
            names[id] = places.GetRow(placeId).ReadStringColumn(0).ExtractText();
        }
        templateHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(macro)));
        districtNames = names;
        language = data.Language.ToString();
        template = macro;
        warned = false;
    }

    private static unsafe int? ReadDateFormat()
    {
        var module = RaptureTextModule.Instance();
        if (module == null) return null;
        ref var parameters = ref module->TextModule.MacroDecoder.GlobalParameters;
        // Macro references are one-based: gnum77 is collection index 76.
        if (parameters.Count <= 76 || parameters.Count > 256) return null;
        var parameter = parameters[76];
        return parameter.Type == TextParameterType.Integer && parameter.IntValue is >= 0 and <= 3
            ? parameter.IntValue : null;
    }
}
