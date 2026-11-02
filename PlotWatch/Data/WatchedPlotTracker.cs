using PlotWatch.Paissa;

namespace PlotWatch.Data;

/// <summary>Shared across characters; no character, bid, travel, or network dependency.</summary>
public sealed class WatchedPlotTracker
{
    private readonly IWatchedPlotStore store;
    private readonly Action<bool, string>? log;
    public WatchedPlotDocument Data { get; private set; }
    public bool IsReadOnly { get; private set; }
    public bool CanRestoreBackup { get; private set; }
    public string? Error { get; private set; }

    public WatchedPlotTracker(IWatchedPlotStore store, Action<bool, string>? log = null)
    {
        this.store = store;
        this.log = log;
        var loaded = store.Load();
        Data = loaded.Data;
        IsReadOnly = loaded.ReadOnly;
        CanRestoreBackup = loaded.CanRestoreBackup;
        Error = loaded.Error;
    }

    public bool Contains(PlotAddress address) => Data.Plots.Any(plot => plot.Address == address);

    public bool Track(PaissaHousingWorldData snapshot, PaissaPlotListing listing, DateTimeOffset now)
    {
        var address = WatchedPlotPolicy.AddressOf(listing);
        if (address is null || address.WorldId != snapshot.World.Id || snapshot.DownloadedAtUtc == default ||
            !snapshot.Listings.Contains(listing) || string.IsNullOrWhiteSpace(snapshot.World.Name))
        { Error = "Cannot track: valid world, district, ward, plot, and cached listing required."; return false; }
        if (IsReadOnly) return false;
        if (Contains(address)) { Error = null; return true; } // Never save a duplicate or reset tracking time.
        var watch = new WatchedPlot(address, snapshot.World.Name, now.ToUniversalTime(), new(listing, snapshot.DownloadedAtUtc));
        return Commit(Data with { Plots = Data.Plots.Append(watch).ToArray() });
    }

    public bool Untrack(PlotAddress address)
    {
        if (IsReadOnly) return false;
        if (!Contains(address)) return true;
        return Commit(Data with { Plots = Data.Plots.Where(plot => plot.Address != address).ToArray() });
    }

    // One save per accepted world snapshot or explicit recovery, never once per rendered frame.
    public bool ObserveWorld(PaissaHousingWorldData? snapshot)
    {
        if (IsReadOnly || snapshot is null || snapshot.DownloadedAtUtc == default) return false;
        var changed = false;
        var next = Data.Plots.Select(watch =>
        {
            if (watch.Address.WorldId != snapshot.World.Id || watch.LastKnown is { } old && snapshot.DownloadedAtUtc <= old.DownloadedAtUtc)
                return watch;
            var listing = snapshot.Listings.FirstOrDefault(plot => WatchedPlotPolicy.Matches(watch.Address, plot));
            if (listing is null || WatchedPlotPolicy.AddressOf(listing) != watch.Address) return watch;
            changed = true;
            return watch with { WorldName = snapshot.World.Name, LastKnown = new(listing, snapshot.DownloadedAtUtc) };
        }).ToArray();
        return changed && Commit(Data with { Plots = next });
    }

    public bool RestoreBackup()
    {
        if (!CanRestoreBackup) return false;
        try
        {
            store.RestoreBackup(Data);
            IsReadOnly = CanRestoreBackup = false;
            Error = null;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { Error = "Watchlist restore failed. Files remain preserved; check disk space/permissions and retry."; return false; }
    }

    private bool Commit(WatchedPlotDocument next)
    {
        if (IsReadOnly) return false;
        if (WatchedPlotPolicy.Validate(next) is { } invalid) { Error = invalid; return false; }
        try
        {
            store.Save(next);
            Data = next;
            Error = null;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Error = "Watchlist save failed; changes were not applied. Check disk space/permissions and retry. Cached detail saves retry on the next accepted world refresh.";
            log?.Invoke(false, Error);
            return false;
        }
    }
}
