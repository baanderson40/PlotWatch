using System.Globalization;
using System.Text.Json;

namespace PlotWatch.Paissa;

public sealed record PaissaWorld(int Id, string Name, int DataCenterId, string DataCenterName);

public sealed record PaissaPlotSample(
    int? WorldId,
    int? DistrictId,
    int? WardNumber,
    int? PlotNumber,
    int? Size,
    long? Price,
    int? PurchaseSystem,
    long? LottoEntries,
    int? LottoPhase,
    long? LottoPhaseUntil,
    double? FirstSeenTime,
    double? LastUpdatedTime,
    double? EstimatedOpenMin,
    double? EstimatedOpenMax);

public sealed record PaissaPlotListing(
    int? WorldId,
    int? DistrictId,
    string DistrictName,
    int? ApiWardNumber,
    int? ApiPlotNumber,
    int? WardNumber,
    int? PlotNumber,
    int? Size,
    long? Price,
    int? PurchaseSystem,
    long? LottoEntries,
    int? LottoPhase,
    long? LottoPhaseUntil,
    double? FirstSeenTime,
    double? LastUpdatedTime,
    double? EstimatedOpenMin,
    double? EstimatedOpenMax);

public sealed record PaissaFieldSummary(string Name, int PresentCount, int NullCount, int MissingCount, string SampleValue);

public sealed record PaissaDistrictSummary(int Id, string Name, int? ReportedOpenPlots, int ActualPlotCount, double? OldestPlotTime);

public sealed record PaissaWorldInspection(
    int Id,
    string Name,
    int? ReportedOpenPlots,
    double? OldestPlotTime,
    IReadOnlyList<PaissaDistrictSummary> Districts,
    IReadOnlyList<PaissaFieldSummary> Fields,
    IReadOnlyList<PaissaPlotSample> Samples,
    IReadOnlyList<PaissaPlotListing> Listings,
    IReadOnlyList<string> Warnings);

public static class PaissaApiParser
{
    private static readonly string[] PlotFields =
    [
        "world_id", "district_id", "ward_number", "plot_number", "size", "price",
        "last_updated_time", "first_seen_time", "est_time_open_min", "est_time_open_max",
        "purchase_system", "lotto_entries", "lotto_phase", "lotto_phase_until",
    ];

    public static IReadOnlyList<PaissaWorld> ParseWorlds(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("World list response must be a JSON array.");

        return document.RootElement.EnumerateArray().Select(item => new PaissaWorld(
            RequiredInt(item, "id"),
            RequiredString(item, "name"),
            RequiredInt(item, "datacenter_id"),
            RequiredString(item, "datacenter_name"))).ToArray();
    }

    public static PaissaWorldInspection InspectWorld(string json, int sampleLimit = 8)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("World detail response must be a JSON object.");
        var warnings = new List<string>();
        foreach (var required in new[] { "id", "name", "districts", "num_open_plots", "oldest_plot_time" })
            if (!root.TryGetProperty(required, out _))
                warnings.Add($"World field '{required}' is missing.");

        if (root.TryGetProperty("districts", out var districtElement) && districtElement.ValueKind != JsonValueKind.Array)
            warnings.Add("World field 'districts' is not an array.");
        var districtItems = GetArray(root, "districts");
        var districts = new List<PaissaDistrictSummary>();
        var plots = new List<JsonElement>();
        var listings = new List<PaissaPlotListing>();
        var identities = new HashSet<(int World, int District, int Ward, int Plot)>();

        foreach (var district in districtItems)
        {
            foreach (var required in new[] { "id", "name", "num_open_plots", "oldest_plot_time", "open_plots" })
                if (!district.TryGetProperty(required, out _))
                    warnings.Add($"District field '{required}' is missing.");

            if (district.TryGetProperty("open_plots", out var plotsElement) && plotsElement.ValueKind != JsonValueKind.Array)
                warnings.Add($"District '{OptionalString(district, "name") ?? "Unknown"}' open_plots is not an array.");
            var openPlots = GetArray(district, "open_plots");
            districts.Add(new PaissaDistrictSummary(
                OptionalInt(district, "id") ?? -1,
                OptionalString(district, "name") ?? "Unknown",
                OptionalInt(district, "num_open_plots"),
                openPlots.Count,
                OptionalDouble(district, "oldest_plot_time")));
            plots.AddRange(openPlots);
            foreach (var plot in openPlots)
            {
                foreach (var required in new[] { "world_id", "district_id", "ward_number", "plot_number", "size", "price", "last_updated_time", "first_seen_time", "purchase_system" })
                    if (!plot.TryGetProperty(required, out _))
                        warnings.Add($"Plot in district '{OptionalString(district, "name") ?? "Unknown"}' is missing required field '{required}'.");

                var parsed = ParsePlot(plot);
                if (parsed.WorldId is { } world && parsed.DistrictId is { } districtId && parsed.WardNumber is { } ward && parsed.PlotNumber is { } plotNumber)
                {
                    if (!identities.Add((world, districtId, ward, plotNumber)))
                    {
                        warnings.Add($"Duplicate plot identity: world {world}, district {districtId}, ward {ward}, plot {plotNumber}.");
                        continue;
                    }

                    if (OptionalInt(root, "id") is { } responseWorldId && world != responseWorldId)
                        warnings.Add($"Plot world ID {world} differs from response world ID {responseWorldId}.");
                    if (OptionalInt(district, "id") is { } responseDistrictId && districtId != responseDistrictId)
                        warnings.Add($"Plot district ID {districtId} differs from containing district ID {responseDistrictId}.");
                }

                listings.Add(new PaissaPlotListing(
                    parsed.WorldId,
                    parsed.DistrictId,
                    OptionalString(district, "name") ?? "Unknown",
                    parsed.WardNumber,
                    parsed.PlotNumber,
                    parsed.WardNumber is { } apiWard ? apiWard + 1 : null,
                    parsed.PlotNumber is { } apiPlot ? apiPlot + 1 : null,
                    parsed.Size,
                    parsed.Price,
                    parsed.PurchaseSystem,
                    parsed.LottoEntries,
                    parsed.LottoPhase,
                    parsed.LottoPhaseUntil,
                    parsed.FirstSeenTime,
                    parsed.LastUpdatedTime,
                    parsed.EstimatedOpenMin,
                    parsed.EstimatedOpenMax));
            }
        }

