using PlotWatch.Paissa;

namespace PlotWatch.Data;

public static class BidCaptureEnrichment
{
    public static BidRecord Apply(BidRecord bid, PaissaHousingWorldData? snapshot)
    {
        if (snapshot is null || snapshot.World.Id != bid.WorldId) return bid;
        var matches = snapshot.Listings.Where(item => item.WorldId == bid.WorldId && item.DistrictId == bid.DistrictId &&
            item.WardNumber == bid.Ward && item.PlotNumber == bid.Plot).Take(2).ToArray();
        if (matches.Length != 1) return bid;
        var listing = matches[0];
        var size = listing.Size is >= 0 and <= 2 ? listing.Size : null;
        var price = listing.Price is >= 0 ? listing.Price : null;
        return bid with { Size = size, SizeFromCache = size is not null, Price = price,
            CacheEvidence = new(snapshot.DownloadedAtUtc,
                listing.LastUpdatedTime is { } updated && double.IsFinite(updated) ? updated : null, size, price) };
    }
}
