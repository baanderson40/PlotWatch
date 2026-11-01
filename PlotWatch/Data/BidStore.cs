using System.Text.Json;

namespace PlotWatch.Data;

public sealed record BidLoadResult(BidDocument Data, bool ReadOnly = false, string? Error = null, bool CanRestoreBackup = false);

public interface IBidStore
{
    BidLoadResult Load();
    void Save(BidDocument data);
    void RestoreBackup(BidDocument data);
}

/// <summary>Single-client, versioned storage. Never overwrite a file that failed to load implicitly.</summary>
public sealed class BidFileStore(string path) : IBidStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private string BackupPath => path + ".bak";

    public BidLoadResult Load()
    {
        try
        {
            return new(Read(path));
        }
        catch (NewerBidSchemaException)
        {
            return new(new(), true, "Bid file uses a newer schema. Update PlotWatch; existing files have not been changed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            try
            {
                var backup = Read(BackupPath);
                return new(backup, true, "Bid file could not be loaded. Showing backup read-only. Restore backup explicitly; the original will be preserved.", true);
            }
            catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NewerBidSchemaException)
            {
                if (ex is FileNotFoundException or DirectoryNotFoundException &&
                    backupError is FileNotFoundException or DirectoryNotFoundException)
                    return new(new());
                return new(new(), true, "Bid data could not be loaded. Writes are blocked. Preserve bids.json and its .bak file, restore a known-good copy, then reload PlotWatch.");
            }
        }
    }

    private static BidDocument Read(string file)
    {
        var data = JsonSerializer.Deserialize<BidDocument>(File.ReadAllText(file), JsonOptions)
            ?? throw new InvalidDataException("Empty bid document.");
        if (data.Version > BidDocument.CurrentVersion) throw new NewerBidSchemaException();
        if (data.Version < 0) throw new InvalidDataException("Unsupported bid schema.");
        // Version 0 used the same fields; migration adds the explicit version marker.
        data = data with { Version = BidDocument.CurrentVersion };
        if (BidPolicy.ValidateDocument(data) is { } error) throw new InvalidDataException(error);
        return data;
    }

    public void Save(BidDocument data) => Write(data, preserveBackup: false);

    public void RestoreBackup(BidDocument data)
    {
        // Recovery is user-triggered. Keep the unreadable original and the good backup.
        if (File.Exists(path)) File.Copy(path, path + ".preserved-" + Guid.NewGuid().ToString("N"), overwrite: false);
        Write(data, preserveBackup: true);
    }

    private void Write(BidDocument data, bool preserveBackup)
    {
        if (data.Version != BidDocument.CurrentVersion || BidPolicy.ValidateDocument(data) is not null)
            throw new InvalidDataException("Refusing to save invalid bid data.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, data, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path) && !preserveBackup)
                File.Replace(temporary, path, BackupPath);
            else
                File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed class NewerBidSchemaException : Exception;
}
