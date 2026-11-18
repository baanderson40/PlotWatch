using System.Text.Json;

namespace PlotWatch.Paissa;

public enum PaissaDeltaKind { PlotOpen, PlotUpdate, PlotSold }

/// <summary>Validated PaissaDB delta. Data is cloned so it remains valid after parser disposal.</summary>
public sealed record PaissaDelta(PaissaDeltaKind Kind, int WorldId, int DistrictId, int ApiWardNumber,
    int ApiPlotNumber, JsonElement Data);

public static class PaissaDeltaParser
{
    public static bool TryParse(string json, out PaissaDelta? delta, out string? error)
    {
        delta = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > 64 * 1024)
        { error = "Delta payload is empty or exceeds 64 KiB."; return false; }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            { error = "Delta envelope has no string type."; return false; }
            var kind = typeElement.GetString() switch
            {
                "plot_open" => PaissaDeltaKind.PlotOpen,
                "plot_update" => PaissaDeltaKind.PlotUpdate,
                "plot_sold" => PaissaDeltaKind.PlotSold,
                "ping" => (PaissaDeltaKind?)null,
                _ => null,
            };
            if (kind is null) return true; // Keepalive or forward-compatible event.
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            { error = "Delta event has no object data."; return false; }

            if (!ReadInt(data, "world_id", out var worldId) || worldId <= 0 ||
                !ReadInt(data, "district_id", out var districtId) || districtId is not (339 or 340 or 341 or 641 or 979) ||
                !ReadInt(data, "ward_number", out var ward) || ward is < 0 or > 29 ||
                !ReadInt(data, "plot_number", out var plot) || plot is < 0 or > 59)
            { error = "Delta has an invalid world or plot identity."; return false; }

            if (!ReadDouble(data, "last_updated_time", out _))
            { error = "Delta has no valid source update timestamp."; return false; }
            if (kind == PaissaDeltaKind.PlotOpen &&
                (!ReadInt(data, "size", out var size) || size is < 0 or > 2 ||
                 !ReadLong(data, "price", out var price) || price < 0 ||
                 !ReadInt(data, "purchase_system", out var purchase) || purchase is < 0 or > 7 ||
                 !ReadDouble(data, "first_seen_time", out _)))
            { error = "Plot delta is missing required listing fields."; return false; }

            delta = new(kind.Value, worldId, districtId, ward, plot, data.Clone());
            return true;
        }
        catch (JsonException)
        {
            error = "Delta payload is not valid bounded JSON.";
            return false;
        }
    }

    public static bool TryApply(PaissaHousingWorldData? current, PaissaWorld world, PaissaDelta delta,
        DateTimeOffset receivedAtUtc, out PaissaHousingWorldData updated)
    {
        updated = current ?? new(world, DateTimeOffset.MinValue, Array.Empty<PaissaPlotListing>(), Array.Empty<string>());
        if (world.Id != delta.WorldId || receivedAtUtc.Offset != TimeSpan.Zero) return false;

        var identityMatches = updated.Listings.Where(plot => plot.DistrictId == delta.DistrictId &&
            plot.ApiWardNumber == delta.ApiWardNumber && plot.ApiPlotNumber == delta.ApiPlotNumber).ToArray();
        var existing = identityMatches.FirstOrDefault();
        ReadDouble(delta.Data, "last_updated_time", out var eventTimestamp);
        if (existing?.LastUpdatedTime is { } oldTime && oldTime > eventTimestamp)
            return false;

        var listings = updated.Listings.Where(plot => !(plot.DistrictId == delta.DistrictId &&
            plot.ApiWardNumber == delta.ApiWardNumber && plot.ApiPlotNumber == delta.ApiPlotNumber)).ToList();
        if (delta.Kind != PaissaDeltaKind.PlotSold)
        {
            var listing = new PaissaPlotListing(
                delta.WorldId, delta.DistrictId, DistrictName(delta.DistrictId),
                delta.ApiWardNumber, delta.ApiPlotNumber, delta.ApiWardNumber + 1, delta.ApiPlotNumber + 1,
                NullableInt(delta.Data, "size", existing?.Size),
                NullableLong(delta.Data, "price", existing?.Price),
                NullableInt(delta.Data, "purchase_system", existing?.PurchaseSystem),
                NullableLong(delta.Data, "lotto_entries", existing?.LottoEntries),
                NullableInt(delta.Data, "lotto_phase", existing?.LottoPhase),
                NullableLong(delta.Data, "lotto_phase_until", existing?.LottoPhaseUntil),
                NullableDouble(delta.Data, "first_seen_time", existing?.FirstSeenTime),
                NullableDouble(delta.Data, "last_updated_time", existing?.LastUpdatedTime),
                NullableDouble(delta.Data, "est_time_open_min", existing?.EstimatedOpenMin),
                NullableDouble(delta.Data, "est_time_open_max", existing?.EstimatedOpenMax));
            listings.Add(listing);
        }

        updated = new(world, receivedAtUtc, listings.OrderBy(plot => plot.DistrictId).ThenBy(plot => plot.ApiWardNumber)
            .ThenBy(plot => plot.ApiPlotNumber).ToArray(), updated.Warnings);
        return true;
    }

    private static string DistrictName(int id) => id switch
    {
        339 => "Mist", 340 => "The Lavender Beds", 341 => "The Goblet", 641 => "Shirogane", 979 => "Empyreum",
        _ => "Unknown",
    };

    private static bool ReadInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out value);
    }

    private static bool ReadLong(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt64(out value);
    }

    private static bool ReadDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out value) && double.IsFinite(value);
    }

    private static int? NullableInt(JsonElement element, string name, int? fallback)
        => !element.TryGetProperty(name, out var property) ? fallback
            : property.ValueKind == JsonValueKind.Null ? null
            : property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value) ? value : fallback;

    private static long? NullableLong(JsonElement element, string name, long? fallback)
        => !element.TryGetProperty(name, out var property) ? fallback
            : property.ValueKind == JsonValueKind.Null ? null
            : property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value) ? value : fallback;

    private static double? NullableDouble(JsonElement element, string name, double? fallback)
        => !element.TryGetProperty(name, out var property) ? fallback
            : property.ValueKind == JsonValueKind.Null ? null
            : property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value) && double.IsFinite(value) ? value : fallback;
}
