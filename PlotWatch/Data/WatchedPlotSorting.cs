using PlotWatch.Paissa;

namespace PlotWatch.Data;

public enum WatchedPlotSortColumn { Address, SizePrice, Entrants, ReportedStatus, CacheAge }
public sealed record WatchedPlotRow(WatchedPlot Watch, WatchedPlotView View);

public static class WatchedPlotSorting
{
    public static IReadOnlyList<WatchedPlotRow> Sort(IEnumerable<WatchedPlot> plots,
        IReadOnlyDictionary<int, PaissaHousingWorldData> cache, WatchedPlotSortColumn column, bool ascending, DateTimeOffset now)
    {
        var rows = plots.Select(watch => new WatchedPlotRow(watch,
            WatchedPlotPolicy.Resolve(watch, cache.GetValueOrDefault(watch.Address.WorldId))));
        var ordered = column switch
        {
            WatchedPlotSortColumn.SizePrice => Then(Order(rows, Size, ascending), row => row.View.Listing?.Price, ascending),
            WatchedPlotSortColumn.Entrants => Order(rows, row => row.View.Listing?.LottoEntries, ascending),
            WatchedPlotSortColumn.ReportedStatus => Order(rows, Status, ascending),
            WatchedPlotSortColumn.CacheAge => Order(rows, row => Age(row, now), ascending),
            _ => Then(Then(Order(rows, row => row.Watch.Address.DistrictId, ascending),
                row => row.Watch.Address.Ward, ascending), row => row.Watch.Address.Plot, ascending),
        };
        return ordered.ThenBy(row => row.Watch.Address.WorldId).ThenBy(row => row.Watch.Address.DistrictId)
            .ThenBy(row => row.Watch.Address.Ward).ThenBy(row => row.Watch.Address.Plot).ToArray();
    }

    private static int? Size(WatchedPlotRow row) => row.View.Listing?.Size is >= 0 and <= 2 ? row.View.Listing.Size : null;

    private static PaissaStatusFilter? Status(WatchedPlotRow row)
    {
        // Missing listings show a separate availability message, not their historical bidding status.
        if (row.View.Availability != WatchedPlotAvailability.Listed) return null;
        var status = PaissaHousingPolicy.GetReportedStatusFilter(row.View.Listing?.LottoPhase);
        return status == PaissaStatusFilter.Unknown ? null : status;
    }

    private static long? Age(WatchedPlotRow row, DateTimeOffset now)
        => row.View.DownloadedAtUtc is { } downloaded && downloaded != default ? Math.Max(0, (now - downloaded).Ticks) : null;

    private static IOrderedEnumerable<WatchedPlotRow> Order<T>(IEnumerable<WatchedPlotRow> rows, Func<WatchedPlotRow, T> key, bool ascending)
    {
        var knownFirst = rows.OrderBy(row => key(row) is null);
        return ascending ? knownFirst.ThenBy(key) : knownFirst.ThenByDescending(key);
    }

    private static IOrderedEnumerable<WatchedPlotRow> Then<T>(IOrderedEnumerable<WatchedPlotRow> rows, Func<WatchedPlotRow, T> key, bool ascending)
    {
        var knownFirst = rows.ThenBy(row => key(row) is null);
        return ascending ? knownFirst.ThenBy(key) : knownFirst.ThenByDescending(key);
    }
}
