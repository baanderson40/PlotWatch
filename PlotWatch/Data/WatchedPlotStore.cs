using System.Text.Json;

namespace PlotWatch.Data;

public sealed record WatchedPlotLoadResult(WatchedPlotDocument Data, bool ReadOnly = false, string? Error = null, bool CanRestoreBackup = false);
public interface IWatchedPlotStore
{
    WatchedPlotLoadResult Load();
    void Save(WatchedPlotDocument data);
    void RestoreBackup(WatchedPlotDocument data);
}

/// <summary>Separate, atomic watchlist storage. A backup never silently becomes writable primary data.</summary>
public sealed class WatchedPlotFileStore(string path) : IWatchedPlotStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private string BackupPath => path + ".bak";

    public WatchedPlotLoadResult Load()
    {
        try { return new(Read(path)); }
        catch (NewerSchemaException)
        { return new(new(), true, "Watchlist uses a newer schema. Update PlotWatch; existing files have not been changed."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            try
            {
                return new(Read(BackupPath), true,
                    "Watchlist could not be loaded. Showing backup read-only. Restore explicitly; the original will be preserved.", true);
            }
            catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NewerSchemaException)
            {
                if (error is FileNotFoundException or DirectoryNotFoundException && backupError is FileNotFoundException or DirectoryNotFoundException)
                    return new(new());
                return new(new(), true, "Watchlist could not be loaded. Writes blocked. Preserve watched-plots.json and its .bak file; restore a known-good copy and reload.");
            }
        }
    }

    private static WatchedPlotDocument Read(string file)
    {
        var document = JsonSerializer.Deserialize<WatchedPlotDocument>(File.ReadAllText(file), Options)
            ?? throw new InvalidDataException("Empty watchlist.");
        if (document.Version > WatchedPlotDocument.CurrentVersion) throw new NewerSchemaException();
        if (WatchedPlotPolicy.Validate(document) is { } error) throw new InvalidDataException(error);
        return document;
    }

    public void Save(WatchedPlotDocument data) => Write(data, false);
    public void RestoreBackup(WatchedPlotDocument data)
    {
        if (File.Exists(path)) File.Copy(path, path + ".preserved-" + Guid.NewGuid().ToString("N"), overwrite: false);
        Write(data, true);
    }

    private void Write(WatchedPlotDocument data, bool preserveBackup)
    {
        if (WatchedPlotPolicy.Validate(data) is { } error) throw new InvalidDataException(error);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, data, Options);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path) && !preserveBackup) File.Replace(temporary, path, BackupPath);
            else File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class NewerSchemaException : Exception;
}
