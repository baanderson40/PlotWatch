using PlotWatch.Paissa;

internal static class PaissaDeltaTests
{
    private const string Open = """
        {"type":"plot_open","data":{"world_id":54,"district_id":341,"ward_number":15,"plot_number":53,"size":0,"price":3187500,"last_updated_time":1794945600,"first_seen_time":1794940000,"est_time_open_min":1794945000,"est_time_open_max":1794946000,"purchase_system":5,"lotto_entries":2,"lotto_phase":1,"lotto_phase_until":1794950000}}
        """;
    private static readonly PaissaWorld World = new(54, "Faerie", 4, "Aether");
    private static readonly DateTimeOffset Received = DateTimeOffset.Parse("2026-11-17T20:00:00Z");

    public static readonly (string Name, Action Test)[] Cases =
    [
        ("Paissa websocket parser accepts plot-open, update, sold and ping events", ParseKinds),
        ("Paissa websocket parser rejects invalid identities and malformed payloads", RejectInvalid),
        ("Paissa websocket deltas normalize addresses and upsert listings", Upsert),
        ("Paissa websocket updates preserve omitted fields and clear explicit nulls", PreserveAndClear),
        ("Paissa websocket sold events remove exact plots", Sold),
        ("Paissa websocket ignores stale deltas", IgnoreStale),
    ];

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}.");
    }

    private static string Update(string fields) => $"{{\"type\":\"plot_update\",\"data\":{{\"world_id\":54,\"district_id\":341,\"ward_number\":15,\"plot_number\":53,{fields}}}}}";
    private static PaissaHousingWorldData Empty() => new(World, Received.AddMinutes(-5), Array.Empty<PaissaPlotListing>(), Array.Empty<string>());
    private static PaissaHousingWorldData Apply(PaissaHousingWorldData? current, string json)
    {
        if (!PaissaDeltaParser.TryParse(json, out var delta, out var error) || delta is null)
            throw new Exception(error ?? "No delta parsed.");
        if (!PaissaDeltaParser.TryApply(current, World, delta, Received, out var updated))
            throw new Exception("Delta was not applied.");
        return updated;
    }

    private static void ParseKinds()
    {
        Equal(true, PaissaDeltaParser.TryParse(Open, out var open, out _)); Equal(PaissaDeltaKind.PlotOpen, open!.Kind);
        var update = Update("\"size\":0,\"price\":3187500,\"last_updated_time\":1794945600,\"first_seen_time\":1794940000,\"purchase_system\":5");
        Equal(true, PaissaDeltaParser.TryParse(update, out var changed, out _)); Equal(PaissaDeltaKind.PlotUpdate, changed!.Kind);
        var sold = Update("\"type\":\"plot_sold\"");
        sold = "{\"type\":\"plot_sold\",\"data\":{\"world_id\":54,\"district_id\":341,\"ward_number\":15,\"plot_number\":53,\"last_updated_time\":1794945600}}";
        Equal(true, PaissaDeltaParser.TryParse(sold, out var removed, out _)); Equal(PaissaDeltaKind.PlotSold, removed!.Kind);
        Equal(true, PaissaDeltaParser.TryParse("{\"type\":\"ping\"}", out var ping, out _)); Equal<PaissaDelta?>(null, ping);
        Equal(true, PaissaDeltaParser.TryParse("{\"type\":\"future_event\",\"data\":{}}", out var future, out _)); Equal<PaissaDelta?>(null, future);
    }

    private static void RejectInvalid()
    {
        foreach (var json in new[]
        {
            "{", Open.Replace("\"world_id\":54", "\"world_id\":0"),
            Open.Replace("\"ward_number\":15", "\"ward_number\":30"),
            Open.Replace("\"plot_number\":53", "\"plot_number\":60"),
            Open.Replace("\"district_id\":341", "\"district_id\":777"),
            Open.Replace("\"last_updated_time\":1794945600", "\"last_updated_time\":\"soon\""),
            new string(' ', 65537),
        })
            Equal(false, PaissaDeltaParser.TryParse(json, out _, out _));
    }

    private static void Upsert()
    {
        var snapshot = Apply(null, Open);
        Equal(1, snapshot.Listings.Count);
        var listing = snapshot.Listings.Single();
        Equal(16, listing.WardNumber); Equal(54, listing.PlotNumber); Equal(15, listing.ApiWardNumber); Equal(53, listing.ApiPlotNumber);
        Equal("The Goblet", listing.DistrictName); Equal(3187500L, listing.Price); Equal(Received, snapshot.DownloadedAtUtc);
        var second = Apply(snapshot, Open.Replace("\"price\":3187500", "\"price\":3200000"));
        Equal(1, second.Listings.Count); Equal(3200000L, second.Listings.Single().Price);
    }

    private static void PreserveAndClear()
    {
        var initial = Apply(null, Open);
        var omitted = Apply(initial, Update("\"size\":0,\"price\":3200000,\"last_updated_time\":1794945700,\"first_seen_time\":1794940000,\"purchase_system\":5"));
        Equal(2L, omitted.Listings.Single().LottoEntries); Equal(1, omitted.Listings.Single().LottoPhase);
        Equal(1794950000L, omitted.Listings.Single().LottoPhaseUntil);
        var cleared = Apply(omitted, Update("\"size\":0,\"price\":3200000,\"last_updated_time\":1794945800,\"first_seen_time\":1794940000,\"purchase_system\":5,\"lotto_entries\":null,\"lotto_phase\":null,\"lotto_phase_until\":null"));
        Equal<long?>(null, cleared.Listings.Single().LottoEntries); Equal<int?>(null, cleared.Listings.Single().LottoPhase);
        Equal<long?>(null, cleared.Listings.Single().LottoPhaseUntil);
        Equal(1794945000d, cleared.Listings.Single().EstimatedOpenMin);
    }

    private static void Sold()
    {
        var initial = Apply(null, Open);
        var other = Open.Replace("\"plot_number\":53", "\"plot_number\":54");
        initial = Apply(initial, other);
        var sold = "{\"type\":\"plot_sold\",\"data\":{\"world_id\":54,\"district_id\":341,\"ward_number\":15,\"plot_number\":53,\"last_updated_time\":1794945700}}";
        var after = Apply(initial, sold);
        Equal(1, after.Listings.Count); Equal(54, after.Listings.Single().ApiPlotNumber);
    }

    private static void IgnoreStale()
    {
        var initial = Apply(null, Open);
        var stale = Update("\"size\":0,\"price\":1,\"last_updated_time\":1794940000,\"first_seen_time\":1794940000,\"purchase_system\":5");
        Equal(true, PaissaDeltaParser.TryParse(stale, out var delta, out _));
        Equal(false, PaissaDeltaParser.TryApply(initial, World, delta!, Received.AddSeconds(1), out _));
        var oldSale = "{\"type\":\"plot_sold\",\"data\":{\"world_id\":54,\"district_id\":341,\"ward_number\":15,\"plot_number\":53,\"last_updated_time\":1794940000}}";
        Equal(true, PaissaDeltaParser.TryParse(oldSale, out delta, out _));
        Equal(false, PaissaDeltaParser.TryApply(initial, World, delta!, Received.AddSeconds(1), out _));
    }
}
