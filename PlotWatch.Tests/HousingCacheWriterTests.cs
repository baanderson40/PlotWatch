using System.Text.Json;
using PlotWatch.Paissa;

internal static class HousingCacheWriterTests
{
    public static readonly (string Name, Action Test)[] Cases =
    [
        ("background cache writer coalesces pending generations and flushes without waiting", () => Coalesce().GetAwaiter().GetResult()),
        ("background cache writer never overlaps writes or overwrites latest with older data", () => Serialized().GetAwaiter().GetResult()),
        ("background cache writer retries a failed save only on explicit flush", () => RetrySave().GetAwaiter().GetResult()),
        ("background cache writer replaces failed snapshots with newer requests", () => NewerAfterFailure().GetAwaiter().GetResult()),
        ("background cache writer shutdown drains latest data and rejects new requests", () => Shutdown().GetAwaiter().GetResult()),
        ("background cache JSON remains compatible and preserves unknown values", FileCompatibility),
        ("background cache failed serialization preserves primary and removes temporary", FileFailure),
        ("background cache flush without requests does not create a file", EmptyFlush),
    ];
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 17, 0, 0, TimeSpan.Zero);
    private static PaissaHousingWorldData World(int generation)
        => new(new(65, "Midgardsormr", 4, "Aether"), Now.AddMinutes(generation),
            new PaissaPlotListing[] { new(65, 339, "Mist", 4, 10, 5, 11, 0, 3187500, 3, null, null, null,
                null, Now.ToUnixTimeSeconds(), null, null) }, []);
    private static HousingCacheWriteStats Stats(IReadOnlyList<PaissaHousingWorldData> worlds)
        => new(worlds.Count, worlds.Sum(world => world.Listings.Count), 1);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}.");
    }
    private static async Task<bool> Finish(Task<bool> task) => await task.WaitAsync(TimeSpan.FromSeconds(5));

    private static async Task Coalesce()
    {
        var calls = 0;
        IReadOnlyList<PaissaHousingWorldData>? saved = null;
        var caller = Environment.CurrentManagedThreadId;
        var writerThread = caller;
        var writer = new HousingCacheWriter(worlds =>
        {
            calls++;
            writerThread = Environment.CurrentManagedThreadId;
            saved = worlds;
            return Task.FromResult(Stats(worlds));
        }, TimeSpan.FromHours(1));
        var requested = new List<PaissaHousingWorldData>();
        for (var generation = 1; generation <= 100; generation++)
        {
            requested.Clear();
            requested.Add(World(generation));
            Equal(true, writer.Request(requested));
        }
        requested.Clear(); // World-reference collection was copied, not retained.
        Equal(true, await Finish(writer.FlushAsync()));
        Equal(1, calls);
        Equal(World(100).DownloadedAtUtc, saved!.Single().DownloadedAtUtc);
        Equal(false, caller == writerThread);
        Equal(true, writer.TryDequeueResult(out var result));
        Equal(100L, result!.Generation);
        Equal(true, result.Success);
        await Finish(writer.CompleteAsync());
    }

    private static async Task Serialized()
    {
        var entered = Signal();
        var release = Signal();
        var downloads = new List<DateTimeOffset>();
        var active = 0;
        var maximum = 0;
        var writer = new HousingCacheWriter(async worlds =>
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref active));
            try
            {
                downloads.Add(worlds.Single().DownloadedAtUtc);
                if (downloads.Count == 1) { entered.SetResult(); await release.Task; }
                return Stats(worlds);
            }
            finally { Interlocked.Decrement(ref active); }
        }, TimeSpan.Zero);
        writer.Request([World(1)]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        writer.Request([World(2)]);
        writer.Request([World(3)]);
        var flush = writer.FlushAsync();
        Equal(false, flush.IsCompleted);
        release.SetResult();
        Equal(true, await Finish(flush));
        Equal(1, maximum);
        Equal(2, downloads.Count);
        Equal(World(1).DownloadedAtUtc, downloads[0]);
        Equal(World(3).DownloadedAtUtc, downloads[1]);
        await Finish(writer.CompleteAsync());
    }

    private static async Task RetrySave()
    {
        var calls = 0;
        var writer = new HousingCacheWriter(worlds =>
        {
            if (++calls == 1) throw new IOException("disk failure");
            return Task.FromResult(Stats(worlds));
        }, TimeSpan.FromHours(1));
        writer.Request([World(1)]);
        Equal(false, await Finish(writer.FlushAsync()));
        Equal(1, calls);
        Equal(true, writer.TryDequeueResult(out var failed));
        Equal(false, failed!.Success);
        Equal(true, await Finish(writer.FlushAsync()));
        Equal(2, calls);
        Equal(true, writer.TryDequeueResult(out var recovered));
        Equal(1L, recovered!.Generation);
        Equal(true, recovered.Success);
        await Finish(writer.CompleteAsync());
    }

    private static async Task NewerAfterFailure()
    {
        var calls = 0;
        DateTimeOffset saved = default;
        var writer = new HousingCacheWriter(worlds =>
        {
            if (++calls == 1) throw new IOException();
            saved = worlds.Single().DownloadedAtUtc;
            return Task.FromResult(Stats(worlds));
        }, TimeSpan.FromHours(1));
        writer.Request([World(1)]);
        Equal(false, await Finish(writer.FlushAsync()));
        writer.Request([World(2)]);
        Equal(true, await Finish(writer.FlushAsync()));
        Equal(World(2).DownloadedAtUtc, saved);
        Equal(2, calls);
        await Finish(writer.CompleteAsync());
    }

    private static async Task Shutdown()
    {
        var entered = Signal();
        var release = Signal();
        DateTimeOffset saved = default;
        var writer = new HousingCacheWriter(async worlds =>
        {
            if (saved == default) { entered.TrySetResult(); await release.Task; }
            saved = worlds.Single().DownloadedAtUtc;
            return Stats(worlds);
        }, TimeSpan.Zero);
        writer.Request([World(1)]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        writer.Request([World(2)]);
        var shutdown = writer.CompleteAsync();
        Equal(false, shutdown.IsCompleted);
        Equal(false, writer.Request([World(3)]));
        release.SetResult();
        Equal(true, await Finish(shutdown));
        Equal(World(2).DownloadedAtUtc, saved);
    }

    private static void WithFile(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "PlotWatch.CacheWriterTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { test(Path.Combine(directory, "housing-cache.json")); }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void FileCompatibility() => WithFile(path =>
    {
        var writer = new HousingCacheWriter(path, TimeSpan.FromHours(1));
        writer.Request([World(1)]);
        Equal(true, Finish(writer.FlushAsync()).GetAwaiter().GetResult());
        var entries = JsonSerializer.Deserialize<List<LegacyCacheEntry>>(File.ReadAllText(path))!;
        Equal(65, entries.Single().World.Id);
        Equal(World(1).DownloadedAtUtc, entries.Single().DownloadedAtUtc);
        Equal<long?>(null, entries.Single().Listings.Single().LottoEntries);
        Equal<int?>(null, entries.Single().Listings.Single().LottoPhase);
        var next = World(2) with { Listings = new[] { World(2).Listings.Single() with { LottoEntries = 0 } } };
        writer.Request([next]);
        Equal(true, Finish(writer.CompleteAsync()).GetAwaiter().GetResult());
        Equal<long?>(0, JsonSerializer.Deserialize<List<LegacyCacheEntry>>(File.ReadAllText(path))!.Single().Listings.Single().LottoEntries);
        Equal(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*").Length);
    });

    private static void FileFailure() => WithFile(path =>
    {
        var writer = new HousingCacheWriter(path, TimeSpan.FromHours(1));
        writer.Request([World(1)]);
        Equal(true, Finish(writer.FlushAsync()).GetAwaiter().GetResult());
        var prior = File.ReadAllText(path);
        writer.Request([World(2) with { Listings = new[] { World(2).Listings.Single() with { LastUpdatedTime = double.NaN } } }]);
        Equal(false, Finish(writer.CompleteAsync()).GetAwaiter().GetResult());
        Equal(prior, File.ReadAllText(path));
        Equal(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*").Length);
    });

    private static void EmptyFlush() => WithFile(path =>
    {
        var writer = new HousingCacheWriter(path);
        Equal(true, Finish(writer.CompleteAsync()).GetAwaiter().GetResult());
        Equal(false, File.Exists(path));
        Equal(false, writer.Request([World(1)]));
    });

    private sealed record LegacyCacheEntry(PaissaWorld World, DateTimeOffset DownloadedAtUtc, PaissaPlotListing[] Listings, string[]? Warnings);
}
