using PlotWatch.Paissa;

namespace PlotWatch.Data;

public sealed record PlotAddress(int WorldId, int DistrictId, int Ward, int Plot);
public sealed record WatchedListing(PaissaPlotListing Listing, DateTimeOffset DownloadedAtUtc);
public sealed record WatchedPlot(PlotAddress Address, string WorldName, DateTimeOffset TrackedAtUtc, WatchedListing? LastKnown = null);
public sealed record WatchedPlotDocument
{
    public const int CurrentVersion = 1;
    public int Version { get; init; } = CurrentVersion;
    public IReadOnlyList<WatchedPlot> Plots { get; init; } = [];
}

public enum WatchedPlotAvailability { WaitingForCache, Listed, NotInLatestListings }
public sealed record WatchedPlotView(WatchedPlotAvailability Availability, PaissaPlotListing? Listing,
    DateTimeOffset? DownloadedAtUtc, bool Historical, bool CacheOlderThanSaved = false);
public sealed record WatchedPlotGroup(int WorldId, string WorldName, IReadOnlyList<WatchedPlot> Plots);

public static class WatchedPlotPolicy
{
    public static IReadOnlyList<WatchedPlotGroup> GroupByWorld(IEnumerable<WatchedPlot> plots,
        IReadOnlyDictionary<int, PaissaHousingWorldData> cache)
        => plots.GroupBy(plot => plot.Address.WorldId).Select(group => new WatchedPlotGroup(group.Key,
                cache.TryGetValue(group.Key, out var snapshot) ? snapshot.World.Name
                    : group.Select(plot => plot.WorldName).Order(StringComparer.Ordinal).First(),
                group.OrderBy(plot => plot.Address.DistrictId).ThenBy(plot => plot.Address.Ward).ThenBy(plot => plot.Address.Plot).ToArray()))
            .OrderBy(group => group.WorldName, StringComparer.Ordinal).ThenBy(group => group.WorldId).ToArray();

    public static bool ValidAddress(PlotAddress? address) => address is { WorldId: > 0, Ward: >= 1 and <= 30, Plot: >= 1 and <= 60 }
        && address.DistrictId is 339 or 340 or 341 or 641 or 979;

    public static bool Matches(PlotAddress address, PaissaPlotListing plot)
        => address.WorldId == plot.WorldId && address.DistrictId == plot.DistrictId &&
            address.Ward == plot.WardNumber && address.Plot == plot.PlotNumber;

    public static PlotAddress? AddressOf(PaissaPlotListing plot)
    {
        if (plot.WorldId is not { } world || plot.DistrictId is not { } district ||
            plot.WardNumber is not { } ward || plot.PlotNumber is not { } number) return null;
        var address = new PlotAddress(world, district, ward, number);
        return ValidAddress(address) && (plot.ApiWardNumber is null || plot.ApiWardNumber == ward - 1) &&
            (plot.ApiPlotNumber is null || plot.ApiPlotNumber == number - 1) ? address : null;
    }

    public static string? Validate(WatchedPlotDocument document)
    {
        if (document.Version != WatchedPlotDocument.CurrentVersion || document.Plots is null) return "Unsupported or incomplete watchlist.";
        var addresses = new HashSet<PlotAddress>();
        foreach (var plot in document.Plots)
        {
            if (plot is null || !ValidAddress(plot.Address) || string.IsNullOrWhiteSpace(plot.WorldName) ||
                plot.TrackedAtUtc == default || !addresses.Add(plot.Address)) return "Invalid or duplicate watched plot.";
            if (plot.LastKnown is { } known && (known.Listing is null || known.DownloadedAtUtc == default ||
                AddressOf(known.Listing) != plot.Address)) return "Last-known listing does not match watched address.";
        }
        return null;
    }

    public static WatchedPlotView Resolve(WatchedPlot watch, PaissaHousingWorldData? snapshot)
    {
        var last = watch.LastKnown;
        if (snapshot is null || snapshot.World.Id != watch.Address.WorldId)
            return new(WatchedPlotAvailability.WaitingForCache, last?.Listing, null, true);
        if (last is not null && snapshot.DownloadedAtUtc < last.DownloadedAtUtc)
            return new(WatchedPlotAvailability.WaitingForCache, last.Listing, snapshot.DownloadedAtUtc, true, true);
        var matching = snapshot.Listings.FirstOrDefault(plot => Matches(watch.Address, plot));
        return matching is not null
            ? new(WatchedPlotAvailability.Listed, matching, snapshot.DownloadedAtUtc, false)
            : new(WatchedPlotAvailability.NotInLatestListings, last?.Listing, snapshot.DownloadedAtUtc, true);
    }
}
