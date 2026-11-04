using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace PlotWatch.Paissa;

public sealed record HousingCacheWriteStats(int Worlds, int Plots, long Bytes);
public sealed record HousingCacheWriteResult(long Generation, bool Success, TimeSpan Duration,
    HousingCacheWriteStats? Stats, string? Error);

/// <summary>
/// Single background writer with one latest pending snapshot. Requests copy only world references;
/// published world records/listings must remain immutable. No UI, game state, or callbacks on worker.
/// </summary>
public sealed class HousingCacheWriter
{
    public static readonly TimeSpan DefaultCoalesceWindow = TimeSpan.FromSeconds(30);
    private sealed record Job(long Generation, PaissaHousingWorldData[] Worlds);
    private readonly object gate = new();
    private readonly Func<IReadOnlyList<PaissaHousingWorldData>, Task<HousingCacheWriteStats>> persist;
    private readonly TimeSpan coalesceWindow;
    private readonly ConcurrentQueue<HousingCacheWriteResult> results = new();
    private Job? pending;
    private Job? failed;
    private long generation;
    private bool active;
    private bool forceFlush;
    private bool completed;
    private CancellationTokenSource? delay;
    private Task<bool> worker = Task.FromResult(true);

    public HousingCacheWriter(string path, TimeSpan? coalesceWindow = null)
        : this(worlds => WriteFileAsync(path, worlds), coalesceWindow ?? DefaultCoalesceWindow) { }

    public HousingCacheWriter(Func<IReadOnlyList<PaissaHousingWorldData>, Task<HousingCacheWriteStats>> persist, TimeSpan coalesceWindow)
    {
        ArgumentNullException.ThrowIfNull(persist);
        if (coalesceWindow < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(coalesceWindow));
        this.persist = persist;
        this.coalesceWindow = coalesceWindow;
    }

    public bool Request(IEnumerable<PaissaHousingWorldData> worlds)
    {
        lock (gate)
        {
            if (completed) return false;
            pending = new(++generation, worlds.ToArray());
            failed = null;
            StartWorker();
            return true;
        }
    }

    public Task<bool> FlushAsync()
    {
        lock (gate) return FlushCore();
    }

    /// <summary>Stop accepting requests and drain latest data; safe to await without the framework thread.</summary>
    public Task<bool> CompleteAsync()
    {
        lock (gate)
        {
            completed = true;
            return FlushCore();
        }
    }

    private Task<bool> FlushCore()
    {
        if (pending is null && failed is not null) { pending = failed; failed = null; }
        if (active || pending is not null)
        {
            forceFlush = true;
            delay?.Cancel();
            StartWorker();
        }
        return worker;
    }

    // gate held. Starting with Task.Run ensures even synchronous serializer/file work is off-thread.
    private void StartWorker()
    {
        if (active || pending is null) return;
        active = true;
        worker = Task.Run(RunAsync);
    }

    private async Task<bool> RunAsync()
    {
        var success = true;
        while (true)
        {
            CancellationTokenSource? wait;
            lock (gate)
            {
                if (pending is null)
                {
                    active = forceFlush = false;
                    return success;
                }
                wait = !forceFlush && coalesceWindow > TimeSpan.Zero ? new CancellationTokenSource() : null;
                delay = wait;
            }
            if (wait is not null)
            {
                try { await Task.Delay(coalesceWindow, wait.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
                finally
                {
                    lock (gate) { if (ReferenceEquals(delay, wait)) delay = null; }
                    wait.Dispose();
                }
            }
            Job job;
            lock (gate)
            {
                job = pending!;
                pending = null;
            }
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var stats = await persist(job.Worlds).ConfigureAwait(false);
                success = true;
                results.Enqueue(new(job.Generation, true, stopwatch.Elapsed, stats, null));
            }
            catch (Exception error)
            {
                success = false;
                lock (gate)
                {
                    // Retry only on a later explicit flush/request; never spin after disk failure.
                    if (job.Generation == generation) failed = job;
                }
                results.Enqueue(new(job.Generation, false, stopwatch.Elapsed, null, error.Message));
            }
        }
    }

    public bool TryDequeueResult(out HousingCacheWriteResult? result) => results.TryDequeue(out result);

    private static async Task<HousingCacheWriteStats> WriteFileAsync(string path, IReadOnlyList<PaissaHousingWorldData> worlds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            long bytes;
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                // Same JSON shape as the existing housing cache. Stream directly, no giant UTF-16 string.
                await JsonSerializer.SerializeAsync(stream, worlds).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                bytes = stream.Length;
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            return new(worlds.Count, worlds.Sum(world => world.Listings.Count), bytes);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
