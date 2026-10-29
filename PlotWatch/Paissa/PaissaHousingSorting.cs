namespace PlotWatch.Paissa;

public static class PaissaHousingSorting
{
    public static List<(PaissaHousingWorldData Snapshot, PaissaPlotListing Plot)> Sort(
        IEnumerable<(PaissaHousingWorldData Snapshot, PaissaPlotListing Plot)> rows,
        int column, bool ascending, DateTimeOffset asOfUtc)
    {
        var ordered = column switch
        {
            1 => Order(rows, row => row.Plot.DistrictName, ascending),
            2 => Order(rows, row => row.Plot.WardNumber, ascending),
            3 => Order(rows, row => row.Plot.PlotNumber, ascending),
            4 => Order(rows, row => row.Plot.Size, ascending),
            5 => Order(rows, row => row.Plot.Price, ascending),
            6 => Order(rows, row => PaissaHousingPolicy.GetEligibility(row.Plot.PurchaseSystem), ascending),
            7 => Order(rows, row => PhaseKey(row.Plot, asOfUtc), ascending),
            8 => Order(rows, row => row.Plot.LottoEntries, ascending),
            9 => Order(rows, row => PaissaHousingPolicy.GetDisplayedPhaseDeadline(row.Plot, asOfUtc), ascending),
            10 => Order(rows, row => PaissaApiParser.UnixSecondsToUtc(row.Plot.LastUpdatedTime), ascending),
            _ => Order(rows, row => row.Snapshot.World.Name, ascending),
        };
        return ordered.ThenBy(row => row.Snapshot.World.Name, StringComparer.Ordinal)
            .ThenBy(row => row.Plot.DistrictName, StringComparer.Ordinal)
            .ThenBy(row => row.Plot.WardNumber ?? int.MaxValue)
            .ThenBy(row => row.Plot.PlotNumber ?? int.MaxValue)
            .ToList();
    }

    private static PaissaPhaseFilter? PhaseKey(PaissaPlotListing plot, DateTimeOffset asOfUtc)
    {
        var phase = PaissaHousingPolicy.GetPhaseFilter(plot, asOfUtc);
        return phase == PaissaPhaseFilter.Unknown ? null : phase;
    }

    private static IOrderedEnumerable<(PaissaHousingWorldData Snapshot, PaissaPlotListing Plot)> Order<TKey>(
        IEnumerable<(PaissaHousingWorldData Snapshot, PaissaPlotListing Plot)> rows,
        Func<(PaissaHousingWorldData Snapshot, PaissaPlotListing Plot), TKey> key, bool ascending)
    {
        // Nulls stay last in both directions; only known values reverse.
        var knownFirst = rows.OrderBy(row => key(row) is null);
        return ascending ? knownFirst.ThenBy(key) : knownFirst.ThenByDescending(key);
    }
}
