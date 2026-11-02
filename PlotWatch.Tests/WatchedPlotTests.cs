using System.Text.Json;
using PlotWatch.Data;
using PlotWatch.Paissa;

internal static class WatchedPlotTests
{
    public static readonly (string Name, Action Test)[] Cases =
    [
        ("watchlist validates complete normalized plot addresses", Addresses),
        ("watchlist add is idempotent and removal targets exact shared address", AddRemove),
        ("watchlist cache resolution separates missing present and no cache", Resolution),
        ("watchlist updates batch by world and preserve null versus zero", Updates),
        ("watchlist failed or older refresh cannot erase last-known details", FailedRefresh),
        ("watchlist save failures retain published state and allow retry", SaveFailure),
        ("watchlist persists shared data independently of bids and settings", RoundTrip),
        ("watchlist creates previous-save backup and deletion survives reload", Backup),
        ("watchlist corrupt primary displays backup read-only until explicit recovery", Recovery),
        ("watchlist corrupt files block writes without data loss", CorruptFiles),
        ("watchlist newer primary schema blocks downgrade recovery", FutureSchema),
        ("watchlist invalid documents block writes", InvalidDocuments),
        ("watchlist backup-only install requires recovery", BackupOnly),
        ("watchlist disk write failure cleans temporary files", DiskFailure),
        ("watchlist failed restore stays read-only", RestoreFailure),
        ("watchlist server groups use world IDs counts and stable address order", ServerGroups),
        ("watchlist server labels prefer cache without merging equal names", ServerNames),
        ("watchlist collapsed server preferences survive reload and default expanded", CollapsePreferences),
        ("watchlist address header sorts district ward and plot in both directions", SortAddress),
        ("watchlist size and price sort keeps unknown values last", SortSizePrice),
        ("watchlist entrant sort preserves zero and puts missing counts last", SortEntrants),
        ("watchlist status sort ignores historical status for missing listings", SortStatus),
        ("watchlist cache age sorts youngest first and missing downloads last", SortCacheAge),
    ];
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 21, 0, 0, TimeSpan.Zero);
    private static PaissaPlotListing Plot => new(65, 339, "Mist", 4, 10, 5, 11, 0, 3187500, 3,
        null, null, null, Now.AddDays(-1).ToUnixTimeSeconds(), Now.ToUnixTimeSeconds(), null, null);
    private static PaissaHousingWorldData World(params PaissaPlotListing[] plots)
        => new(new(65, "Midgardsormr", 4, "Aether"), Now, plots, []);
    private static PlotAddress Address => new(65, 339, 5, 11);
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}.");
    }
    private static WatchedPlotTracker Tracked(MemoryStore store)
    {
        var tracker = new WatchedPlotTracker(store);
        Equal(true, tracker.Track(World(Plot), Plot, Now));
        return tracker;
    }

    private static void Addresses()
    {
        Equal(Address, WatchedPlotPolicy.AddressOf(Plot));
        foreach (var bad in new[]
        {
            Plot with { WorldId = null }, Plot with { WorldId = 0 }, Plot with { DistrictId = 0 },
            Plot with { WardNumber = 0 }, Plot with { WardNumber = 31 }, Plot with { PlotNumber = 0 },
            Plot with { PlotNumber = 61 }, Plot with { ApiWardNumber = 5 }, Plot with { ApiPlotNumber = 11 },
        })
        {
            Equal<PlotAddress?>(null, WatchedPlotPolicy.AddressOf(bad));
            Equal(false, new WatchedPlotTracker(new MemoryStore()).Track(World(bad), bad, Now));
        }
        var tracker = new WatchedPlotTracker(new MemoryStore());
        Equal(false, tracker.Track(World(), Plot, Now));
        Equal(false, tracker.Track(World(Plot) with { World = new(54, "Faerie", 4, "Aether") }, Plot, Now));
        Equal(Address with { Ward = 30, Plot = 60 }, WatchedPlotPolicy.AddressOf(Plot with { WardNumber = 30, PlotNumber = 60, ApiWardNumber = 29, ApiPlotNumber = 59 }));
    }

    private static void AddRemove()
    {
        var store = new MemoryStore();
        var tracker = Tracked(store);
        Equal(true, tracker.Track(World(Plot), Plot, Now.AddDays(1)));
        Equal(1, store.Saves);
        Equal(Now, tracker.Data.Plots.Single().TrackedAtUtc);
        var other = Plot with { WorldId = 54 };
        var world = new PaissaHousingWorldData(new(54, "Faerie", 4, "Aether"), Now, [other], []);
        Equal(true, tracker.Track(world, other, Now));
        Equal(2, tracker.Data.Plots.Count);
        Equal(true, tracker.Untrack(Address));
        Equal(54, tracker.Data.Plots.Single().Address.WorldId);
        Equal(true, tracker.Untrack(Address));
        Equal(3, store.Saves);
    }

    private static void Resolution()
    {
        var watch = Tracked(new MemoryStore()).Data.Plots.Single();
        var noCache = WatchedPlotPolicy.Resolve(watch, null);
        Equal(WatchedPlotAvailability.WaitingForCache, noCache.Availability);
        Equal(true, noCache.Historical);
        Equal<DateTimeOffset?>(null, noCache.DownloadedAtUtc);
        var present = WatchedPlotPolicy.Resolve(watch, World(Plot));
        Equal(WatchedPlotAvailability.Listed, present.Availability);
        Equal(false, present.Historical);
        var missing = WatchedPlotPolicy.Resolve(watch, World() with { DownloadedAtUtc = Now.AddHours(1) });
        Equal(WatchedPlotAvailability.NotInLatestListings, missing.Availability);
        Equal(true, missing.Historical);
        Equal(Plot, missing.Listing);
        Equal<DateTimeOffset?>(Now.AddHours(1), missing.DownloadedAtUtc);
        Equal(Now, watch.LastKnown!.DownloadedAtUtc);
        var wrongWorld = World(Plot) with { World = new(54, "Faerie", 4, "Aether") };
        Equal(WatchedPlotAvailability.WaitingForCache, WatchedPlotPolicy.Resolve(watch, wrongWorld).Availability);
        Equal(WatchedPlotAvailability.NotInLatestListings, WatchedPlotPolicy.Resolve(watch, World(Plot with { DistrictId = 340 })).Availability);
        Equal(WatchedPlotAvailability.NotInLatestListings, WatchedPlotPolicy.Resolve(watch, World(Plot with { WardNumber = 6 })).Availability);
        Equal(WatchedPlotAvailability.NotInLatestListings, WatchedPlotPolicy.Resolve(watch, World(Plot with { PlotNumber = 12 })).Availability);
    }

    private static void Updates()
    {
        var store = new MemoryStore();
        var tracker = Tracked(store);
        var second = Plot with { PlotNumber = 12, ApiPlotNumber = 11 };
        tracker.Track(World(second), second, Now);
        Equal(2, store.Saves);
        var next = Plot with { LottoEntries = 0, LottoPhase = 1, LastUpdatedTime = Now.AddMinutes(1).ToUnixTimeSeconds() };
        var snapshot = World(next, second with { LottoEntries = 17 }) with { DownloadedAtUtc = Now.AddMinutes(2) };
        Equal(true, tracker.ObserveWorld(snapshot));
        Equal(3, store.Saves); // Two plots, one accepted world, one write.
        Equal<long?>(0, tracker.Data.Plots[0].LastKnown!.Listing.LottoEntries);
        Equal<long?>(17, tracker.Data.Plots[1].LastKnown!.Listing.LottoEntries);
        Equal<double?>(next.LastUpdatedTime, tracker.Data.Plots[0].LastKnown!.Listing.LastUpdatedTime);
        Equal(false, tracker.ObserveWorld(snapshot));
        Equal(3, store.Saves);
    }

    private static void FailedRefresh()
    {
        var tracker = Tracked(new MemoryStore());
        var original = tracker.Data;
        Equal(false, tracker.ObserveWorld(null));
        Equal(false, tracker.ObserveWorld(World() with { DownloadedAtUtc = Now.AddMinutes(1) }));
        Equal(original, tracker.Data);
        var newer = World(Plot with { LottoEntries = 12 }) with { DownloadedAtUtc = Now.AddHours(1) };
        Equal(true, tracker.ObserveWorld(newer));
        Equal(false, tracker.ObserveWorld(World(Plot)));
        Equal<long?>(12, tracker.Data.Plots.Single().LastKnown!.Listing.LottoEntries);
        var view = WatchedPlotPolicy.Resolve(tracker.Data.Plots.Single(), World());
        Equal(WatchedPlotAvailability.WaitingForCache, view.Availability);
        Equal(true, view.CacheOlderThanSaved);
        Equal<long?>(12, view.Listing!.LottoEntries);
    }

    private static void SaveFailure()
    {
        var store = new MemoryStore { FailSave = true };
        var tracker = new WatchedPlotTracker(store);
        Equal(false, tracker.Track(World(Plot), Plot, Now));
        Equal(0, tracker.Data.Plots.Count);
        store.FailSave = false;
        Equal(true, tracker.Track(World(Plot), Plot, Now));
        var original = tracker.Data;
        store.FailSave = true;
        Equal(false, tracker.Untrack(Address));
        Equal(original, tracker.Data);
        Equal(false, tracker.ObserveWorld(World(Plot with { LottoEntries = 9 }) with { DownloadedAtUtc = Now.AddHours(1) }));
        Equal(original, tracker.Data);
        store.FailSave = false;
        Equal(true, tracker.Untrack(Address));
        Equal(0, tracker.Data.Plots.Count);
    }

    private static void WithFile(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "PlotWatch.WatchTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { test(Path.Combine(directory, "watched-plots.json")); }
        finally { Directory.Delete(directory, recursive: true); }
    }
    private static WatchedPlotTracker FileTracker(string path) => new(new WatchedPlotFileStore(path));
    private static void Track(WatchedPlotTracker tracker) => Equal(true, tracker.Track(World(Plot), Plot, Now));

    private static void RoundTrip() => WithFile(path =>
    {
        var root = Path.GetDirectoryName(path)!;
        File.WriteAllText(Path.Combine(root, "bids.json"), "untouched bids");
        File.WriteAllText(Path.Combine(root, "settings.json"), "character A");
        var tracker = FileTracker(path);
        Track(tracker);
        File.WriteAllText(Path.Combine(root, "settings.json"), "character B");
        var loaded = FileTracker(path);
        Equal(false, loaded.IsReadOnly);
        Equal(tracker.Data.Plots.Single(), loaded.Data.Plots.Single());
        Equal("untouched bids", File.ReadAllText(Path.Combine(root, "bids.json")));
        Equal(false, File.ReadAllText(path).Contains("Character"));
        Equal("character B", File.ReadAllText(Path.Combine(root, "settings.json")));
    });

    private static void Backup() => WithFile(path =>
    {
        var tracker = FileTracker(path);
        Track(tracker);
        tracker.Untrack(Address);
        Equal(0, FileTracker(path).Data.Plots.Count);
        Equal(1, JsonSerializer.Deserialize<WatchedPlotDocument>(File.ReadAllText(path + ".bak"))!.Plots.Count);
    });

    private static void Recovery() => WithFile(path =>
    {
        var tracker = FileTracker(path);
        Track(tracker);
        tracker.Untrack(Address);
        File.WriteAllText(path, "broken-original");
        var recovered = FileTracker(path);
        Equal(true, recovered.IsReadOnly);
        Equal(true, recovered.CanRestoreBackup);
        Equal(1, recovered.Data.Plots.Count);
        Equal(false, recovered.Untrack(Address));
        Equal(false, recovered.ObserveWorld(World(Plot) with { DownloadedAtUtc = Now.AddDays(1) }));
        Equal("broken-original", File.ReadAllText(path));
        Equal(true, recovered.RestoreBackup());
        Equal(false, recovered.IsReadOnly);
        Equal(1, FileTracker(path).Data.Plots.Count);
        Equal("broken-original", File.ReadAllText(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.preserved-*").Single()));
        Equal(true, File.Exists(path + ".bak"));
    });

    private static void CorruptFiles() => WithFile(path =>
    {
        File.WriteAllText(path, "bad-primary");
        File.WriteAllText(path + ".bak", "bad-backup");
        var tracker = FileTracker(path);
        Equal(true, tracker.IsReadOnly);
        Equal(false, tracker.CanRestoreBackup);
        Equal(false, tracker.Track(World(Plot), Plot, Now));
        Equal("bad-primary", File.ReadAllText(path));
        Equal("bad-backup", File.ReadAllText(path + ".bak"));
    });

    private static void FutureSchema() => WithFile(path =>
    {
        const string future = "{\"Version\":999}";
        File.WriteAllText(path, future);
        File.WriteAllText(path + ".bak", JsonSerializer.Serialize(new WatchedPlotDocument()));
        var tracker = FileTracker(path);
        Equal(true, tracker.IsReadOnly);
        Equal(false, tracker.CanRestoreBackup);
        Equal(false, tracker.RestoreBackup());
        Equal(future, File.ReadAllText(path));
    });

    private static void InvalidDocuments() => WithFile(path =>
    {
        var watch = Tracked(new MemoryStore()).Data.Plots.Single();
        foreach (var data in new[]
        {
            new WatchedPlotDocument { Plots = null! }, new WatchedPlotDocument { Plots = [watch, watch] },
            new WatchedPlotDocument { Plots = [watch with { Address = Address with { Plot = 0 } }] },
            new WatchedPlotDocument { Plots = [watch with { WorldName = "" }] },
            new WatchedPlotDocument { Plots = [watch with { LastKnown = new(Plot with { WorldId = 54 }, Now) }] },
            new WatchedPlotDocument { Version = 0 },
        })
        {
            var json = JsonSerializer.Serialize(data);
            File.WriteAllText(path, json);
            var tracker = FileTracker(path);
            Equal(true, tracker.IsReadOnly);
            Equal(false, tracker.Track(World(Plot), Plot, Now));
            Equal(json, File.ReadAllText(path));
        }
    });

    private static void BackupOnly() => WithFile(path =>
    {
        File.WriteAllText(path + ".bak", JsonSerializer.Serialize(new WatchedPlotDocument()));
        var tracker = FileTracker(path);
        Equal(true, tracker.IsReadOnly);
        Equal(true, tracker.CanRestoreBackup);
        Equal(false, File.Exists(path));
        Equal(true, tracker.RestoreBackup());
        Equal(true, File.Exists(path));
    });

    private static void DiskFailure() => WithFile(path =>
    {
        var tracker = FileTracker(path);
        Directory.CreateDirectory(path);
        Equal(false, tracker.Track(World(Plot), Plot, Now));
        Equal(0, tracker.Data.Plots.Count);
        Equal(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*").Length);
    });

    private static void RestoreFailure()
    {
        var tracker = new WatchedPlotTracker(new MemoryStore { ReadOnly = true, FailRestore = true });
        Equal(false, tracker.RestoreBackup());
        Equal(true, tracker.IsReadOnly);
        Equal(true, tracker.CanRestoreBackup);
    }

    private static WatchedPlot Watch(int world, string name, int ward = 5, int plot = 11)
        => new(new(world, 339, ward, plot), name, Now);

    private static void ServerGroups()
    {
        var plots = new[] { Watch(65, "Midgardsormr", 6), Watch(54, "Faerie"), Watch(65, "Midgardsormr", 5, 12), Watch(65, "Midgardsormr") };
        var groups = WatchedPlotPolicy.GroupByWorld(plots, new Dictionary<int, PaissaHousingWorldData>());
        Equal(2, groups.Count);
        Equal(54, groups[0].WorldId);
        Equal(1, groups[0].Plots.Count);
        Equal(65, groups[1].WorldId);
        Equal(3, groups[1].Plots.Count);
        Equal("5/11,5/12,6/11", string.Join(",", groups[1].Plots.Select(item => $"{item.Address.Ward}/{item.Address.Plot}")));
        Equal(0, WatchedPlotPolicy.GroupByWorld([], new Dictionary<int, PaissaHousingWorldData>()).Count);
    }

    private static void ServerNames()
    {
        var cache = new Dictionary<int, PaissaHousingWorldData> { [65] = World() };
        var groups = WatchedPlotPolicy.GroupByWorld(new[] { Watch(65, "Old display name"), Watch(54, "Midgardsormr") }, cache);
        Equal(2, groups.Count);
        Equal("Midgardsormr", groups[0].WorldName);
        Equal("Midgardsormr", groups[1].WorldName);
        Equal(54, groups[0].WorldId);
        Equal(65, groups[1].WorldId);
    }

    private static void CollapsePreferences()
    {
        var old = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>("{\"Version\":10,\"HousingPhaseMask\":2,\"HousingStatusMask\":4}")!;
        old.Normalize();
        Equal(0, old.CollapsedWatchedPlotWorldIds.Count);
        Equal(2, old.HousingPhaseMask);
        Equal(4, old.HousingStatusMask);
        old.CollapsedWatchedPlotWorldIds = [65, 54];
        var reloaded = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(Newtonsoft.Json.JsonConvert.SerializeObject(old))!;
        reloaded.Normalize();
        Equal("65,54", string.Join(",", reloaded.CollapsedWatchedPlotWorldIds));
        Equal(false, reloaded.CollapsedWatchedPlotWorldIds.Contains(40)); // New server starts expanded.
        reloaded.CollapsedWatchedPlotWorldIds.AddRange([65, -1, 0]);
        reloaded.Normalize();
        Equal("65,54", string.Join(",", reloaded.CollapsedWatchedPlotWorldIds));
        reloaded.Normalize();
        Equal("65,54", string.Join(",", reloaded.CollapsedWatchedPlotWorldIds));
        reloaded.ResetToDefaults();
        Equal(0, reloaded.CollapsedWatchedPlotWorldIds.Count);
        reloaded.CollapsedWatchedPlotWorldIds = null!;
        reloaded.Normalize();
        Equal(0, reloaded.CollapsedWatchedPlotWorldIds.Count);
    }

    private static PaissaPlotListing SortPlot(int number, int? size = 0, long? price = 3187500, long? entrants = 0, int? phase = 1)
        => Plot with { PlotNumber = number, ApiPlotNumber = number - 1, Size = size, Price = price, LottoEntries = entrants, LottoPhase = phase };

    private static WatchedPlot WatchFor(PaissaPlotListing listing)
        => new(WatchedPlotPolicy.AddressOf(listing)!, "Midgardsormr", Now, new(listing, Now));

    private static string SortOrder(PaissaPlotListing[] plots, WatchedPlotSortColumn column, bool ascending)
    {
        var cache = new Dictionary<int, PaissaHousingWorldData> { [65] = World(plots) };
        return string.Join(",", WatchedPlotSorting.Sort(plots.Select(WatchFor), cache, column, ascending, Now)
            .Select(row => row.Watch.Address.Plot));
    }

    private static void SortAddress()
    {
        var plots = new[]
        {
            SortPlot(1) with { DistrictId = 340, WardNumber = 1, ApiWardNumber = 0 },
            SortPlot(2) with { WardNumber = 2, ApiWardNumber = 1 },
            SortPlot(3) with { WardNumber = 1, ApiWardNumber = 0 },
            SortPlot(4) with { WardNumber = 1, ApiWardNumber = 0 },
        };
        Equal("3,4,2,1", SortOrder(plots, WatchedPlotSortColumn.Address, true));
        Equal("1,2,4,3", SortOrder(plots, WatchedPlotSortColumn.Address, false));
        Equal("3,4,2,1", SortOrder(plots, (WatchedPlotSortColumn)99, true));
    }

    private static void SortSizePrice()
    {
        var plots = new[] { SortPlot(1, 1, 200), SortPlot(2, 0, 300), SortPlot(3, 0, 100),
            SortPlot(4, 0, null), SortPlot(5, null, 100), SortPlot(6, 99, 50) };
        Equal("3,2,4,1,6,5", SortOrder(plots, WatchedPlotSortColumn.SizePrice, true));
        Equal("1,2,3,4,5,6", SortOrder(plots, WatchedPlotSortColumn.SizePrice, false));
    }

    private static void SortEntrants()
    {
        var plots = new[] { SortPlot(1, entrants: null), SortPlot(2, entrants: 0), SortPlot(3, entrants: 17),
            SortPlot(4, entrants: null), SortPlot(5, entrants: 9) };
        Equal("2,5,3,1,4", SortOrder(plots, WatchedPlotSortColumn.Entrants, true));
        Equal("3,5,2,1,4", SortOrder(plots, WatchedPlotSortColumn.Entrants, false));
        Equal<long?>(null, plots[0].LottoEntries);
        Equal<long?>(0, plots[1].LottoEntries);
    }

    private static void SortStatus()
    {
        var plots = new[] { SortPlot(1, phase: null), SortPlot(2, phase: 3), SortPlot(3, phase: 2), SortPlot(4, phase: 1), SortPlot(5, phase: 99) };
        Equal("4,3,2,1,5", SortOrder(plots, WatchedPlotSortColumn.ReportedStatus, true));
        Equal("2,3,4,1,5", SortOrder(plots, WatchedPlotSortColumn.ReportedStatus, false));
        var historical = WatchFor(SortPlot(6, phase: 1));
        var cache = new Dictionary<int, PaissaHousingWorldData> { [65] = World(plots) };
        var rows = WatchedPlotSorting.Sort(plots.Select(WatchFor).Append(historical), cache, WatchedPlotSortColumn.ReportedStatus, true, Now);
        Equal("4,3,2,1,5,6", string.Join(",", rows.Select(row => row.Watch.Address.Plot)));
        Equal(WatchedPlotAvailability.NotInLatestListings, rows.Last().View.Availability);
        Equal(1, historical.LastKnown!.Listing.LottoPhase);
    }

    private static void SortCacheAge()
    {
        var watches = new[] { Watch(65, "Midgardsormr"), Watch(54, "Faerie"), Watch(40, "Jenova"), Watch(57, "Siren") };
        var cache = new Dictionary<int, PaissaHousingWorldData>
        {
            [65] = World() with { DownloadedAtUtc = Now.AddHours(-2) },
            [54] = World() with { World = new(54, "Faerie", 4, "Aether"), DownloadedAtUtc = Now.AddHours(-1) },
            [40] = World() with { World = new(40, "Jenova", 4, "Aether"), DownloadedAtUtc = Now.AddHours(1) },
        };
        Equal("40,54,65,57", string.Join(",", WatchedPlotSorting.Sort(watches, cache, WatchedPlotSortColumn.CacheAge, true, Now)
            .Select(row => row.Watch.Address.WorldId)));
        Equal("65,54,40,57", string.Join(",", WatchedPlotSorting.Sort(watches, cache, WatchedPlotSortColumn.CacheAge, false, Now)
            .Select(row => row.Watch.Address.WorldId)));
    }

    private sealed class MemoryStore : IWatchedPlotStore
    {
        public WatchedPlotDocument Data = new();
        public bool FailSave, ReadOnly, FailRestore;
        public int Saves;
        public WatchedPlotLoadResult Load() => new(Data, ReadOnly, null, ReadOnly);
        public void Save(WatchedPlotDocument data)
        {
            if (FailSave) throw new IOException();
            Saves++;
            Data = data;
        }
        public void RestoreBackup(WatchedPlotDocument data)
        {
            if (FailRestore) throw new IOException();
            Data = data;
        }
    }
}
