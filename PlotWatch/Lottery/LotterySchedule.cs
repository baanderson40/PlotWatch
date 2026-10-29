using System.Globalization;

namespace PlotWatch.Lottery;

public enum LotteryPhase
{
    Application,
    Results,
}

public sealed record LotteryPeriodOccurrence(
    LotteryPhase Phase,
    bool IsActive,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    DateTimeOffset NextAtUtc);

public sealed record LotteryScheduleSnapshot(
    DateTimeOffset NowUtc,
    long CycleId,
    LotteryPhase CurrentPhase,
    DateTimeOffset CurrentPhaseEndsAtUtc,
    TimeSpan Remaining,
    LotteryPeriodOccurrence Application,
    LotteryPeriodOccurrence Results);

/// <summary>Pure FFXIV housing-lottery cycle calculations.</summary>
public static class LotterySchedule
{
    public static readonly TimeSpan ApplicationLength = TimeSpan.FromDays(5);
    public static readonly TimeSpan ResultsLength = TimeSpan.FromDays(4);
    public static readonly TimeSpan CycleLength = ApplicationLength + ResultsLength;
    public static readonly DateTimeOffset ApplicationAnchorUtc = new(2026, 11, 1, 15, 0, 0, TimeSpan.Zero);

    public const string ApplicationExplanation = "During this time, you may place an entry into the housing lottery.";
    public const string ResultsExplanation = "During this time, you may accept a winning bid.";

    public static LotteryScheduleSnapshot GetSnapshot(DateTimeOffset now)
    {
        var nowUtc = now.ToUniversalTime();
        var elapsedTicks = (nowUtc - ApplicationAnchorUtc).Ticks;
        var cycleId = (long)Math.Floor(elapsedTicks / (double)CycleLength.Ticks);
        var cycleStart = ApplicationAnchorUtc.AddTicks(cycleId * CycleLength.Ticks);
        var resultsStart = cycleStart + ApplicationLength;
        var applicationEnd = resultsStart;
        var cycleEnd = cycleStart + CycleLength;
        var applicationIsActive = nowUtc < applicationEnd;
        var currentPhase = applicationIsActive ? LotteryPhase.Application : LotteryPhase.Results;
        var currentPhaseEnd = applicationIsActive ? applicationEnd : cycleEnd;

        var applicationStart = applicationIsActive ? cycleStart : cycleStart + CycleLength;
        var displayedResultsStart = resultsStart;

        return new LotteryScheduleSnapshot(
            nowUtc,
            cycleId,
            currentPhase,
            currentPhaseEnd,
            currentPhaseEnd - nowUtc,
            new LotteryPeriodOccurrence(
                LotteryPhase.Application,
                applicationIsActive,
                applicationStart,
                applicationStart + ApplicationLength,
                applicationStart + CycleLength),
            new LotteryPeriodOccurrence(
                LotteryPhase.Results,
                !applicationIsActive,
                displayedResultsStart,
                displayedResultsStart + ResultsLength,
                displayedResultsStart + CycleLength));
    }

    public static string FormatRemaining(TimeSpan remaining)
    {
        var seconds = Math.Max(0, (long)Math.Ceiling(remaining.TotalSeconds));
        var days = seconds / 86_400;
        var hours = seconds / 3_600 % 24;
        var minutes = seconds / 60 % 60;
        var remainder = seconds % 60;
        return days > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}d {1:00}:{2:00}:{3:00}", days, hours, minutes, remainder)
            : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", hours, minutes, remainder);
    }

    public static string FormatTimestamp(DateTimeOffset timestampUtc, bool useUtc, TimeZoneInfo localTimeZone)
    {
        var displayTime = useUtc
            ? timestampUtc.ToUniversalTime()
            : TimeZoneInfo.ConvertTime(timestampUtc, localTimeZone);
        return displayTime.ToString("ddd, MMM dd, yyyy h:mm:ss tt 'UTC'zzz", CultureInfo.InvariantCulture);
    }

    public static string GetDisplayZoneLabel(bool useUtc, TimeZoneInfo localTimeZone, DateTimeOffset nowUtc)
    {
        if (useUtc)
            return "UTC";

        var offset = localTimeZone.GetUtcOffset(nowUtc.UtcDateTime);
        var sign = offset < TimeSpan.Zero ? "−" : "+";
        return $"Local time (UTC{sign}{offset.Duration():hh\\:mm})";
    }
}
