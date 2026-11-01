using System.Text.Json;
using PlotWatch.Data;
using PlotWatch.Lottery;

internal static class BidTests
{
    public static readonly (string Name, Action Test)[] Cases =
    [
        ("bid characters discover once and update metadata", CharacterDiscovery),
        ("bid exclusion preserves records and stops new capture", CharacterExclusion),
        ("bid character removal permits later rediscovery", CharacterRemoval),
        ("bid remove-and-exclude persists suppression", CharacterRemoveAndExclude),
        ("bid validation rejects invalid addresses and dates", RecordValidation),
        ("bid unknown fields stay unknown without cycle evidence", UnknownFields),
        ("bid duplicate warning requires deliberate override", Duplicates),
        ("bid edit preserves identity and allows explicit outcomes", EditAndOutcomes),
        ("bid game-observed deadlines override manual estimates", DeadlineEvidence),
        ("bid outcomes never change from schedule or character switches", NoInferredOutcome),
        ("bid save failure keeps previous data and remains retryable", SaveFailure),
        ("bid file round-trip preserves records and exclusions", FileRoundTrip),
        ("bid file creates recoverable previous-save backup", FileBackup),
        ("corrupt bid primary loads backup read-only", CorruptPrimary),
        ("bid recovery preserves original before restoring backup", BackupRestore),
        ("corrupt bid files block writes without data loss", CorruptFiles),
        ("newer bid schema refuses overwrite and downgrade recovery", NewerSchema),
        ("legacy bid schema migrates without writing during load", LegacySchema),
        ("invalid bid document blocks writes", InvalidDocument),
        ("bid backup-only install requires explicit restoration", BackupOnly),
        ("bid file save failure cannot publish pending mutations", DiskSaveFailure),
        ("bid restore failure keeps read-only recovery state", RestoreFailure),
        ("bid deletion persists across reload", DeleteRecord),
        ("bid entry logs successes and rejections without character identifiers", EntryLogging),
    ];

    private static readonly DateTimeOffset Anchor = LotterySchedule.ApplicationAnchorUtc;
    private static TrackedCharacter Character(ulong id = 100) => new(id, "Test Character", 73, "Adamantoise");
    private static BidRecord Bid() => new()
    {
        Id = Guid.NewGuid(), PurchaseType = BidPurchaseType.Personal,
        CharacterContentId = 100, WorldId = 73, WorldName = "Adamantoise", DistrictId = 339,
        Ward = 1, Plot = 2, CycleId = 0, EnteredAt = new(Anchor.AddDays(1), BidFieldSource.Manual),
    };
    private static BidTracker Tracker(MemoryBidStore? store = null)
    {
        var tracker = new BidTracker(store ?? new());
        tracker.ObserveCharacter(Character());
        return tracker;
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; got {actual}.");
    }

    private static void CharacterDiscovery()
    {
        var store = new MemoryBidStore();
        var tracker = Tracker(store);
        Equal(1, store.Saves);
        Equal(false, tracker.ObserveCharacter(Character()));
        Equal(1, store.Saves);
        Equal(true, tracker.ObserveCharacter(Character() with { Name = "Renamed Character" }));
        Equal(1, tracker.Data.Characters.Count);
        Equal(true, tracker.ObserveCharacter(Character(101)));
        Equal(2, tracker.Data.Characters.Count);
        Equal(false, tracker.ObserveCharacter(null));
        Equal<ulong?>(null, tracker.CurrentCharacterId);
        Equal(false, tracker.ObserveCharacter(Character(0)));
    }

    private static void CharacterExclusion()
    {
        var tracker = Tracker();
        var bid = Bid();
        Equal(true, tracker.SaveBid(bid));
        Equal(true, tracker.SetExcluded(100, true));
        Equal(false, tracker.ObserveCharacter(Character() with { Name = "Ignored rename" }));
        Equal(1, tracker.Data.Bids.Count);
        Equal(false, tracker.SaveBid(bid with { Id = Guid.NewGuid(), Plot = 3 }));
        Equal(true, tracker.SaveBid(bid with { Outcome = BidOutcome.Won }));
        Equal(true, tracker.SetExcluded(100, false));
        Equal(true, tracker.SaveBid(bid with { Id = Guid.NewGuid(), Plot = 3 }));
    }