        foreach (var district in districts)
            if (district.ReportedOpenPlots is { } reported && reported != district.ActualPlotCount)
                warnings.Add($"District '{district.Name}' reports {reported} open plots but contains {district.ActualPlotCount}.");

        var totalReported = OptionalInt(root, "num_open_plots");
        if (totalReported is { } worldReported && worldReported != listings.Count)
            warnings.Add($"World reports {worldReported} open plots but contains {listings.Count}.");

        var fields = PlotFields.Select(field => SummarizeField(plots, field)).ToArray();
        var samples = plots.Take(Math.Clamp(sampleLimit, 0, 20)).Select(ParsePlot).ToArray();

        return new PaissaWorldInspection(
            OptionalInt(root, "id") ?? -1,
            OptionalString(root, "name") ?? "Unknown",
            OptionalInt(root, "num_open_plots"),
            OptionalDouble(root, "oldest_plot_time"),
            districts,
            fields,
            samples,
            listings,
            warnings);
    }

    public static string DescribePurchaseSystem(int? value) => value switch
    {
        null => "Unknown",
        1 => "Lottery",
        2 => "Free Company",
        3 => "Lottery + Free Company",
        4 => "Personal",
        5 => "Lottery + Personal",
        6 => "Free Company + Personal",
        7 => "Lottery + Free Company + Personal",
        _ => $"Unrecognized ({value})",
    };

    public static string DescribeLotteryPhase(int? value) => value switch
    {
        null => "Unknown / not reported",
        1 => "Available",
        2 => "Results period",
        3 => "Closed / unavailable",
        _ => $"Unrecognized ({value})",
    };

    public static DateTimeOffset? UnixSecondsToUtc(double? seconds)
    {
        if (seconds is null || !double.IsFinite(seconds.Value))
            return null;

        try
        {
            var ticks = checked((long)Math.Round(seconds.Value * TimeSpan.TicksPerSecond));
            return DateTimeOffset.UnixEpoch.AddTicks(ticks);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static PaissaFieldSummary SummarizeField(IReadOnlyList<JsonElement> plots, string field)
    {
        var present = 0;
        var nulls = 0;
        var missing = 0;
        string sample = "None";
        foreach (var plot in plots)
        {
            if (!plot.TryGetProperty(field, out var value))
            {
                missing++;
                continue;
            }

            present++;
            if (value.ValueKind == JsonValueKind.Null)
            {
                nulls++;
                continue;
            }

            if (sample == "None")
            {
                sample = value.ToString();
                if (sample.Length > 96)
                    sample = sample[..93] + "...";
            }
        }

        return new PaissaFieldSummary(field, present, nulls, missing, sample);
    }

    private static PaissaPlotSample ParsePlot(JsonElement item) => new(
        OptionalInt(item, "world_id"),
        OptionalInt(item, "district_id"),
        OptionalInt(item, "ward_number"),
        OptionalInt(item, "plot_number"),
        OptionalInt(item, "size"),
        OptionalLong(item, "price"),
        OptionalInt(item, "purchase_system"),
        OptionalLong(item, "lotto_entries"),
        OptionalInt(item, "lotto_phase"),
        OptionalLong(item, "lotto_phase_until"),
        OptionalDouble(item, "first_seen_time"),
        OptionalDouble(item, "last_updated_time"),
        OptionalDouble(item, "est_time_open_min"),
        OptionalDouble(item, "est_time_open_max"));

    private static List<JsonElement> GetArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];
        return value.EnumerateArray().ToList();
    }

    private static int RequiredInt(JsonElement parent, string name)
        => OptionalInt(parent, name) ?? throw new JsonException($"Required integer field '{name}' is missing or invalid.");

    private static string RequiredString(JsonElement parent, string name)
        => OptionalString(parent, name) ?? throw new JsonException($"Required string field '{name}' is missing or invalid.");

    private static int? OptionalInt(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static long? OptionalLong(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static double? OptionalDouble(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;

    private static string? OptionalString(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