    private static void CharacterRemoval()
    {
        var tracker = Tracker();
        tracker.SaveBid(Bid());
        Equal(true, tracker.RemoveCharacter(100, false));
        Equal(0, tracker.Data.Characters.Count);
        Equal(0, tracker.Data.Bids.Count);
        Equal(false, tracker.ObserveCharacter(Character()));
        tracker.ObserveCharacter(null);
        Equal(true, tracker.ObserveCharacter(Character()));
    }

    private static void CharacterRemoveAndExclude()
    {
        var store = new MemoryBidStore();
        var tracker = Tracker(store);
        tracker.SaveBid(Bid());
        Equal(true, tracker.RemoveCharacter(100, true));
        var reloaded = new BidTracker(store);
        Equal(false, reloaded.ObserveCharacter(Character()));
        Equal(0, reloaded.Data.Characters.Count);
        Equal(true, reloaded.SetExcluded(100, false));
        Equal(true, reloaded.ObserveCharacter(Character()));
        Equal(0, reloaded.Data.Bids.Count);
        Equal(true, tracker.SetExcluded(100, false));
        Equal(true, tracker.ObserveCharacter(Character()));
    }

    private static void RecordValidation()
    {
        var bid = Bid();
        Equal<string?>(null, BidPolicy.Validate(bid));
        foreach (var invalid in new[]
        {
            bid with { Ward = 0 }, bid with { Ward = 31 }, bid with { Plot = 61 }, bid with { Plot = 0 },
            bid with { TicketNumber = 0 }, bid with { TicketNumber = -1 }, bid with { Size = 3 },
            bid with { WorldId = 0 }, bid with { WorldName = "" }, bid with { DistrictId = 999 },
            bid with { Outcome = (BidOutcome)999 }, bid with { PurchaseType = (BidPurchaseType)999 },
            bid with { CycleId = long.MaxValue }, bid with { CycleId = long.MinValue },
            bid with { EnteredAt = new(Anchor.AddDays(10), BidFieldSource.Manual) },
            bid with { EnteredAt = new(Anchor.AddDays(6), BidFieldSource.Manual) },
            bid with { ResultsAt = new(Anchor.AddDays(10), BidFieldSource.Manual) },
            bid with { ClaimDeadline = new(Anchor, BidFieldSource.Manual) },
            bid with { OutcomeSource = BidFieldSource.Calculated },
            bid with { EnteredAt = new(Anchor, BidFieldSource.Calculated) },
        }) Equal(true, BidPolicy.Validate(invalid) is not null);
        Equal<string?>(null, BidPolicy.Validate(bid with { Ward = 30, Plot = 60, Size = null, TicketNumber = null }));
    }

    private static void UnknownFields()
    {
        var bid = Bid() with { CycleId = null, EnteredAt = null, TicketNumber = null, Size = null };
        Equal<string?>(null, BidPolicy.Validate(bid));
        Equal<BidTimestamp?>(null, BidPolicy.GetDates(bid).Results);
        Equal<BidTimestamp?>(null, BidPolicy.GetDates(bid).Claim);
        var dates = BidPolicy.GetDates(bid with { CycleId = 0 });
        Equal(Anchor.AddDays(5), dates.Results!.Value);
        Equal(Anchor.AddDays(9), dates.Claim!.Value);
        Equal(BidFieldSource.Calculated, dates.Results.Source);
    }

    private static void Duplicates()
    {
        var tracker = Tracker();
        var bid = Bid();
        tracker.SaveBid(bid);
        var duplicate = bid with { Id = Guid.NewGuid() };
        Equal(false, tracker.SaveBid(duplicate));
        Equal(1, tracker.Data.Bids.Count);
        Equal(true, tracker.SaveBid(duplicate, allowDuplicate: true));
        Equal(2, tracker.Data.Bids.Count);
        Equal(false, BidPolicy.IsDuplicate(bid, bid));
        Equal(false, BidPolicy.IsDuplicate(bid, duplicate with { PurchaseType = BidPurchaseType.FreeCompany }));
        Equal(false, BidPolicy.IsDuplicate(bid, duplicate with { CycleId = 1 }));
        Equal(false, BidPolicy.IsDuplicate(bid, duplicate with { CharacterContentId = 101 }));
    }

    private static void EditAndOutcomes()
    {
        var tracker = Tracker();
        var bid = Bid();
        tracker.SaveBid(bid);
        foreach (var outcome in Enum.GetValues<BidOutcome>())
        {
            Equal(true, tracker.SaveBid(bid with { Outcome = outcome, TicketNumber = 42 }));
            Equal(1, tracker.Data.Bids.Count);
            Equal(bid.Id, tracker.Data.Bids[0].Id);
            Equal(outcome, tracker.Data.Bids[0].Outcome);
        }
        Equal(true, BidPolicy.IsActive(bid with { Outcome = BidOutcome.Won }));
        Equal(false, BidPolicy.IsActive(bid with { Outcome = BidOutcome.Lost }));
        Equal(false, BidPolicy.IsActive(bid with { Outcome = BidOutcome.Claimed }));
    }

    private static void DeadlineEvidence()
    {
        var tracker = Tracker();
        var bid = Bid() with
        {
            ResultsAt = new(Anchor.AddDays(5).AddMinutes(1), BidFieldSource.GameObserved),
            ClaimDeadline = new(Anchor.AddDays(9).AddMinutes(1), BidFieldSource.GameObserved),
        };
        tracker.SaveBid(bid);
        Equal(true, tracker.SaveBid(bid with
        {
            ResultsAt = new(Anchor.AddDays(5), BidFieldSource.Manual), ClaimDeadline = null,
        }));
        Equal(bid.ResultsAt, tracker.Data.Bids[0].ResultsAt);
        Equal(bid.ClaimDeadline, tracker.Data.Bids[0].ClaimDeadline);
        Equal(BidFieldSource.GameObserved, BidPolicy.GetDates(tracker.Data.Bids[0]).Claim!.Source);
        var revised = bid with { ClaimDeadline = new(Anchor.AddDays(9).AddMinutes(2), BidFieldSource.GameObserved) };
        Equal(true, tracker.SaveBid(revised));
        Equal(revised.ClaimDeadline, tracker.Data.Bids[0].ClaimDeadline);
    }

    private static void NoInferredOutcome()
    {
        var tracker = Tracker();
        tracker.SaveBid(Bid());
        foreach (var days in new[] { 0, 5, 9, 90 })
        {
            _ = LotterySchedule.GetSnapshot(Anchor.AddDays(days));
            _ = BidPolicy.GetDates(tracker.Data.Bids[0]);
            tracker.ObserveCharacter(Character(101));
            tracker.ObserveCharacter(Character());
            Equal(BidOutcome.Pending, tracker.Data.Bids[0].Outcome);
        }
    }

    private static void SaveFailure()
    {
        var store = new MemoryBidStore();
        var tracker = Tracker(store);
        var bid = Bid();
        tracker.SaveBid(bid);
        var before = tracker.Data;
        store.FailSave = true;
        Equal(false, tracker.SaveBid(bid with { Outcome = BidOutcome.Won }));
        Equal(before, tracker.Data);
        Equal(true, tracker.Error is not null);
        Equal(false, tracker.RemoveCharacter(100, true));
        Equal(before, tracker.Data);
        store.FailSave = false;
        Equal(true, tracker.SaveBid(bid with { Outcome = BidOutcome.Won }));
        Equal(BidOutcome.Won, tracker.Data.Bids[0].Outcome);
    }

    private static void WithFile(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "PlotWatch.Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { test(Path.Combine(directory, "bids.json")); }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static BidTracker FileTracker(string path)
    {
        var tracker = new BidTracker(new BidFileStore(path));
        tracker.ObserveCharacter(Character());
        return tracker;
    }

    private static void FileRoundTrip() => WithFile(path =>
    {
        var tracker = FileTracker(path);
        var bid = Bid() with { Outcome = BidOutcome.Won, TicketNumber = 42 };
        tracker.SaveBid(bid);
        tracker.SetExcluded(100, true);
        var reloaded = new BidTracker(new BidFileStore(path));
        Equal(false, reloaded.IsReadOnly);
        Equal(bid, reloaded.Data.Bids.Single());
        Equal(Character(), reloaded.Data.Characters.Single());
        Equal(100UL, reloaded.Data.ExcludedCharacters.Single());
        Equal(BidDocument.CurrentVersion, reloaded.Data.Version);
    });

    private static void FileBackup() => WithFile(path =>
    {
        var tracker = FileTracker(path);
        var bid = Bid();
        tracker.SaveBid(bid);
        tracker.SaveBid(bid with { Outcome = BidOutcome.Won });
        Equal(true, File.Exists(path + ".bak"));
        var backup = JsonSerializer.Deserialize<BidDocument>(File.ReadAllText(path + ".bak"))!;
        Equal(BidOutcome.Pending, backup.Bids.Single().Outcome);
        Equal(BidOutcome.Won, new BidTracker(new BidFileStore(path)).Data.Bids.Single().Outcome);
    });

    private static void CorruptPrimary() => WithFile(path =>
    {
        var tracker = FileTracker(path);
        var bid = Bid();
        tracker.SaveBid(bid);
        tracker.SaveBid(bid with { Outcome = BidOutcome.Won });
        File.WriteAllText(path, "broken");
        var recovered = new BidTracker(new BidFileStore(path));
        Equal(true, recovered.IsReadOnly);
        Equal(true, recovered.CanRestoreBackup);
        Equal(BidOutcome.Pending, recovered.Data.Bids.Single().Outcome);
        Equal(false, recovered.SaveBid(bid));
        Equal("broken", File.ReadAllText(path));
    });

    private static void BackupRestore() => WithFile(path =>
    {
        var tracker = FileTracker(path);
        tracker.SaveBid(Bid());
        tracker.SetExcluded(101, true);
        File.WriteAllText(path, "broken-original");
        var recovered = new BidTracker(new BidFileStore(path));
        Equal(true, recovered.RestoreBackup());
        Equal(false, recovered.IsReadOnly);
        Equal(1, new BidTracker(new BidFileStore(path)).Data.Bids.Count);
        var preserved = Directory.GetFiles(Path.GetDirectoryName(path)!, "bids.json.preserved-*");
        Equal(1, preserved.Length);
        Equal("broken-original", File.ReadAllText(preserved[0]));
        Equal(true, File.Exists(path + ".bak"));
    });

    private static void CorruptFiles() => WithFile(path =>
    {
        File.WriteAllText(path, "corrupt-main");
        File.WriteAllText(path + ".bak", "corrupt-backup");
        var tracker = new BidTracker(new BidFileStore(path));
        Equal(true, tracker.IsReadOnly);
        Equal(false, tracker.CanRestoreBackup);
        Equal(false, tracker.ObserveCharacter(Character()));
        Equal(false, tracker.SetExcluded(100, true));
        Equal("corrupt-main", File.ReadAllText(path));
        Equal("corrupt-backup", File.ReadAllText(path + ".bak"));
    });

    private static void NewerSchema() => WithFile(path =>
    {
        var text = "{\"Version\":999}";
        File.WriteAllText(path, text);
        File.WriteAllText(path + ".bak", JsonSerializer.Serialize(new BidDocument()));
        var tracker = new BidTracker(new BidFileStore(path));
        Equal(true, tracker.IsReadOnly);
        Equal(false, tracker.CanRestoreBackup);
        Equal(false, tracker.RestoreBackup());
        Equal(text, File.ReadAllText(path));
    });

    private static void LegacySchema() => WithFile(path =>
    {
        var legacy = new BidDocument { Version = 0, Characters = [Character()], Bids = [Bid()] };
        var text = JsonSerializer.Serialize(legacy);
        File.WriteAllText(path, text);
        var tracker = new BidTracker(new BidFileStore(path));
        Equal(false, tracker.IsReadOnly);
        Equal(BidDocument.CurrentVersion, tracker.Data.Version);
        Equal(legacy.Bids.Single(), tracker.Data.Bids.Single());
        Equal(text, File.ReadAllText(path));
        Equal(true, tracker.SetExcluded(100, true));
        Equal(BidDocument.CurrentVersion, JsonSerializer.Deserialize<BidDocument>(File.ReadAllText(path))!.Version);
    });

    private static void InvalidDocument() => WithFile(path =>
    {
        foreach (var data in new[]
        {
            new BidDocument { Characters = null! },
            new BidDocument { Characters = [Character(), Character()] },
            new BidDocument { Bids = [Bid()] },
            new BidDocument { Characters = [Character()], Bids = [Bid() with { Ward = 99 }] },
            new BidDocument { Characters = [Character()], Bids = [Bid() with { Id = Guid.Empty }] },
        })
        {
            var text = JsonSerializer.Serialize(data);
            File.WriteAllText(path, text);
            var tracker = new BidTracker(new BidFileStore(path));
            Equal(true, tracker.IsReadOnly);
            Equal(false, tracker.ObserveCharacter(Character()));
            Equal(text, File.ReadAllText(path));
        }
    });

    private static void BackupOnly() => WithFile(path =>
    {
        File.WriteAllText(path + ".bak", JsonSerializer.Serialize(new BidDocument { Characters = [Character()] }));
        var tracker = new BidTracker(new BidFileStore(path));
        Equal(true, tracker.IsReadOnly);
        Equal(true, tracker.CanRestoreBackup);
        Equal(false, File.Exists(path));
        Equal(true, tracker.RestoreBackup());
        Equal(true, File.Exists(path));
    });

    private static void DiskSaveFailure() => WithFile(path =>
    {
        var tracker = new BidTracker(new BidFileStore(path));
        Directory.CreateDirectory(path);
        Equal(false, tracker.ObserveCharacter(Character()));
        Equal(0, tracker.Data.Characters.Count);
        Equal(true, tracker.Error is not null);
        Equal(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*").Length);
    });

    private static void RestoreFailure()
    {
        var store = new MemoryBidStore { InitialReadOnly = true, FailRestore = true };
        var tracker = new BidTracker(store);
        Equal(false, tracker.RestoreBackup());
        Equal(true, tracker.IsReadOnly);
        Equal(true, tracker.CanRestoreBackup);
        store.FailRestore = false;
        Equal(true, tracker.RestoreBackup());
        Equal(false, tracker.IsReadOnly);
    }

    private static void DeleteRecord() => WithFile(path =>
    {
        var tracker = FileTracker(path);
        var bid = Bid();
        tracker.SaveBid(bid);
        Equal(true, tracker.DeleteBid(bid.Id));
        Equal(0, new BidTracker(new BidFileStore(path)).Data.Bids.Count);
    });

    private static void EntryLogging()
    {
        const ulong privateId = 887766554433221100;
        var logs = new List<(bool Success, string Message)>();
        var store = new MemoryBidStore();
        var tracker = new BidTracker(store, (success, message) => logs.Add((success, message)));
        tracker.ObserveCharacter(Character(privateId));
        Equal(0, logs.Count);
        var bid = Bid() with { CharacterContentId = privateId };
        Equal(true, tracker.SaveBid(bid));
        Equal(true, logs[^1].Success);
        Equal(true, logs[^1].Message.Contains("added"));
        Equal(true, logs[^1].Message.Contains("ward=1; plot=2"));
        Equal(true, logs[^1].Message.Contains("Calculated"));
        Equal(true, tracker.SaveBid(bid with { Outcome = BidOutcome.Won }));
        Equal(true, logs[^1].Message.Contains("updated"));
        Equal(false, tracker.SaveBid(bid with { Id = Guid.NewGuid() }));
        Equal(false, logs[^1].Success);
        Equal(false, tracker.SaveBid(bid with { Ward = 0 }));
        Equal(false, logs[^1].Success);
        store.FailSave = true;
        Equal(false, tracker.SaveBid(bid));
        Equal(false, logs[^1].Success);
        Equal(true, logs[^1].Message.Contains("save failed"));
        Equal(5, logs.Count);
        Equal(false, logs.Any(item => item.Message.Contains(privateId.ToString())));
        var blocked = new BidTracker(new MemoryBidStore { InitialReadOnly = true }, (success, message) => logs.Add((success, message)));
        Equal(false, blocked.SaveBid(bid));
        Equal(false, logs[^1].Success);
        Equal(true, logs[^1].Message.Contains("read-only"));
    }

    private sealed class MemoryBidStore : IBidStore
    {
        public BidDocument Data = new();
        public bool FailSave;
        public bool FailRestore;
        public bool InitialReadOnly;
        public int Saves;
        public BidLoadResult Load() => new(Data, InitialReadOnly, InitialReadOnly ? "Recovery required" : null, InitialReadOnly);
        public void Save(BidDocument data)
        {
            if (FailSave) throw new IOException("Simulated disk failure");
            Data = data;
            Saves++;
        }
        public void RestoreBackup(BidDocument data)
        {
            if (FailRestore) throw new IOException("Simulated restore failure");
            Save(data);
        }
    }
}
